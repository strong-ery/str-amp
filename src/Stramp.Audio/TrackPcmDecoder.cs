using System.Diagnostics;

namespace Stramp.Audio;

/// <summary>
/// Decodes an audio file to raw mono PCM via ffmpeg, entirely separate from playback. Used to feed
/// the spectrum visualizer/beat detection without touching the WASAPI playback pipeline — playback
/// keeps working normally even if ffmpeg is missing or decoding fails, since this is a purely
/// supplementary feature.
/// </summary>
public static class TrackPcmDecoder
{
    public static async Task<float[]?> DecodeMonoAsync(string path, int sampleRate, CancellationToken ct = default)
    {
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

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            using var stdout = new MemoryStream();
            var copyTask = process.StandardOutput.BaseStream.CopyToAsync(stdout, ct);
            var drainStderrTask = process.StandardError.ReadToEndAsync(ct);

            await copyTask;
            await drainStderrTask;
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
                return null;

            var bytes = stdout.GetBuffer();
            var sampleCount = (int)(stdout.Length / sizeof(float));
            var samples = new float[sampleCount];
            Buffer.BlockCopy(bytes, 0, samples, 0, sampleCount * sizeof(float));
            return samples;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            // ffmpeg missing, unreadable file, etc — the visualizer just stays idle.
            return null;
        }
    }
}
