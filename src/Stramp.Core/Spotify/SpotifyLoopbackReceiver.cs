using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web;

namespace Stramp.Core.Spotify;

/// <summary>Waits on a local port for the browser to return from Spotify's sign-in page.</summary>
/// <remarks>
/// Listens on 127.0.0.1 port 8888, so <see cref="RedirectUri"/> is always
/// <c>http://127.0.0.1:8888/callback</c>: Spotify's dashboard only accepts an http redirect with an
/// explicit port, and the user registers this exact value once. If another program holds the port,
/// construction fails with a <see cref="SpotifyException"/> that says so. Each connection is read on its own with a short timeout, so a browser's idle speculative
/// connections and stray requests such as a favicon never hold up the real answer. Dispose stops
/// listening.
/// </remarks>
public sealed class SpotifyLoopbackReceiver : IDisposable
{
    public const int Port = 8888;
    public const string RedirectUri = "http://127.0.0.1:8888/callback";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private readonly TcpListener _listener = new(IPAddress.Loopback, Port);

    public SpotifyLoopbackReceiver()
    {
        try
        {
            _listener.Start();
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            throw new SpotifyException(
                $"Another program is using port {Port}, which Stramp needs to finish the Spotify sign-in. Close it and try again.");
        }
    }

    // Completes with the authorization code once a request arrives carrying this sign-in's state.
    public async Task<string> ReceiveCodeAsync(string expectedState, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => answer.TrySetCanceled(ct));

        while (!answer.Task.IsCompleted)
        {
            var accept = _listener.AcceptTcpClientAsync(ct).AsTask();
            if (await Task.WhenAny(accept, answer.Task) != accept)
                break;

            _ = AnswerAsync(await accept, expectedState, answer);
        }

        return await answer.Task;
    }

    private static async Task AnswerAsync(TcpClient client, string expectedState, TaskCompletionSource<string> answer)
    {
        using var _ = client;
        using var timeout = new CancellationTokenSource(RequestTimeout);
        try
        {
            await using var stream = client.GetStream();
            var query = await ReadQueryAsync(stream, timeout.Token);
            if (query is null || query["state"] != expectedState)
            {
                await RespondAsync(stream, "404 Not Found", "", timeout.Token);
            }
            else if (query["code"] is { Length: > 0 } code)
            {
                await RespondAsync(stream, "200 OK", "Signed in to Spotify. You can close this tab and return to Stramp.", timeout.Token);
                answer.TrySetResult(code);
            }
            else
            {
                await RespondAsync(stream, "200 OK", "Spotify sign-in was cancelled. You can close this tab.", timeout.Token);
                answer.TrySetException(new SpotifyException("Spotify sign-in was cancelled."));
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            // A connection that drops or idles out carried no answer; the next one may.
        }
    }

    // Reads only the request line; null when it has no query string.
    private static async Task<NameValueCollection?> ReadQueryAsync(NetworkStream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(ct);
        var target = requestLine?.Split(' ') is [_, var path, ..] ? path : null;
        var queryStart = target?.IndexOf('?') ?? -1;
        return queryStart < 0 ? null : HttpUtility.ParseQueryString(target![(queryStart + 1)..]);
    }

    private static async Task RespondAsync(NetworkStream stream, string status, string message, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(
            $"<!doctype html><meta charset=\"utf-8\"><title>Stramp</title><p style=\"font-family:sans-serif\">{message}</p>");
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(body, ct);
    }

    public void Dispose() => _listener.Stop();
}
