namespace Stramp.App.ViewModels;

/// <summary>
/// One entry in the playback-device picker. A record so that rebuilding the list on every refresh
/// still matches the previously selected entry by value rather than by instance.
/// </summary>
/// <param name="Id">Endpoint id, or null for the "system default" entry.</param>
/// <param name="Name">What the picker shows.</param>
public sealed record OutputDeviceRow(string? Id, string Name);
