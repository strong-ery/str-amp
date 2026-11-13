namespace Stramp.Core.Updates;

/// <summary>A release newer than the running build, ready to show the user.</summary>
public sealed record UpdateInfo(string Version, string? ReleaseNotes, string HtmlUrl, string? DownloadUrl);
