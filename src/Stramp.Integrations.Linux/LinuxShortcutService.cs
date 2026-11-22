using System.Text;

namespace Stramp.Integrations.Linux;

/// <summary>
/// Creates and removes str-amp's freedesktop launchers: an entry in the application menu
/// (the Linux counterpart of a Start Menu shortcut) and, optionally, one on the Desktop.
/// </summary>
public static class LinuxShortcutService
{
    public const string DesktopFileName = "stramp.desktop";
    private const string IconName = "stramp";

    /// <summary>The application menu entry: <c>~/.local/share/applications/stramp.desktop</c>.</summary>
    public static bool SetApplicationMenuEntry(bool enabled, byte[]? iconPng = null) =>
        SetLauncher(Path.Combine(DataHome(), "applications", DesktopFileName), enabled, iconPng);

    /// <summary>A launcher on the user's Desktop folder.</summary>
    public static bool SetDesktopShortcut(bool enabled, byte[]? iconPng = null)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop))
            return false;

        return SetLauncher(Path.Combine(desktop, DesktopFileName), enabled, iconPng);
    }

    private static bool SetLauncher(string path, bool enabled, byte[]? iconPng)
    {
        try
        {
            if (!enabled)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return true;
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return false;

            var iconValue = InstallIcon(iconPng) ?? "audio-x-generic";

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, BuildDesktopEntry(exe, iconValue), new UTF8Encoding(false));
            MakeExecutable(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildDesktopEntry(string exe, string icon) =>
        string.Join('\n',
            "[Desktop Entry]",
            "Type=Application",
            "Name=str-amp",
            "GenericName=Music Player",
            "Comment=A fast, themeable local music player",
            $"Exec={Quote(exe)}",
            $"TryExec={exe}",
            $"Icon={icon}",
            "Terminal=false",
            "Categories=AudioVideo;Audio;Player;Music;",
            "StartupWMClass=Stramp.App",
            "StartupNotify=false",
            "") + "\n";

    /// <summary>Quotes an Exec value per the desktop entry spec (spaces and a few reserved characters).</summary>
    private static string Quote(string value)
    {
        if (value.IndexOfAny([' ', '\t', '"', '\'', '\\', '>', '<', '~', '|', '&', ';', '$', '*', '?', '#', '(', ')', '`']) < 0)
            return value;

        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$") + "\"";
    }

    /// <summary>
    /// Puts the icon where launchers look for it. Returns the icon name to reference, or null (and
    /// the caller falls back to a stock icon) when no image was supplied or it couldn't be written.
    /// </summary>
    private static string? InstallIcon(byte[]? iconPng)
    {
        if (iconPng is not { Length: > 0 })
            return null;

        try
        {
            var directory = Path.Combine(DataHome(), "icons", "hicolor", "256x256", "apps");
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, IconName + ".png"), iconPng);
            return IconName;
        }
        catch
        {
            return null;
        }
    }

    private static string DataHome()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return !string.IsNullOrEmpty(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    }

    private static void MakeExecutable(string path)
    {
        // Desktop launchers on the Desktop folder are only trusted by most file managers when executable.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }
}
