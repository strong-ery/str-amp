using System.Diagnostics;
using System.Text;

namespace Stramp.Integrations.Linux;

/// <summary>
/// Swaps a downloaded, already-extracted update into the folder str-amp is running from and
/// relaunches it. The running binary can't safely overwrite itself, so a small shell helper does
/// it after this process exits, started detached so it outlives us.
/// </summary>
public static class LinuxAppUpdater
{
    /// <summary>
    /// Starts the helper and returns true once it is running. Nothing in the install folder is
    /// touched until the helper runs (after this process has exited), so a false return leaves the
    /// current install exactly as it was.
    /// </summary>
    public static bool TryApplyUpdate(string stagingDirectory, out string? error)
    {
        error = null;

        var exePath = Environment.ProcessPath;
        var installDirectory = exePath is null ? null : Path.GetDirectoryName(exePath);
        if (exePath is null || string.IsNullOrEmpty(installDirectory))
        {
            error = "Could not locate the running executable.";
            return false;
        }

        if (!Directory.Exists(stagingDirectory) || !Directory.EnumerateFileSystemEntries(stagingDirectory).Any())
        {
            error = "The downloaded update is missing or empty.";
            return false;
        }

        if (!IsWritable(installDirectory))
        {
            error = $"{installDirectory} isn't writable (installed system-wide, or a read-only image). " +
                    "Download the new release from GitHub instead.";
            return false;
        }

        try
        {
            var scriptPath = Path.Combine(Path.GetTempPath(), $"stramp-update-{Guid.NewGuid():N}.sh");
            File.WriteAllText(scriptPath, BuildScript(), new UTF8Encoding(false));

            // setsid detaches the helper from our process group so it survives us exiting; plain sh
            // is the fallback where it's missing.
            var launcher = File.Exists("/usr/bin/setsid") ? "/usr/bin/setsid" : "/bin/sh";
            var psi = new ProcessStartInfo { FileName = launcher, UseShellExecute = false, CreateNoWindow = true };
            if (launcher.EndsWith("setsid", StringComparison.Ordinal))
                psi.ArgumentList.Add("/bin/sh");
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add(stagingDirectory);
            psi.ArgumentList.Add(installDirectory);
            psi.ArgumentList.Add(exePath);
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".stramp-write-test-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Waits for the app to exit, copies the staged build over the install folder, relaunches, then
    /// cleans up after itself. Arguments: pid, staging dir, install dir, executable path.
    /// </summary>
    private static string BuildScript() => """
        #!/bin/sh
        pid="$1"; src="$2"; dest="$3"; exe="$4"

        # Up to ~30s for the app to finish exiting.
        i=0
        while kill -0 "$pid" 2>/dev/null && [ "$i" -lt 60 ]; do
            sleep 0.5
            i=$((i + 1))
        done
        sleep 0.5

        # cp -a keeps the executable bit; retry a few times in case something briefly holds a file.
        attempt=0
        until cp -a "$src"/. "$dest"/; do
            attempt=$((attempt + 1))
            [ "$attempt" -ge 3 ] && break
            sleep 1
        done

        chmod +x "$exe" 2>/dev/null
        nohup "$exe" >/dev/null 2>&1 &

        rm -rf "$src"
        rm -f "$0"
        """;
}
