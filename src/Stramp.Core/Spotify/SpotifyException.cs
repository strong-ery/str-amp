namespace Stramp.Core.Spotify;

/// <summary>A Spotify sign-in or import failure whose message is written for the user.</summary>
/// <remarks>
/// Thrown for outcomes the user can act on: a link that is not a playlist, a declined sign-in, or a
/// playlist Spotify will not share. Network failures surface as <see cref="HttpRequestException"/>
/// instead, so the caller can word those itself.
/// </remarks>
public sealed class SpotifyException(string message) : Exception(message);
