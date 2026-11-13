using System.Diagnostics;

namespace Stramp.Integrations.Windows;

/// <summary>
/// Swaps a downloaded, already-extracted update into the install directory and relaunches.
/// str-amp ships as a plain folder with no installer, so "installing" an update means replacing
/// that folder's files - something this running process cannot do to its own open .exe and .dlls.
/// A small PowerShell helper does it instead, started detached so it survives after this process
/// exits a moment later and releases its file locks.
/// </summary>
public static class WindowsAppUpdater
{
    /// <summary>
    /// Starts the helper and returns true once it is running. Nothing in the install directory is
    /// touched until the helper actually runs (after this process has exited), so a false return
    /// leaves the current install exactly as it was.
    /// </summary>
    public static bool TryApplyUpdate(string stagingDirectory, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows())
        {
            error = "Automatic updates are only supported on Windows.";
            return false;
        }

        var exePath = WindowsProcessPaths.GetExecutablePath();
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

        try
        {
            var scriptPath = WriteUpdateScript(stagingDirectory, installDirectory, exePath);
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\" " +
                            $"-ProcessId {Environment.ProcessId}",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Waits for this process to exit, mirrors the staged build over the install directory with
    /// robocopy (retrying a few times in case a file briefly held onto an antivirus scan), relaunches
    /// the app, then cleans up after itself.
    /// </summary>
    private static string WriteUpdateScript(string source, string target, string exePath)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"stramp-update-{Guid.NewGuid():N}.ps1");
        var script = $$"""
            param([int]$ProcessId)
            try { Wait-Process -Id $ProcessId -Timeout 30 -ErrorAction SilentlyContinue } catch {}
            Start-Sleep -Milliseconds 500

            $attempt = 0
            do {
                robocopy "{{source}}" "{{target}}" /E /IS /IT /R:2 /W:1 | Out-Null
                $code = $LASTEXITCODE
                $attempt++
            } while ($code -ge 8 -and $attempt -lt 3)

            Start-Process -FilePath "{{exePath}}"
            Remove-Item -LiteralPath "{{source}}" -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
            """;
        File.WriteAllText(scriptPath, script);
        return scriptPath;
    }
}
