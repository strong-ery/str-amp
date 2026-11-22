using System.Diagnostics;

#if STRAMP_WINDOWS
using Stramp.Integrations.Windows;
#else
using Stramp.Integrations.Linux;
#endif

namespace Stramp.App.Services;

/// <summary>
/// The handful of OS-specific actions the UI triggers (shortcuts, self-update, "show in file
/// manager"), behind one door so the rest of the app doesn't care which OS it is on.
/// </summary>
public static class PlatformServices
{
    public static bool IsWindows =>
#if STRAMP_WINDOWS
        true;
#else
        false;
#endif

    /// <summary>Where the app lives, shown in the setup wizard.</summary>
    public static string InstallDirectory =>
#if STRAMP_WINDOWS
        WindowsAppInstaller.InstallDirectory;
#else
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
#endif

    /// <summary>"Start Menu" on Windows, "application menu" elsewhere.</summary>
    public static string MenuName => IsWindows ? "Start Menu" : "application menu";

    /// <summary>"Windows File Explorer" / "file manager".</summary>
    public static string FileManagerName => IsWindows ? "Windows File Explorer" : "file manager";

    /// <summary>Windows only: relocate a portable run into the per-user install folder. True means exit now.</summary>
    public static bool TryRelocateToInstallDirectory(string[] args) =>
#if STRAMP_WINDOWS
        OperatingSystem.IsWindows() && WindowsAppInstaller.TryRelocateToInstallDirectory(args);
#else
        false;
#endif

    public static bool SetDesktopShortcut(bool enabled) =>
#if STRAMP_WINDOWS
        WindowsShortcutService.SetDesktopShortcut(enabled);
#else
        LinuxShortcutService.SetDesktopShortcut(enabled, ReadIconPng());
#endif

    public static bool SetMenuShortcut(bool enabled) =>
#if STRAMP_WINDOWS
        WindowsShortcutService.SetStartMenuShortcut(enabled);
#else
        LinuxShortcutService.SetApplicationMenuEntry(enabled, ReadIconPng());
#endif

    public static bool TryApplyUpdate(string stagingDirectory, out string? error) =>
#if STRAMP_WINDOWS
        WindowsAppUpdater.TryApplyUpdate(stagingDirectory, out error);
#else
        LinuxAppUpdater.TryApplyUpdate(stagingDirectory, out error);
#endif

    /// <summary>Opens the OS file manager on a folder.</summary>
    public static void OpenFolder(string folder)
    {
        if (IsWindows)
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true, Verb = "open" });
        else
            Process.Start(new ProcessStartInfo { FileName = "xdg-open", ArgumentList = { folder }, UseShellExecute = false });
    }

    /// <summary>Opens the file manager with a file selected where the OS supports it, else its folder.</summary>
    public static void RevealFile(string filePath)
    {
        if (IsWindows)
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{filePath}\"",
                UseShellExecute = true,
            });
        else
            OpenFolder(Path.GetDirectoryName(filePath) ?? filePath);
    }

#if !STRAMP_WINDOWS
    private static byte[]? ReadIconPng()
    {
        try
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Stramp.App/Assets/Stramp.png"));
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch
        {
            return null;
        }
    }
#endif
}
