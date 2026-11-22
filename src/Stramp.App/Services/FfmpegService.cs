using System.Diagnostics;

namespace Stramp.App.Services;

/// <summary>
/// Finds ffmpeg and installs it through the platform's package manager: winget on Windows, the
/// distro's own (apt, dnf, pacman, zypper, xbps, apk) on Linux, run through pkexec so the user gets
/// the normal graphical password prompt. ffmpeg is optional on Windows (only .ogg/.opus,
/// normalization and the waveform need it) and required on Linux, where it does all the decoding.
/// </summary>
public static class FfmpegService
{
    public const string WingetPackageId = "Gyan.FFmpeg";

    /// <summary>One way of installing ffmpeg: what to run, and what to tell the user.</summary>
    /// <param name="Label">Button text, e.g. "Install ffmpeg with winget".</param>
    /// <param name="ManualCommand">The command the user can run themselves in a terminal.</param>
    /// <param name="FileName">Executable to start.</param>
    /// <param name="Arguments">Its arguments.</param>
    private sealed record InstallPlan(string Label, string ManualCommand, string FileName, string[] Arguments);

    private static readonly Lazy<InstallPlan?> Plan = new(DetectPlan);

    /// <summary>True when there's a one-click install on this machine.</summary>
    public static bool CanInstallAutomatically => Plan.Value is not null;

    public static string InstallButtonText => Plan.Value?.Label ?? "Install ffmpeg";

    /// <summary>What to tell the user to do themselves when there's no one-click install.</summary>
    public static string ManualInstallHint => Plan.Value is { } plan
        ? $"Or install it yourself: {plan.ManualCommand}"
        : "Install ffmpeg with your package manager (for example: sudo apt install ffmpeg, " +
          "sudo dnf install ffmpeg, or sudo pacman -S ffmpeg), then press Check again.";

    private static InstallPlan? DetectPlan()
    {
        if (OperatingSystem.IsWindows())
        {
            return new InstallPlan(
                "Install ffmpeg with winget",
                $"winget install {WingetPackageId}",
                "winget",
                ["install", "--id", WingetPackageId, "--exact", "--silent",
                 "--accept-source-agreements", "--accept-package-agreements"]);
        }

        if (!OperatingSystem.IsLinux() || FindOnPath("pkexec") is null)
            return null;

        // Each plan runs under `pkexec sh -c "<script>"` so multi-step installs are one prompt.
        (string Binary, string Name, string Script)[] candidates =
        [
            ("apt-get", "apt", "apt-get update && apt-get install -y ffmpeg"),
            ("dnf", "dnf", "dnf install -y ffmpeg || dnf install -y ffmpeg-free"),
            ("pacman", "pacman", "pacman -S --noconfirm ffmpeg"),
            ("zypper", "zypper", "zypper --non-interactive install ffmpeg"),
            ("xbps-install", "xbps", "xbps-install -y ffmpeg"),
            ("apk", "apk", "apk add ffmpeg"),
        ];

        foreach (var (binary, name, script) in candidates)
        {
            if (FindOnPath(binary) is null)
                continue;

            return new InstallPlan(
                $"Install ffmpeg with {name}",
                "sudo sh -c '" + script + "'",
                "pkexec",
                ["sh", "-c", script]);
        }

        return null;
    }

    private static string? FindOnPath(string name)
    {
        // pkexec and the package managers usually live in /usr/sbin or /sbin, which a desktop
        // session's PATH may not include.
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(["/usr/bin", "/bin", "/usr/sbin", "/sbin"]);

        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>True when "ffmpeg" can be started from this process's PATH.</summary>
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                ArgumentList = { "-version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
                return false;

            // Drain output so a chatty build can't block on a full pipe.
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Installs ffmpeg with the platform's package manager. Returns null on success, or a message to
    /// show the user. On Windows a successful install only updates PATH for new processes, so this
    /// also refreshes this process's own PATH, letting ffmpeg work without restarting str-amp.
    /// </summary>
    public static async Task<string?> InstallAsync(CancellationToken ct = default)
    {
        if (Plan.Value is not { } plan)
            return ManualInstallHint;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = plan.FileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in plan.Arguments)
                psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi);
            if (process is null)
                return $"Couldn't start {plan.FileName}.";

            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(output, error);

            if (OperatingSystem.IsWindows())
            {
                // 0x8A150061 (package already installed) also means "it's there".
                if (process.ExitCode != 0 && (uint)process.ExitCode != 0x8A150061)
                    return $"winget couldn't install ffmpeg (exit code {process.ExitCode}). " +
                           $"You can run \"{plan.ManualCommand}\" yourself.";
            }
            else if (process.ExitCode == 126 || process.ExitCode == 127)
            {
                // pkexec: 126 = the user dismissed the password prompt / wasn't authorized.
                return "The install wasn't authorized. Press the button to try again, " +
                       $"or run it yourself: {plan.ManualCommand}";
            }
            else if (process.ExitCode != 0)
            {
                return $"The install failed (exit code {process.ExitCode}). " +
                       $"You can run it yourself: {plan.ManualCommand}";
            }

            RefreshProcessPath();
            return await IsAvailableAsync()
                ? null
                : "ffmpeg installed, but str-amp can't find it yet. Restart str-amp and it should be picked up.";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return OperatingSystem.IsWindows()
                ? "winget isn't available on this PC. Install ffmpeg manually from ffmpeg.org, " +
                  "or install \"App Installer\" from the Microsoft Store to get winget."
                : $"Couldn't run {plan.FileName}. You can run it yourself: {plan.ManualCommand}";
        }
        catch (OperationCanceledException)
        {
            return "Cancelled.";
        }
        catch (Exception ex)
        {
            return $"Couldn't install ffmpeg: {ex.Message}";
        }
    }

    /// <summary>Re-reads the machine and user PATH (winget changes them after we started).</summary>
    private static void RefreshProcessPath()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var parts = new List<string>();
        foreach (var target in new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User })
        {
            var value = Environment.GetEnvironmentVariable("PATH", target);
            if (!string.IsNullOrEmpty(value))
                parts.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries));
        }

        // winget's per-user command shims live here and aren't always on the registry PATH yet.
        parts.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links"));

        var current = Environment.GetEnvironmentVariable("PATH") ?? "";
        parts.AddRange(current.Split(';', StringSplitOptions.RemoveEmptyEntries));

        var merged = string.Join(';', parts
            .Select(Environment.ExpandEnvironmentVariables)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        Environment.SetEnvironmentVariable("PATH", merged);
    }
}
