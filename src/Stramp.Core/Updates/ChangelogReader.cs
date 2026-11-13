namespace Stramp.Core.Updates;

/// <summary>Pulls one version's section out of a Keep-a-Changelog-formatted CHANGELOG.md.</summary>
public static class ChangelogReader
{
    /// <summary>
    /// The text under the "## [version]" heading for the given version, up to (not including) the
    /// next "## [" heading, with the heading line itself stripped out. Null if that version has no
    /// section. The heading may or may not carry a leading "v" ("## [v2.0.0]" and "## [2.0.0]"
    /// both match a <paramref name="version"/> of "2.0.0" or "v2.0.0".
    /// </summary>
    public static string? FindSection(string changelog, string version)
    {
        var lines = changelog.Replace("\r\n", "\n").Split('\n');
        var start = -1;
        var end = lines.Length;

        for (var i = 0; i < lines.Length; i++)
        {
            if (start < 0)
            {
                if (IsHeadingFor(lines[i], version))
                    start = i + 1;
            }
            else if (IsHeading(lines[i]))
            {
                end = i;
                break;
            }
        }

        return start < 0 ? null : string.Join('\n', lines[start..end]).Trim();
    }

    private static bool IsHeading(string line) => line.TrimStart().StartsWith("## [", StringComparison.Ordinal);

    private static bool IsHeadingFor(string line, string version)
    {
        if (!IsHeading(line))
            return false;

        var afterBracket = line.TrimStart()[4..];
        var closeBracket = afterBracket.IndexOf(']');
        if (closeBracket < 0)
            return false;

        var tag = afterBracket[..closeBracket].Trim().TrimStart('v', 'V');
        return string.Equals(tag, version.Trim().TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase);
    }
}
