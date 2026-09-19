using NAudio.Wave;
using Stramp.Audio;

namespace Stramp.Core.Tests;

public sealed class SurroundDownmixTests
{
    private sealed class TestMultiChannelProvider : ISampleProvider
    {
        private readonly float[] _data;
        private int _position;

        public WaveFormat WaveFormat { get; }

        public TestMultiChannelProvider(int sampleRate, int channels, float[] data)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _data = data;
        }

        public int Read(Span<float> buffer)
        {
            var available = Math.Min(buffer.Length, _data.Length - _position);
            if (available <= 0)
                return 0;

            _data.AsSpan(_position, available).CopyTo(buffer);
            _position += available;
            return available;
        }
    }

    [Fact]
    public void RejectsTwoChannelsOrFewer()
    {
        var mono = new TestMultiChannelProvider(48000, 1, [0.5f]);
        var stereo = new TestMultiChannelProvider(48000, 2, [0.5f, 0.5f]);

        Assert.Throws<ArgumentException>(() => new SurroundToStereoSampleProvider(mono));
        Assert.Throws<ArgumentException>(() => new SurroundToStereoSampleProvider(stereo));
    }

    [Fact]
    public void DownmixWaveFormatIsStereoSameSampleRate()
    {
        var provider = new TestMultiChannelProvider(96000, 6, new float[6]);
        var downmix = new SurroundToStereoSampleProvider(provider);

        Assert.Equal(2, downmix.WaveFormat.Channels);
        Assert.Equal(96000, downmix.WaveFormat.SampleRate);
        Assert.Equal(32, downmix.WaveFormat.BitsPerSample);
    }

    [Fact]
    public void FullScaleOnAllChannelsDoesNotClip()
    {
        // 5.1 layout: all channels at 1.0f
        var frame = new float[] { 1f, 1f, 1f, 1f, 1f, 1f };
        var provider = new TestMultiChannelProvider(48000, 6, frame);
        var downmix = new SurroundToStereoSampleProvider(provider);

        var output = new float[2];
        var read = downmix.Read(output);

        Assert.Equal(2, read);
        // Norm ensures the sum is <= 1.0f
        Assert.InRange(output[0], 0f, 1.00001f);
        Assert.InRange(output[1], 0f, 1.00001f);
        // And Left equals Right when input is symmetrical
        Assert.Equal(output[0], output[1], precision: 5);
    }

    [Fact]
    public void CenterAndLfeAreDistributedEquallyToBothEars()
    {
        // Center only
        var centerFrame = new float[] { 0f, 0f, 1f, 0f, 0f, 0f };
        var centerProvider = new TestMultiChannelProvider(48000, 6, centerFrame);
        var centerDownmix = new SurroundToStereoSampleProvider(centerProvider);
        var centerOutput = new float[2];
        centerDownmix.Read(centerOutput);

        Assert.True(centerOutput[0] > 0);
        Assert.Equal(centerOutput[0], centerOutput[1], precision: 6);

        // LFE only
        var lfeFrame = new float[] { 0f, 0f, 0f, 1f, 0f, 0f };
        var lfeProvider = new TestMultiChannelProvider(48000, 6, lfeFrame);
        var lfeDownmix = new SurroundToStereoSampleProvider(lfeProvider);
        var lfeOutput = new float[2];
        lfeDownmix.Read(lfeOutput);

        Assert.True(lfeOutput[0] > 0);
        Assert.Equal(lfeOutput[0], lfeOutput[1], precision: 6);
    }

    [Fact]
    public void LeftAndRightSurroundChannelsStayInRespectiveEars()
    {
        // Surround Left only
        var slFrame = new float[] { 0f, 0f, 0f, 0f, 1f, 0f };
        var slProvider = new TestMultiChannelProvider(48000, 6, slFrame);
        var slDownmix = new SurroundToStereoSampleProvider(slProvider);
        var slOutput = new float[2];
        slDownmix.Read(slOutput);

        Assert.True(slOutput[0] > 0);
        Assert.Equal(0f, slOutput[1]);

        // Surround Right only
        var srFrame = new float[] { 0f, 0f, 0f, 0f, 0f, 1f };
        var srProvider = new TestMultiChannelProvider(48000, 6, srFrame);
        var srDownmix = new SurroundToStereoSampleProvider(srProvider);
        var srOutput = new float[2];
        srDownmix.Read(srOutput);

        Assert.Equal(0f, srOutput[0]);
        Assert.True(srOutput[1] > 0);
    }

    [Fact]
    public void FrontLeftAndRightStayInRespectiveEars()
    {
        // Front Left only
        var flFrame = new float[] { 1f, 0f, 0f, 0f, 0f, 0f };
        var flProvider = new TestMultiChannelProvider(48000, 6, flFrame);
        var flDownmix = new SurroundToStereoSampleProvider(flProvider);
        var flOutput = new float[2];
        flDownmix.Read(flOutput);

        Assert.True(flOutput[0] > 0);
        Assert.Equal(0f, flOutput[1]);

        // Front Right only
        var frFrame = new float[] { 0f, 1f, 0f, 0f, 0f, 0f };
        var frProvider = new TestMultiChannelProvider(48000, 6, frFrame);
        var frDownmix = new SurroundToStereoSampleProvider(frProvider);
        var frOutput = new float[2];
        frDownmix.Read(frOutput);

        Assert.Equal(0f, frOutput[0]);
        Assert.True(frOutput[1] > 0);
    }

    [Fact]
    public void SevenPointOneDownmixesWithoutClipping()
    {
        // 7.1 layout: 8 channels at full scale
        var frame = new float[] { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };
        var provider = new TestMultiChannelProvider(48000, 8, frame);
        var downmix = new SurroundToStereoSampleProvider(provider);

        var output = new float[2];
        var read = downmix.Read(output);

        Assert.Equal(2, read);
        Assert.InRange(output[0], 0f, 1.00001f);
        Assert.InRange(output[1], 0f, 1.00001f);
        Assert.Equal(output[0], output[1], precision: 5);
    }

    [Fact]
    public void WasapiMediaPlayerPlaysSurroundTrackWithoutCrashing()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var path = @"C:\Users\dylan\Music\Spotify Export\TotK Trailer 3 [Bonus Track] - Nintendo.flac";
        if (!File.Exists(path))
            return;

        using var player = new WasapiMediaPlayer();

        // 1. Play with SurroundSoundEnabled = false (stereo downmix)
        player.SurroundSoundEnabled = false;
        player.Play(path);
        Assert.True(player.IsPlaying);
        player.Pause();

        // 2. Play with SurroundSoundEnabled = true (surround or fallback)
        player.SurroundSoundEnabled = true;
        player.Play(path);
        Assert.True(player.IsPlaying);
        player.Pause();
    }
}

