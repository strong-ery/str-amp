namespace Stramp.App.ViewModels;

/// <summary>A configured folder displayed in the settings library-source list.</summary>
public sealed class LibraryLocationRow(string path)
{
    public string Path { get; } = path;

    public string Name
    {
        get
        {
            var trimmed = Path.TrimEnd(System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar);
            return System.IO.Path.GetFileName(trimmed) is { Length: > 0 } name ? name : Path;
        }
    }
}
