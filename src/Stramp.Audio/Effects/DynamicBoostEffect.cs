namespace Stramp.Audio.Effects;

/// <summary>
/// Upward compression: quiet passages are lifted toward the loud ones, so the track sits at a more
/// even level without its peaks being pushed any higher.
///
/// This is the opposite of what a normal compressor does. A downward compressor turns the loud
/// parts down and then makes the whole thing up with a make-up gain; the peaks end up flattened and
/// the noise floor comes up with everything else. Here the gain applies only below the threshold
/// and tapers to nothing above it, so transients keep their shape and only material that was
/// already quiet moves.
///
/// The gain is derived from the loudest channel and applied to all of them equally. Per-channel
/// gain would pump the stereo image sideways every time one side got louder than the other.
/// </summary>
internal sealed class DynamicBoostEffect
{
    /// <summary>Level below which lifting starts. Around where a quiet passage sits.</summary>
    private const float ThresholdDb = -28;

    /// <summary>Fraction of the shortfall below the threshold that gets made up.</summary>
    private const float Ratio = 0.55f;

    /// <summary>Most the level is lifted by, at an amount of 10.</summary>
    private const float MaxBoostDb = 12;

    /// <summary>How fast the follower reacts to something getting louder — fast, to catch a transient
    /// before it is boosted into the limiter.</summary>
    private const float AttackSeconds = 0.005f;

    /// <summary>How fast it lets go again. Slow enough not to audibly breathe between notes.</summary>
    private const float ReleaseSeconds = 0.35f;

    /// <summary>Floor on the envelope, so digital silence does not ask for infinite gain.</summary>
    private const float SilenceFloor = 1e-5f;

    private readonly float _attack;
    private readonly float _release;
    private readonly SmoothedParameter _amount;
    private float _envelope;
    private float _gain = 1;

    public DynamicBoostEffect(int sampleRate)
    {
        _attack = 1 - MathF.Exp(-1f / (AttackSeconds * sampleRate));
        _release = 1 - MathF.Exp(-1f / (ReleaseSeconds * sampleRate));
        _amount = new SmoothedParameter(sampleRate);
    }

    /// <summary>True once the lift has faded fully out, so the stage can be skipped.</summary>
    public bool IsIdle => _amount.IsSettled && _amount.Current == 0;

    /// <summary>Sets the amount, 0 to 10 (and a little beyond).</summary>
    public void SetAmount(double amount) => _amount.SetTarget((float)amount / 10);

    public void Reset()
    {
        _envelope = 0;
        _gain = 1;
    }

    public void Process(Span<float> buffer, int count, int channels)
    {
        for (var frame = 0; frame + channels <= count; frame += channels)
        {
            var amount = _amount.Next();

            var peak = 0f;
            for (var channel = 0; channel < channels; channel++)
                peak = MathF.Max(peak, MathF.Abs(buffer[frame + channel]));

            // Asymmetric follower: rises with the attack coefficient, falls with the release one.
            var coefficient = peak > _envelope ? _attack : _release;
            _envelope += (peak - _envelope) * coefficient;

            var level = MathF.Max(_envelope, SilenceFloor);
            var levelDb = 20 * MathF.Log10(level);
            var shortfall = MathF.Max(0, ThresholdDb - levelDb);
            var boostDb = MathF.Min(shortfall * Ratio, MaxBoostDb) * amount;

            // The gain follows the same smoothing as the envelope, so it never steps between
            // samples even when the envelope crosses the threshold abruptly.
            var target = MathF.Pow(10, boostDb / 20);
            _gain += (target - _gain) * coefficient;

            for (var channel = 0; channel < channels; channel++)
                buffer[frame + channel] *= _gain;
        }
    }
}
