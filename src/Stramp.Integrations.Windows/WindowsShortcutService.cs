using System.Reflection;
using System.Runtime.InteropServices;

namespace Stramp.Integrations.Windows;

/// <summary>Creates and removes Stramp-managed user shortcuts through Windows Script Host.</summary>
public static class WindowsShortcutService
{
    private const string ShortcutName = "stramp.lnk";
    private const string ManagedDescription = "Stramp music player";

    public static bool SetDesktopShortcut(bool enabled) => SetShortcut(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), enabled);

    public static bool SetStartMenuShortcut(bool enabled) => SetShortcut(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), enabled);

    private static bool SetShortcut(string directory, bool enabled)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(directory))
            return !enabled;

        var targetPath = GetExecutablePath();
        if (targetPath is null)
            return false;

        var shortcutPath = Path.Combine(directory, ShortcutName);
        try
        {
            if (!enabled)
                return RemoveManagedShortcut(shortcutPath, targetPath);

            Directory.CreateDirectory(directory);
            object? shell = null;
            object? shortcutObject = null;
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType is null || (shell = Activator.CreateInstance(shellType)) is null)
                    return false;

                dynamic dynamicShell = shell;
                shortcutObject = dynamicShell.CreateShortcut(shortcutPath);
                dynamic shortcut = shortcutObject;
                shortcut.TargetPath = targetPath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath) ?? "";
                shortcut.Description = ManagedDescription;
                shortcut.IconLocation = $"{targetPath},0";
                shortcut.Save();
                return File.Exists(shortcutPath);
            }
            finally
            {
                ReleaseComObject(shortcutObject);
                ReleaseComObject(shell);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool RemoveManagedShortcut(string shortcutPath, string targetPath)
    {
        if (!File.Exists(shortcutPath))
            return true;

        object? shell = null;
        object? shortcutObject = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null || (shell = Activator.CreateInstance(shellType)) is null)
                return false;

            dynamic dynamicShell = shell;
            shortcutObject = dynamicShell.CreateShortcut(shortcutPath);
            dynamic shortcut = shortcutObject;
            string existingTarget = shortcut.TargetPath ?? "";
            string description = shortcut.Description ?? "";
            if (!string.Equals(description, ManagedDescription, StringComparison.Ordinal) &&
                !string.Equals(existingTarget, targetPath, StringComparison.OrdinalIgnoreCase))
                return true;

            File.Delete(shortcutPath);
            return !File.Exists(shortcutPath);
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseComObject(shortcutObject);
            ReleaseComObject(shell);
        }
    }

    private static string? GetExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            !string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(processPath))
            return Path.GetFullPath(processPath);

        var entryLocation = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(entryLocation))
            return null;
        var appHost = Path.ChangeExtension(entryLocation, ".exe");
        return File.Exists(appHost) ? Path.GetFullPath(appHost) : null;
    }

    private static void ReleaseComObject(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
            Marshal.FinalReleaseComObject(instance);
    }
}
