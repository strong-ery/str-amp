using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Stramp.Core.Playback;

namespace Stramp.Audio;

/// <summary>Measures integrated EBU R128 loudness and true peak without changing the source file.</summary>
public static class FfmpegLoudnessAnalyzer
{
    public static async Task<TrackLoudness?> AnalyzeAsync(string path, CancellationToken ct = default)
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
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-nostats");
            psi.ArgumentList.Add("-threads");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-filter_threads");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("-vn");
            psi.ArgumentList.Add("-sn");
            psi.ArgumentList.Add("-dn");
            psi.ArgumentList.Add("-af");
            psi.ArgumentList.Add("loudnorm=I=-14:TP=-1:LRA=11:print_format=json");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("null");
            psi.ArgumentList.Add("-");

            process = Process.Start(psi);
            if (process is null)
                return null;

            using var cancellation = ct.Register(() => TryKill(process));
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stderr = await stderrTask;
            await stdoutTask;

            if (process.ExitCode != 0)
                return null;

            return ParseMeasurements(stderr);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    internal static TrackLoudness? ParseMeasurements(string output)
    {
        var start = output.LastIndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        try
        {
            using var json = JsonDocument.Parse(output[start..(end + 1)]);
            if (!TryReadDouble(json.RootElement, "input_i", out var integrated) ||
                !TryReadDouble(json.RootElement, "input_tp", out var truePeak))
                return null;

            return new TrackLoudness(integrated, truePeak);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadDouble(JsonElement root, string name, out double value)
    {
        value = 0;
        if (!root.TryGetProperty(name, out var property))
            return false;

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetDouble(out value) && double.IsFinite(value),
            JsonValueKind.String => double.TryParse(property.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value) && double.IsFinite(value),
            _ => false,
        };
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
