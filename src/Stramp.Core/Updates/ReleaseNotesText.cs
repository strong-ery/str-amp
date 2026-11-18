using System.Text.RegularExpressions;

namespace Stramp.Core.Updates;

/// <summary>
/// Turns a GitHub release body (Markdown) into plain text for the update dialog, which shows text
/// only: images and HTML tags are dropped, links keep their label, and emphasis/heading marks go.
/// </summary>
public static partial class ReleaseNotesText
{
    public static string ToPlain(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return "";

        var text = markdown.Replace("\r\n", "\n");
        text = ImageRegex().Replace(text, "");
        text = LinkRegex().Replace(text, "$1");
        text = HtmlTagRegex().Replace(text, "");
        text = HeadingRegex().Replace(text, "");
        text = EmphasisRegex().Replace(text, "$2");
        text = text.Replace("`", "");
        text = BlankRunRegex().Replace(text, "\n\n");
        return text.Trim();
    }

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"</?[a-zA-Z][^>]*>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s*", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"(\*\*|__|\*|_)(?=\S)(.+?)(?<=\S)\1")]
    private static partial Regex EmphasisRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRunRegex();
}
