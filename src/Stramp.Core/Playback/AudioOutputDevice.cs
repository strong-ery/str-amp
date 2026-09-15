namespace Stramp.Core.Playback;

/// <summary>
/// An output endpoint the backend can render to. <see cref="Id"/> is backend-specific and is what
/// gets persisted; <see cref="Name"/> is only for display and may change when hardware is renamed.
/// </summary>
/// <param name="Id">Backend-specific endpoint id, stable across restarts.</param>
/// <param name="Name">Friendly name as the OS reports it.</param>
public sealed record AudioOutputDevice(string Id, string Name);
