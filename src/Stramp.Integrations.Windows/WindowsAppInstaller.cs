using System.Diagnostics;

namespace Stramp.Integrations.Windows;

/// <summary>
/// Moves a portable run (wherever its zip was extracted - Downloads, Desktop, a temp folder) into
/// a stable per-user location the first time it runs, then relaunches from there. The install
/// directory doubles as the config/history folder <see cref="Stramp.Core.Settings.SettingsService"/>
/// already uses, so a relocation never touches - and a fresh install never overwrites - anything a
/// previous run left there.
/// </summary>
public static class WindowsAppInstaller
{
    private const string FolderName = "stramp";

    public static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

    /// <summary>
    /// If the running exe is not already inside <see cref="InstallDirectory"/>, copies the current
    /// build there and launches it. Returns true once that replacement process is started, which
    /// means the caller should exit immediately rather than carry on starting the UI in this one.
    /// A false return means nothing was touched (already installed, or the relocation failed) and
    /// this process should just continue normally.
    /// </summary>
    public static bool TryRelocateToInstallDirectory(string[] args)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var exePath = WindowsProcessPaths.GetExecutablePath();
        var currentDirectory = exePath is null ? null : Path.GetDirectoryName(exePath);
        if (exePath is null || currentDirectory is null)
            return false;

        if (IsSameDirectory(currentDirectory, InstallDirectory))
            return false;

        try
        {
            CopyDirectory(currentDirectory, InstallDirectory);

            var installedExe = Path.Combine(InstallDirectory, Path.GetFileName(exePath));
            if (!File.Exists(installedExe))
                return false;

            Process.Start(new ProcessStartInfo
            {
                FileName = installedExe,
                Arguments = string.Join(' ', args.Select(a => $"\"{a}\"")),
                UseShellExecute = true,
                WorkingDirectory = InstallDirectory,
            });
            return true;
        }
        catch
        {
            // No write access to AppData, or something else unexpected: run in place rather than
            // leave the user with no app at all.
            return false;
        }
    }

    private static bool IsSameDirectory(string a, string b) => string.Equals(
        Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase);

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);

        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
