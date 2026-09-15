namespace Stramp.Audio.Effects;

/// <summary>
/// A short reverb, for a sense of room around the track rather than an audible effect.
///
/// Schroeder's arrangement: four feedback comb filters in parallel build up echo density, then two
/// allpass sections in series smear what comes out so the combs' individual repeats stop being
/// countable. The comb delays are mutually prime so their repeats do not line up into a ringing
/// pitch, and the right channel's delays are offset from the left's so the two sides decorrelate —
/// without that the reverb collapses to a mono blob in the middle of the image.
///
/// Only the wet path is delayed; the dry signal passes through untouched, so nothing here adds
/// latency to the track itself.
/// </summary>
internal sealed class AmbienceEffect
{
    /// <summary>Comb delays in samples at 44.1 kHz, scaled to the real rate. Mutually prime.</summary>
    private static readonly int[] CombDelays = [1557, 1617, 1491, 1422];

    /// <summary>Allpass delays in samples at 44.1 kHz.</summary>
    private static readonly int[] AllpassDelays = [225, 341];

    /// <summary>Samples the right channel's delays are offset by, to decorrelate the two sides.</summary>
    private const int StereoSpread = 23;

    private const float ReferenceSampleRate = 44100;
    private const float CombFeedback = 0.78f;
    private const float AllpassFeedback = 0.5f;

    /// <summary>Damping on the comb feedback path, so the tail darkens as it decays.</summary>
    private const float Damping = 0.32f;

    /// <summary>Wet level at an amount of 10. A room, not a cathedral.</summary>
    private const float MaxWet = 0.32f;

    private readonly Comb[][] _combs;
    private readonly Allpass[][] _allpasses;
    private readonly SmoothedParameter _wet;
    private readonly int _channels;

    public AmbienceEffect(int sampleRate, int channels)
    {
        _channels = channels;
        var scale = sampleRate / ReferenceSampleRate;

        _combs = new Comb[channels][];
        _allpasses = new Allpass[channels][];
        for (var channel = 0; channel < channels; channel++)
        {
            // Odd channels (right, and beyond) are offset so no two sides share a delay set.
            var spread = (int)(StereoSpread * scale) * (channel % 2);

            _combs[channel] = new Comb[CombDelays.Length];
            for (var i = 0; i < CombDelays.Length; i++)
                _combs[channel][i] = new Comb((int)(CombDelays[i] * scale) + spread);

            _allpasses[channel] = new Allpass[AllpassDelays.Length];
            for (var i = 0; i < AllpassDelays.Length; i++)
                _allpasses[channel][i] = new Allpass((int)(AllpassDelays[i] * scale) + spread);
        }

        _wet = new SmoothedParameter(sampleRate);
    }

    /// <summary>True once the reverb tail has faded fully out, so the stage can be skipped.</summary>
    public bool IsIdle => _wet.IsSettled && _wet.Current == 0;

    /// <summary>Sets the amount, 0 to 10 (and a little beyond).</summary>
    public void SetAmount(double amount) => _wet.SetTarget((float)amount / 10 * MaxWet);

    public void Reset()
    {
        foreach (var channel in _combs)
            foreach (var comb in channel)
                comb.Reset();
        foreach (var channel in _allpasses)
            foreach (var allpass in channel)
                allpass.Reset();
    }

    public void Process(Span<float> buffer, int count, int channels)
    {
        for (var frame = 0; frame + channels <= count; frame += channels)
        {
            var wet = _wet.Next();
            for (var channel = 0; channel < channels && channel < _channels; channel++)
            {
                var index = frame + channel;
                var input = buffer[index];

                var tail = 0f;
                foreach (var comb in _combs[channel])
                    tail += comb.Process(input);

                foreach (var allpass in _allpasses[channel])
                    tail = allpass.Process(tail);

                buffer[index] = input + wet * tail;
            }
        }
    }

    /// <summary>Feedback comb with a one-pole lowpass in the loop, so repeats lose their top end.</summary>
    private sealed class Comb(int length)
    {
        private readonly float[] _line = new float[Math.Max(length, 1)];
        private int _position;
        private float _filterStore;

        public void Reset()
        {
            Array.Clear(_line);
            _filterStore = 0;
            _position = 0;
        }

        public float Process(float input)
        {
            var output = _line[_position];

            // Flush denormals: a tail decaying toward zero otherwise parks the CPU in
            // denormal arithmetic for as long as the effect is enabled.
            if (MathF.Abs(output) < 1e-20f)
                output = 0;

            _filterStore = output * (1 - Damping) + _filterStore * Damping;
            _line[_position] = input + _filterStore * CombFeedback;

            if (++_position >= _line.Length)
                _position = 0;
            return output;
        }
    }

    /// <summary>Allpass section: passes every frequency at the same level, but not at the same time.</summary>
    private sealed class Allpass(int length)
    {
        private readonly float[] _line = new float[Math.Max(length, 1)];
        private int _position;

        public void Reset()
        {
            Array.Clear(_line);
            _position = 0;
        }

        public float Process(float input)
        {
            var buffered = _line[_position];
            if (MathF.Abs(buffered) < 1e-20f)
                buffered = 0;

            _line[_position] = input + buffered * AllpassFeedback;
            if (++_position >= _line.Length)
                _position = 0;

            return buffered - input;
        }
    }
}
