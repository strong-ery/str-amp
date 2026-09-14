using NAudio.Dsp;

namespace Stramp.Audio.Effects;

/// <summary>
/// Stereo widening, done on the mid/side decomposition: what both speakers share stays where it is,
/// and what differs between them is amplified.
///
///   mid = (L+R)/2      side = (L−R)/2
///   L' = mid + w·side   R' = mid − w·side
///
/// The side signal is split first, and only the part above <see cref="BassCrossoverHz"/> is
/// widened. Low frequencies carry almost no usable directional information — the wavelengths are
/// longer than the distance between your ears — so widening them buys no width, while it does move
/// bass energy out of the shared channel, which is what makes a widened mix collapse into
/// thinness the moment anything sums it to mono.
///
/// A mono track has no side signal at all, so widening it is correctly a no-op rather than a way
/// to fake stereo out of nothing.
/// </summary>
internal sealed class SurroundEffect
{
    /// <summary>Below this, the side signal is passed through at its original level.</summary>
    private const float BassCrossoverHz = 250;

    /// <summary>Side gain at an amount of 10. Past roughly this, the centre starts hollowing out.</summary>
    private const float MaxExtraWidth = 1.1f;

    private readonly BiQuadFilter _sideLow;
    private readonly SmoothedParameter _width;

    public SurroundEffect(int sampleRate)
    {
        _sideLow = BiQuadFilter.LowPassFilter(sampleRate, BassCrossoverHz, 0.7071f);
        _width = new SmoothedParameter(sampleRate, initial: 0);
    }

    /// <summary>True once the extra width has faded fully out, so the stage can be skipped.</summary>
    public bool IsIdle => _width.IsSettled && _width.Current == 0;

    /// <summary>Sets the amount, 0 to 10 (and a little beyond). 0 leaves the image untouched.</summary>
    public void SetAmount(double amount) => _width.SetTarget((float)amount / 10 * MaxExtraWidth);

    public void Reset() => _sideLow.ResetState();

    /// <summary>
    /// Runs only on stereo; there is no side signal to work with otherwise, and applying this to a
    /// surround layout channel-pairwise would scramble it rather than widen it.
    /// </summary>
    public static bool SupportsLayout(int channels) => channels == 2;

    public void Process(Span<float> buffer, int count)
    {
        for (var frame = 0; frame + 2 <= count; frame += 2)
        {
            var extra = _width.Next();
            var left = buffer[frame];
            var right = buffer[frame + 1];

            var mid = (left + right) * 0.5f;
            var side = (left - right) * 0.5f;

            // Widen only what is above the crossover: the low band is subtracted out, scaled by
            // one, and added straight back, so it reaches the output at its original level.
            var sideLow = _sideLow.Transform(side);
            var widened = side + extra * (side - sideLow);

            buffer[frame] = mid + widened;
            buffer[frame + 1] = mid - widened;
        }
    }
}
