using NAudio.Dsp;
using NAudio.Wave;

namespace Stramp.Audio.Dsp;

/// <summary>
/// Interleaved, per-channel graphic EQ: a bank of peaking biquads, one per band, each sized to
/// the gap it owns. Because neighbouring bells overlap, the filters are not driven straight off
/// the sliders — <see cref="SolveBandGains"/> first works out the gains whose combined response
/// passes through the requested level at every band centre, so a slider moves its own part of
/// the spectrum and leaves the rest where it was.
///
/// Boosts are real boosts relative to one another: raising one band does not quieten the rest
/// of the spectrum. Gain and centre-frequency changes crossfade rather than snap.
///
/// The bank does take back its own peak, though. Modern masters arrive at full scale, so any
/// boost on top has nowhere to go: measured with a +2 dB band on a normal track, this stage was
/// handing on a signal at +2.56 dBFS. What that costs depends on what follows and both answers
/// were bad -- with the enhancements off it reached the endpoint and was clipped there, and with
/// them on it drove FxSound's limiter into continuous heavy reduction, which modulates the gain
/// of everything else and is audible as distortion.
///
/// So the bank is attenuated by the peak of its own combined response, computed across the
/// spectrum rather than assumed from the largest slider, since overlapping bells add. A flat
/// bank attenuates by nothing. This is not the old preamp that quietened the track whenever any
/// band was raised: that one scaled by the largest boost whether or not the response actually
/// reached it, so a boost bought nothing but a quieter mix. Here the shape the user drew is
/// preserved exactly and only the part that would have gone past full scale is given up.
/// </summary>
internal sealed class EqualizerSampleProvider : ISampleProvider
{
    /// <summary>Gain difference below which a band counts as unchanged / flat.</summary>
    private const double GainEpsilon = 1e-9;

    /// <summary>Centre-frequency difference below which a band counts as unmoved.</summary>
    private const float FrequencyEpsilon = 1e-3f;

    /// <summary>Length of the crossfade that retunes a band after a slider moves.</summary>
    private const float RetuneSeconds = 0.02f;

    /// <summary>
    /// How close the smoothed headroom has to be to its target before it is treated as settled.
    /// Well below a hundredth of a decibel, so the step at the end of a ramp is inaudible.
    /// </summary>
    private const float HeadroomEpsilon = 1e-4f;

    /// <summary>
    /// Bands centred above this fraction of the sample rate are left flat: a peaking biquad
    /// that close to Nyquist is too warped to be the band it claims to be.
    /// </summary>
    private const float MaxCentreFrequencyRatio = 0.45f;

    /// <summary>Lowest centre frequency a band may be placed at.</summary>
    private const float MinCentreFrequency = 10;

    /// <summary>
    /// Smallest ratio allowed between one band's centre and the next. Bands are kept strictly
    /// ascending so band <i>n</i> on screen is always band <i>n</i> in the bank, but the margin
    /// is deliberately tiny — some presets really do put two bands a sixth of an octave apart.
    /// </summary>
    private const float MinBandSpacingRatio = 1.01f;

    /// <summary>Refinement passes used to linearise the band-interaction solve.</summary>
    private const int SolverPasses = 3;

    /// <summary>
    /// Points the combined response is sampled at when measuring its peak. Logarithmic over the
    /// whole band, so roughly thirty per octave -- far finer than the bells, whose peaks are
    /// broad enough that nothing sharp hides between two points.
    /// </summary>
    private const int HeadroomGridPoints = 320;

    /// <summary>
    /// Ridge term on the interaction solve. Neighbouring bells are nearly collinear, so an
    /// undamped fit answers extreme slider combinations with enormous opposing gains; this
    /// costs about 0.02 dB of accuracy at the band centres and removes that failure mode.
    /// </summary>
    private const double SolverDamping = 0.0005;

    /// <summary>Filter gain past which the interaction solve is treated as unusable.</summary>
    private const double MaxSolvedGainDb = 60;

    /// <summary>
    /// How far each band's bell is widened beyond the gap it owns. Wider bells overlap more,
    /// which the solve below compensates for at the centres and which fills in the response
    /// between them; past roughly 2x the bands become collinear and the solve falls apart.
    /// </summary>
    private const double BandwidthScale = 1.5;

    private readonly ISampleProvider _source;
    private readonly int _crossfadeSamples;
    private readonly object _settingsGate = new();
    private BandPlan? _pendingPlan;
    private bool _resetRequested;

    // Touched only by the audio thread once construction is done.
    private BandPlan _activePlan;

    /// <summary>
    /// Headroom actually in force, which chases the active plan's rather than jumping to it.
    /// A slider that changes the bank's peak changes this too, and stepping a gain mid-stream
    /// is a click; it rides the same short ramp the band crossfade uses.
    /// </summary>
    private float _headroom = 1;

    /// <summary>Per-sample coefficient of that ramp.</summary>
    private float _headroomRate;
    private CrossfadingBiQuadFilter[][]? _filters;
    private int _tailSamples;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public EqualizerSampleProvider(ISampleProvider source, IReadOnlyList<float> frequencies)
    {
        ArgumentNullException.ThrowIfNull(frequencies);
        _source = source;
        _crossfadeSamples = Math.Max(1, (int)(RetuneSeconds * WaveFormat.SampleRate));
        _headroomRate = 1f / _crossfadeSamples;
        _activePlan = CreatePlan(frequencies, new double[frequencies.Count], enabled: false);
    }

    public int Read(Span<float> buffer)
    {
        var read = _source.Read(buffer);

        if (!RefreshFilters() || _filters is null)
            return read;

        var channels = WaveFormat.Channels;
        var target = _activePlan.Headroom;
        for (var frame = 0; frame + channels <= read; frame += channels)
        {
            // Chase the plan's headroom rather than stepping to it. Once it has arrived the
            // arithmetic is skipped, so a settled bank costs one multiply per sample.
            if (MathF.Abs(_headroom - target) > HeadroomEpsilon)
                _headroom += (target - _headroom) * _headroomRate;
            else
                _headroom = target;

            for (var channel = 0; channel < channels; channel++)
            {
                var sample = buffer[frame + channel];
                foreach (var filter in _filters[channel])
                    sample = filter.Transform(sample);

                // A bad sample from the decoder can latch in the feedback path; flush the
                // delay lines rather than play out the result of a diverged filter.
                if (!float.IsFinite(sample))
                {
                    ResetFilterState();
                    sample = 0;
                }

                buffer[frame + channel] = sample * _headroom;
            }
        }

        if (_tailSamples > 0)
            _tailSamples = Math.Max(0, _tailSamples - read);

        return read;
    }

    /// <summary>
    /// Stages a new band layout. Everything expensive — sanitising the frequencies, sizing each
    /// band's Q, and solving the interaction between them — happens here on the caller's thread,
    /// so the audio thread only ever picks up a finished plan.
    /// </summary>
    public void Update(IReadOnlyList<float> centreFrequencies, IReadOnlyList<double> gainsDb, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(centreFrequencies);
        ArgumentNullException.ThrowIfNull(gainsDb);

        var plan = CreatePlan(centreFrequencies, gainsDb, enabled);
        lock (_settingsGate)
            _pendingPlan = plan;
    }

    public void Reset()
    {
        lock (_settingsGate)
            _resetRequested = true;
    }

    private BandPlan CreatePlan(IReadOnlyList<float> centreFrequencies, IReadOnlyList<double> gainsDb, bool enabled)
    {
        var frequencies = SanitiseFrequencies(centreFrequencies);
        var qFactors = ComputeQFactors(frequencies);

        var target = new double[frequencies.Length];
        if (enabled)
            for (var i = 0; i < Math.Min(target.Length, gainsDb.Count); i++)
                target[i] = Math.Clamp(gainsDb[i], -20, 20);

        // "Off" is a flat bank rather than a bypass, so switching it fades instead of cutting;
        // a peaking biquad at 0 dB is an exact pass-through.
        var filterGains = HasAnyGain(target)
            ? SolveBandGains(frequencies, qFactors, target)
            : target;

        return new BandPlan(frequencies, qFactors, filterGains, HasAnyGain(filterGains),
                            Headroom(frequencies, qFactors, filterGains));
    }

    /// <summary>
    /// Picks up a pending plan and reports whether the bank still has to run. A flattened EQ
    /// keeps processing until its bands have crossfaded back to unity, and only then bypasses.
    /// </summary>
    private bool RefreshFilters()
    {
        BandPlan? incoming;
        bool reset;
        lock (_settingsGate)
        {
            reset = _resetRequested;
            _resetRequested = false;

            if (_pendingPlan is null && !reset)
                return _filters is not null && (_tailSamples > 0 || _activePlan.HasAnyGain);

            // Restarting a crossfade that is still running snaps the output back to the old
            // response, and dragging a slider sends updates far faster than a crossfade takes.
            // Hold the newest plan and pick it up when the one in flight finishes.
            if (_pendingPlan is not null && _tailSamples > 0 && !reset)
                return true;

            incoming = _pendingPlan;
            _pendingPlan = null;
        }

        if (reset)
        {
            ResetFilterState();
            _tailSamples = 0;
        }

        var plan = incoming ?? _activePlan;

        // A different band count is a different bank; there is nothing to crossfade from.
        if (_filters is null || plan.Frequencies.Length != _activePlan.Frequencies.Length)
        {
            _activePlan = plan;
            BuildFilters(plan);
            return plan.HasAnyGain;
        }

        for (var band = 0; band < plan.Frequencies.Length; band++)
        {
            var moved = Math.Abs(plan.Frequencies[band] - _activePlan.Frequencies[band]) >= FrequencyEpsilon;
            var regained = Math.Abs(plan.FilterGains[band] - _activePlan.FilterGains[band]) >= GainEpsilon;
            if (!moved && !regained)
                continue;

            for (var channel = 0; channel < _filters.Length; channel++)
            {
                var filter = _filters[channel][band];
                filter.Standby.SetPeakingEq(
                    WaveFormat.SampleRate, CentreFrequency(plan, band), plan.QFactors[band], BandGain(plan, band));
                filter.BeginCrossfade();
            }

            _tailSamples = _crossfadeSamples * WaveFormat.Channels;
        }

        _activePlan = plan;
        return _tailSamples > 0 || plan.HasAnyGain;
    }

    private void BuildFilters(BandPlan plan)
    {
        var channels = WaveFormat.Channels;
        _filters = new CrossfadingBiQuadFilter[channels][];
        for (var channel = 0; channel < channels; channel++)
        {
            _filters[channel] = new CrossfadingBiQuadFilter[plan.Frequencies.Length];
            for (var band = 0; band < plan.Frequencies.Length; band++)
            {
                var frequency = CentreFrequency(plan, band);
                var q = plan.QFactors[band];
                var gainDb = BandGain(plan, band);
                _filters[channel][band] = new CrossfadingBiQuadFilter(
                    BiQuadFilter.PeakingEQ(WaveFormat.SampleRate, frequency, q, gainDb),
                    BiQuadFilter.PeakingEQ(WaveFormat.SampleRate, frequency, q, gainDb),
                    _crossfadeSamples);
            }
        }
    }

    private void ResetFilterState()
    {
        if (_filters is not null)
            foreach (var channel in _filters)
                foreach (var filter in channel)
                    filter.Reset();
    }

    /// <summary>
    /// Puts a caller-supplied band list into a shape the filter bank can use: finite, positive,
    /// strictly ascending and inside the sample rate. Bands are nudged rather than sorted, so a
    /// frequency typed under one slider can never jump to a different slider.
    /// </summary>
    private float[] SanitiseFrequencies(IReadOnlyList<float> centreFrequencies)
    {
        var ceiling = WaveFormat.SampleRate * MaxCentreFrequencyRatio;
        var result = new float[centreFrequencies.Count];
        var floor = MinCentreFrequency;

        for (var i = 0; i < result.Length; i++)
        {
            var value = centreFrequencies[i];
            if (!float.IsFinite(value) || value <= 0)
                value = floor;

            // Keep ascending, but never push a band past what the sample rate can represent —
            // one parked at the ceiling is rendered flat rather than being misplaced.
            result[i] = Math.Min(Math.Max(value, floor), ceiling);
            floor = result[i] * MinBandSpacingRatio;
        }

        return result;
    }

    /// <summary>
    /// Whether a band's centre is far enough below Nyquist for a peaking filter to actually be
    /// the band it claims to be. At 48 kHz a 16 kHz band is; at 22 kHz it is not.
    /// </summary>
    private bool IsRealisable(BandPlan plan, int band) =>
        plan.Frequencies[band] < WaveFormat.SampleRate * MaxCentreFrequencyRatio;

    /// <summary>Centre frequency the biquad is built at; always legal for the sample rate.</summary>
    private float CentreFrequency(BandPlan plan, int band) =>
        Math.Min(plan.Frequencies[band], WaveFormat.SampleRate * MaxCentreFrequencyRatio);

    /// <summary>Gain a band's filter is driven at: zero, i.e. pass-through, if unrealisable.</summary>
    private float BandGain(BandPlan plan, int band) =>
        IsRealisable(plan, band) ? (float)plan.FilterGains[band] : 0;

    private static bool HasAnyGain(double[] gains)
    {
        foreach (var gain in gains)
            if (Math.Abs(gain) >= GainEpsilon)
                return true;
        return false;
    }

    /// <summary>
    /// Turns slider positions into the filter gains that actually produce them. Neighbouring
    /// bells overlap, so driving each filter straight off its slider stacks the skirts and
    /// overshoots — ten sliders at +6 dB measure nearer +9. This solves the interaction between
    /// the bands so the response at each centre frequency lands on the number that was asked
    /// for, which is what makes one slider move one part of the spectrum and nothing else.
    /// </summary>
    private double[] SolveBandGains(float[] frequencies, float[] qFactors, double[] target)
    {
        var count = target.Length;
        var solved = (double[])target.Clone();
        var ceiling = WaveFormat.SampleRate * MaxCentreFrequencyRatio;

        for (var pass = 0; pass < SolverPasses; pass++)
        {
            var interaction = new double[count, count];
            for (var column = 0; column < count; column++)
            {
                // A bell's skirts widen with gain, so linearise around the running estimate
                // rather than a fixed reference. A band sitting at zero still needs a usable
                // column, since it may be asked to trim a neighbour's spill.
                var reference = Math.Max(Math.Abs(solved[column]), 1);
                var realisable = frequencies[column] < ceiling;
                for (var row = 0; row < count; row++)
                    interaction[row, column] = realisable
                        ? PeakingResponseDb(frequencies[row], frequencies[column], qFactors[column], reference) / reference
                        : row == column ? 1 : 0;
            }

            // Normal equations rather than a direct solve, so the ridge term above can damp
            // the near-collinear cases instead of letting them run away.
            var normal = new double[count, count];
            var projected = new double[count];
            for (var i = 0; i < count; i++)
            {
                for (var j = 0; j < count; j++)
                {
                    double sum = 0;
                    for (var row = 0; row < count; row++)
                        sum += interaction[row, i] * interaction[row, j];
                    normal[i, j] = sum + (i == j ? SolverDamping : 0);
                }

                double rhs = 0;
                for (var row = 0; row < count; row++)
                    rhs += interaction[row, i] * target[row];
                projected[i] = rhs;
            }

            if (!TrySolve(normal, projected, out var next))
                break;
            solved = next;
        }

        foreach (var gain in solved)
            if (!double.IsFinite(gain) || Math.Abs(gain) > MaxSolvedGainDb)
                return target;

        return solved;
    }

    /// <summary>
    /// Gain to apply after the bank so its output never rises above its input: the inverse of
    /// the highest point of the combined response, or unity when the bank never boosts.
    ///
    /// The peak is looked for across the spectrum rather than taken from the largest band gain,
    /// because neighbouring bells add -- two +2 dB bands a third of an octave apart make more
    /// than +2 dB between them. The grid is logarithmic, which is where the bands are, and runs
    /// to the same limit above which bands are left flat.
    /// </summary>
    private float Headroom(float[] frequencies, float[] qFactors, double[] filterGains)
    {
        if (!HasAnyGain(filterGains))
            return 1;

        var nyquistLimit = WaveFormat.SampleRate * MaxCentreFrequencyRatio;
        var lowest = Math.Min(MinCentreFrequency, nyquistLimit / 2);
        var steps = HeadroomGridPoints;
        var ratio = Math.Pow(nyquistLimit / lowest, 1.0 / (steps - 1));

        var peakDb = 0.0;
        var frequency = (double)lowest;
        for (var step = 0; step < steps; step++, frequency *= ratio)
        {
            var sum = 0.0;
            for (var band = 0; band < frequencies.Length; band++)
            {
                if (Math.Abs(filterGains[band]) < GainEpsilon)
                    continue;
                sum += PeakingResponseDb(frequency, frequencies[band], qFactors[band], filterGains[band]);
            }

            if (sum > peakDb)
                peakDb = sum;
        }

        return peakDb > 0 ? (float)Math.Pow(10, -peakDb / 20) : 1;
    }

    /// <summary>Response, in dB, that one band's peaking filter has at a given frequency.</summary>
    private double PeakingResponseDb(double frequency, float centre, float q, double gainDb)
    {
        // The RBJ peaking-EQ coefficients the biquad itself is built from.
        var a = Math.Pow(10, gainDb / 40);
        var w0 = 2 * Math.PI * centre / WaveFormat.SampleRate;
        var alpha = Math.Sin(w0) / (2 * q);
        var cosW0 = Math.Cos(w0);
        var w = 2 * Math.PI * frequency / WaveFormat.SampleRate;

        var numerator = Magnitude(1 + alpha * a, -2 * cosW0, 1 - alpha * a, w);
        var denominator = Magnitude(1 + alpha / a, -2 * cosW0, 1 - alpha / a, w);
        return denominator > 0 ? 20 * Math.Log10(numerator / denominator) : 0;
    }

    /// <summary>Magnitude of a second-order polynomial in z⁻¹ evaluated on the unit circle.</summary>
    private static double Magnitude(double c0, double c1, double c2, double w)
    {
        var real = c0 + c1 * Math.Cos(w) + c2 * Math.Cos(2 * w);
        var imaginary = -(c1 * Math.Sin(w) + c2 * Math.Sin(2 * w));
        return Math.Sqrt(real * real + imaginary * imaginary);
    }

    /// <summary>Gaussian elimination with partial pivoting; false if the system is singular.</summary>
    private static bool TrySolve(double[,] matrix, double[] rhs, out double[] solution)
    {
        var count = rhs.Length;
        var work = (double[,])matrix.Clone();
        solution = [.. rhs];

        for (var pivot = 0; pivot < count; pivot++)
        {
            var best = pivot;
            for (var row = pivot + 1; row < count; row++)
                if (Math.Abs(work[row, pivot]) > Math.Abs(work[best, pivot]))
                    best = row;

            if (Math.Abs(work[best, pivot]) < 1e-12)
                return false;

            if (best != pivot)
            {
                for (var column = 0; column < count; column++)
                    (work[pivot, column], work[best, column]) = (work[best, column], work[pivot, column]);
                (solution[pivot], solution[best]) = (solution[best], solution[pivot]);
            }

            for (var row = pivot + 1; row < count; row++)
            {
                var factor = work[row, pivot] / work[pivot, pivot];
                if (factor == 0)
                    continue;
                for (var column = pivot; column < count; column++)
                    work[row, column] -= factor * work[pivot, column];
                solution[row] -= factor * solution[pivot];
            }
        }

        for (var row = count - 1; row >= 0; row--)
        {
            var sum = solution[row];
            for (var column = row + 1; column < count; column++)
                sum -= work[row, column] * solution[column];
            solution[row] = sum / work[row, row];
        }

        return true;
    }

    /// <summary>
    /// Derives each band's Q from the spacing of its neighbours so its bell spans roughly the
    /// gap it owns. A single Q shared by unevenly spaced bands makes the closely spaced ones
    /// pile up, which both skews the response and leaves the solve above more to undo. Since
    /// the centres are user-adjustable, this is recomputed whenever they move.
    /// </summary>
    private static float[] ComputeQFactors(float[] frequencies)
    {
        var qFactors = new float[frequencies.Length];
        for (var band = 0; band < frequencies.Length; band++)
        {
            // The wider of the two neighbouring gaps, so a band always reaches its furthest
            // neighbour; sizing to the average instead leaves a hole wherever one gap is much
            // bigger than the other.
            var below = band > 0 ? Math.Log2(frequencies[band] / frequencies[band - 1]) : 0;
            var above = band < frequencies.Length - 1
                ? Math.Log2(frequencies[band + 1] / frequencies[band])
                : 0;
            var octaves = Math.Max(Math.Max(below, above), 0.05) * BandwidthScale;

            // Standard bandwidth-to-Q relation for an RBJ peaking filter.
            var width = Math.Pow(2, octaves);
            qFactors[band] = (float)(Math.Sqrt(width) / (width - 1));
        }
        return qFactors;
    }

    /// <summary>
    /// A finished band layout: where the bands sit, how wide each one ended up, and the gains
    /// the filters are driven at (which are not the slider values — see
    /// <see cref="SolveBandGains"/>). Immutable, so the audio thread adopts one atomically.
    /// </summary>
    private sealed record BandPlan(
        float[] Frequencies,
        float[] QFactors,
        double[] FilterGains,
        bool HasAnyGain,
        float Headroom);
}
