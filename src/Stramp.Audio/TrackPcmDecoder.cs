using System.Diagnostics;

namespace Stramp.Audio;

/// <summary>
/// Decodes an audio file to raw mono PCM via ffmpeg, entirely separate from playback. Used to feed
/// the spectrum visualizer/beat detection without touching the WASAPI playback pipeline; playback
/// keeps working normally even if ffmpeg is missing or decoding fails, since this is a purely
/// supplementary feature.
/// </summary>
public static class TrackPcmDecoder
{
    /// <summary>
    /// Decodes <paramref name="path"/> to mono float PCM. <paramref name="expectedSeconds"/> is only a
    /// sizing hint so the result lands in a single right-sized buffer; it may be wrong or zero.
    /// </summary>
    public static async Task<ReadOnlyMemory<float>?> DecodeMonoAsync(
        string path, int sampleRate, double expectedSeconds = 0, CancellationToken ct = default)
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("f32le");
            psi.ArgumentList.Add("-ac");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-ar");
            psi.ArgumentList.Add(sampleRate.ToString());
            psi.ArgumentList.Add("-");

            process = Process.Start(psi);
            if (process is null)
                return null;

            using var cancellation = ct.Register(() => TryKill(process));
            var drainStderrTask = process.StandardError.ReadToEndAsync(ct);

            // Stream ffmpeg's output straight into one float[] (no MemoryStream + second copy, which
            // briefly tripled the footprint). The hint plus a second of slack means it normally never
            // has to grow; if the hint is missing or short it grows by 25% rather than doubling.
            var capacity = expectedSeconds > 0
                ? (int)Math.Min(int.MaxValue / 2, (expectedSeconds + 1) * sampleRate)
                : sampleRate * 60;
            var samples = new float[capacity];
            var chunk = new byte[64 * 1024];
            var carry = 0; // bytes of a split float left over from the previous read
            var count = 0; // floats written so far
            var stdout = process.StandardOutput.BaseStream;
            while (true)
            {
                var n = await stdout.ReadAsync(chunk.AsMemory(carry), ct);
                if (n == 0)
                    break;

                var available = carry + n;
                var whole = available / sizeof(float);
                if (count + whole > samples.Length)
                {
                    var grown = new float[(int)Math.Min(
                        int.MaxValue / 2, Math.Max((long)samples.Length * 5 / 4, (long)count + whole) + 4096)];
                    Array.Copy(samples, grown, count);
                    samples = grown;
                }

                Buffer.BlockCopy(chunk, 0, samples, count * sizeof(float), whole * sizeof(float));
                count += whole;
                carry = available - whole * sizeof(float);
                if (carry > 0)
                    Buffer.BlockCopy(chunk, whole * sizeof(float), chunk, 0, carry);
            }

            await drainStderrTask;
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
                return null;

            return samples.AsMemory(0, count);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            // ffmpeg missing, unreadable file, etc: the visualizer just stays idle.
            return null;
        }
        finally
        {
            if (process is not null)
            {
                TryKill(process);
                process.Dispose();
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Cancellation is best-effort; the process may already have exited.
        }
    }
}
