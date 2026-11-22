using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using NAudio.Wave;

namespace Stramp.Audio.Linux;

/// <summary>What ffprobe says about a file's first audio stream.</summary>
internal readonly record struct AudioProbe(int Channels, int SampleRate, double DurationSeconds);

internal static class FfmpegProbe
{
    /// <summary>Thrown when ffmpeg/ffprobe isn't installed, with a message fit to show the user.</summary>
    internal sealed class FfmpegMissingException(Exception inner) : InvalidOperationException(
        "playback needs ffmpeg, which wasn't found. Install ffmpeg (for example `sudo apt install ffmpeg`).", inner);

    internal static AudioProbe Probe(string path)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ffprobe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[]
        {
            "-v", "error", "-select_streams", "a:0",
            "-show_entries", "stream=channels,sample_rate:format=duration",
            "-of", "json", path,
        })
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start ffprobe.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException("ffprobe timed out reading this file.");
            }

            var json = stdoutTask.GetAwaiter().GetResult();
            stderrTask.GetAwaiter().GetResult();

            using var doc = JsonDocument.Parse(json);
            var streams = doc.RootElement.GetProperty("streams");
            if (streams.GetArrayLength() == 0)
                throw new InvalidOperationException("this file has no audio stream.");

            var stream = streams[0];
            var channels = stream.GetProperty("channels").GetInt32();
            var rate = int.Parse(stream.GetProperty("sample_rate").GetString()!, CultureInfo.InvariantCulture);

            var duration = 0.0;
            if (doc.RootElement.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var d) &&
                double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                duration = parsed;

            return new AudioProbe(Math.Max(1, channels), rate, duration);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new FfmpegMissingException(ex);
        }
    }
}

/// <summary>
/// Decodes any file ffmpeg understands to interleaved 32-bit float PCM by reading a child process's
/// stdout. Seeking is a restart at a new offset: input seeking (-ss before -i) is fast.
/// </summary>
internal sealed class FfmpegSampleProvider : ISampleProvider, IDisposable
{
    private readonly Process _process;
    private readonly Stream _stdout;
    private readonly byte[] _scratch = new byte[64 * 1024];
    private int _carry;
    private bool _finished;

    public WaveFormat WaveFormat { get; }

    /// <summary>True once ffmpeg's output has been read to the end.</summary>
    public bool ReachedEnd => _finished;

    /// <summary>True if ffmpeg exited with an error rather than a clean end of stream.</summary>
    public bool Failed { get; private set; }

    public FfmpegSampleProvider(string path, double startSeconds, int channels, int sampleRate)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-nostdin", "-v", "error" })
            psi.ArgumentList.Add(arg);
        if (startSeconds > 0)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(startSeconds.ToString("0.######", CultureInfo.InvariantCulture));
        }
        foreach (var arg in new[]
        {
            "-i", path, "-map", "0:a:0", "-vn", "-sn", "-dn",
            "-f", "f32le", "-ac", channels.ToString(CultureInfo.InvariantCulture),
            "-ar", sampleRate.ToString(CultureInfo.InvariantCulture), "-",
        })
            psi.ArgumentList.Add(arg);

        try
        {
            _process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start ffmpeg.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new FfmpegProbe.FfmpegMissingException(ex);
        }

        // Unread stderr eventually fills its pipe and stalls ffmpeg; nothing in it is needed.
        _process.ErrorDataReceived += static (_, _) => { };
        _process.BeginErrorReadLine();
        _stdout = _process.StandardOutput.BaseStream;
    }

    public int Read(Span<float> buffer)
    {
        if (_finished || buffer.IsEmpty)
            return 0;

        var floatsRead = 0;
        while (floatsRead < buffer.Length)
        {
            var wantBytes = Math.Min(_scratch.Length, (buffer.Length - floatsRead) * sizeof(float));
            var n = _stdout.Read(_scratch, _carry, wantBytes - _carry);
            if (n == 0)
            {
                _finished = true;
                try
                {
                    _process.WaitForExit(2000);
                    Failed = _process.HasExited && _process.ExitCode != 0;
                }
                catch { }
                break;
            }

            var available = _carry + n;
            var whole = available / sizeof(float);
            System.Runtime.InteropServices.MemoryMarshal
                .Cast<byte, float>(_scratch.AsSpan(0, whole * sizeof(float)))
                .CopyTo(buffer[floatsRead..]);
            floatsRead += whole;

            _carry = available - whole * sizeof(float);
            if (_carry > 0)
                Buffer.BlockCopy(_scratch, whole * sizeof(float), _scratch, 0, _carry);
        }

        return floatsRead;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone.
        }

        _process.Dispose();
    }
}
