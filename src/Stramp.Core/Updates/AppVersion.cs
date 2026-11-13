namespace Stramp.Core.Updates;

/// <summary>The app's own version, bumped by hand to match each GitHub release tag.</summary>
public static class AppVersion
{
    public const string Current = "2.0.0";

    /// <summary>
    /// Whether a GitHub tag like "v2.1.0" (or "2.1.0") is newer than <see cref="Current"/>. A tag
    /// equal to the running version is never "newer" - strictly greater-than, not greater-or-equal
    /// - so the app never offers to update to the exact build it's already running.
    /// </summary>
    public static bool IsNewerThanCurrent(string? tagName) =>
        TryParse(tagName, out var tag) && TryParse(Current, out var current) && tag > current;

    private static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim().TrimStart('v', 'V');
        return Version.TryParse(trimmed, out version!);
    }
}
