using Avalonia.Media;

namespace Stramp.App;

/// <summary>
/// Icon glyphs as vector path data (24x24 viewBox, Material Design Icons geometry — freely
/// reusable interface iconography, Apache-2.0). Used via PathIcon rather than a third-party
/// icon-font package, to avoid an extra dependency for a handful of glyphs.
/// </summary>
public static class Icons
{
    public static readonly Geometry Play = Geometry.Parse("M8 5v14l11-7z");
    public static readonly Geometry Pause = Geometry.Parse("M6 19h4V5H6v14zm8-14v14h4V5h-4z");
    public static readonly Geometry SkipNext = Geometry.Parse("M6 18l8.5-6L6 6v12zM16 6v12h2V6h-2z");
    public static readonly Geometry SkipPrevious = Geometry.Parse("M6 6h2v12H6zm3.5 6l8.5 6V6z");
    public static readonly Geometry Shuffle = Geometry.Parse(
        "M10.59 9.17L5.41 4 4 5.41l5.17 5.17 1.42-1.41zM14.5 4l2.04 2.04L4 18.59 5.41 20 17.96 7.46 20 9.5V4h-5.5zm.33 9.41l-1.41 1.42 3.13 3.13L14.5 20H20v-5.5l-2.04 2.04-3.13-3.13z");
    public static readonly Geometry Refresh = Geometry.Parse(
        "M17.65 6.35A7.958 7.958 0 0012 4c-4.42 0-7.99 3.58-7.99 8s3.57 8 7.99 8c3.73 0 6.84-2.55 7.73-6h-2.08a5.998 5.998 0 01-5.65 4c-3.31 0-6-2.69-6-6s2.69-6 6-6c1.66 0 3.14.69 4.22 1.78L13 11h7V4l-2.35 2.35z");
    public static readonly Geometry VolumeHigh = Geometry.Parse(
        "M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z");
    public static readonly Geometry FolderOpen = Geometry.Parse(
        "M20 6h-8l-2-2H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2zm0 12H4V8h16v10z");
    public static readonly Geometry Search = Geometry.Parse(
        "M15.5 14h-.79l-.28-.27C15.41 12.59 16 11.11 16 9.5 16 5.91 13.09 3 9.5 3S3 5.91 3 9.5 5.91 16 9.5 16c1.61 0 3.09-.59 4.23-1.57l.27.28v.79l5 4.99L20.49 19l-4.99-5zm-6 0C7.01 14 5 11.99 5 9.5S7.01 5 9.5 5 14 7.01 14 9.5 11.99 14 9.5 14z");
    public static readonly Geometry MusicNote = Geometry.Parse(
        "M12 3v10.55c-.59-.34-1.27-.55-2-.55-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4V7h4V3h-6z");
    public static readonly Geometry Delete = Geometry.Parse(
        "M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z");
    public static readonly Geometry Menu = Geometry.Parse("M3 6h18v2H3zm0 5h18v2H3zm0 5h18v2H3z");
    public static readonly Geometry Back = Geometry.Parse("M15.41 7.41L14 6l-6 6 6 6 1.41-1.41L10.83 12z");
    public static readonly Geometry Playlist = Geometry.Parse(
        "M15 6H3v2h12V6zm0 4H3v2h12v-2zM3 16h8v-2H3v2zM17 6v8.18a3 3 0 101 2.82V8h3V6h-4z");
    public static readonly Geometry LibraryMusic = Geometry.Parse(
        "M4 6H2v14a2 2 0 002 2h14v-2H4V6zm16-4H8a2 2 0 00-2 2v12a2 2 0 002 2h12a2 2 0 002-2V4a2 2 0 00-2-2zm-6 12a2.5 2.5 0 110-5 2.5 2.5 0 010 5zm2-6V5h3V3h-4v6.5a2.5 2.5 0 101 2V8z");
    public static readonly Geometry Settings = Geometry.Parse(
        "M19.14 12.94a7.07 7.07 0 000-1.88l2.03-1.58a.5.5 0 00.12-.64l-1.92-3.32a.5.5 0 00-.6-.22l-2.39.96a7 7 0 00-1.62-.94l-.36-2.54a.5.5 0 00-.5-.42h-3.84a.5.5 0 00-.5.42l-.36 2.54c-.58.24-1.12.56-1.62.94l-2.39-.96a.5.5 0 00-.6.22L2.67 8.84a.5.5 0 00.12.64l2.03 1.58a7.07 7.07 0 000 1.88l-2.03 1.58a.5.5 0 00-.12.64l1.92 3.32c.13.22.39.3.6.22l2.39-.96c.5.38 1.04.7 1.62.94l.36 2.54c.04.24.25.42.5.42h3.84c.25 0 .46-.18.5-.42l.36-2.54c.58-.24 1.12-.56 1.62-.94l2.39.96c.22.08.47 0 .6-.22l1.92-3.32a.5.5 0 00-.12-.64l-2.03-1.58zM12 15.6A3.6 3.6 0 1112 8.4a3.6 3.6 0 010 7.2z");
    public static readonly Geometry EqualizePanels = Geometry.Parse(
        "M4 4h5v16H4V4zm11 0h5v16h-5V4zm-5 7h4v2h-4v-2z");
    public static readonly Geometry WindowMinimize = Geometry.Parse("M4 11h16v2H4z");
    public static readonly Geometry WindowMaximize = Geometry.Parse("M4 4h16v16H4V4zm2 2v12h12V6H6z");
    public static readonly Geometry WindowRestore = Geometry.Parse(
        "M8 4h12v12h-4v4H4V8h4V4zm2 2v2h6v6h2V6h-8zM6 10v8h8v-8H6z");
    public static readonly Geometry WindowClose = Geometry.Parse(
        "M18.3 5.71L12 12l6.3 6.29-1.41 1.42L10.59 13.4 4.3 19.71 2.88 18.3 9.17 12 2.88 5.71 4.3 4.29l6.29 6.3 6.3-6.3z");
}
