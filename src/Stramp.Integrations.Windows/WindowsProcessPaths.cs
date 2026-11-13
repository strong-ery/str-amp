using System.Reflection;

namespace Stramp.Integrations.Windows;

/// <summary>Finds the actual .exe this process is running from, whether launched as a
/// self-contained apphost or via <c>dotnet.exe</c> during development.</summary>
internal static class WindowsProcessPaths
{
    public static string? GetExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            !string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase) &&
            File.Exists(processPath))
            return Path.GetFullPath(processPath);

        // Only reached when launched via dotnet.exe, which never happens from a single-file
        // publish - so Assembly.Location always being "" there doesn't cost us this fallback.
#pragma warning disable IL3000
        var entryLocation = Assembly.GetEntryAssembly()?.Location;
#pragma warning restore IL3000
        if (string.IsNullOrWhiteSpace(entryLocation))
            return null;
        var appHost = Path.ChangeExtension(entryLocation, ".exe");
        return File.Exists(appHost) ? Path.GetFullPath(appHost) : null;
    }
}
