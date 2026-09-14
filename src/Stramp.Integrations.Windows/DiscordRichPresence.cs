using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stramp.Integrations.Windows;

public readonly record struct DiscordActivity(
    string Details,
    string State,
    string? LargeImageKey = null,
    string? LargeImageText = null,
    string? SmallImageKey = null,
    string? SmallImageText = null,
    DateTimeOffset? StartTimestamp = null,
    DateTimeOffset? EndTimestamp = null,
    string? ButtonLabel = null,
    string? ButtonUrl = null);

/// <summary>
/// Minimal Discord Rich Presence client over the local IPC named pipe (discord-ipc-0..9). Implements
/// just enough of Discord's undocumented-but-stable RPC wire format (handshake + SET_ACTIVITY frames)
/// to publish a "now playing" status. No OAuth, no join/spectate, no inbound commands.
///
/// Runs its own connect/reconnect loop on a background task so callers never block: if Discord isn't
/// running yet (or quits and restarts), SetActivity calls are simply cached and sent once a pipe
/// connects.
/// </summary>
public sealed class DiscordRichPresence : IDisposable
{
    private enum Opcode
    {
        Handshake = 0,
        Frame = 1,
    }

    private const int MaxPipeIndex = 9;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);

    // Discord's IPC schema validator rejects optional fields sent as an explicit `null` (e.g.
    // "timestamps": null) with an ERROR frame instead of just ignoring them — they must be omitted
    // from the payload entirely.
    private static readonly JsonSerializerOptions ActivityJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _clientId;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _stateLock = new();

    private NamedPipeClientStream? _pipe;
    private volatile bool _connected;
    private DiscordActivity? _pendingActivity;
    private bool _pendingClear;
    private Task? _connectionLoop;
    private bool _disposed;

    public DiscordRichPresence(string clientId)
    {
        _clientId = clientId;
    }

    /// <summary>Begins connecting in the background. Safe to call once; further calls are ignored.</summary>
    public void Start()
    {
        if (_connectionLoop is not null || string.IsNullOrWhiteSpace(_clientId))
            return;

        _connectionLoop = Task.Run(() => RunConnectionLoopAsync(_lifetimeCts.Token));
    }

    /// <summary>Publishes (or replaces) the current activity. Queued if Discord isn't connected yet.</summary>
    public void SetActivity(DiscordActivity activity)
    {
        lock (_stateLock)
        {
            _pendingActivity = activity;
            _pendingClear = false;
        }

        if (_connected)
            _ = TrySendActivityAsync(activity, _lifetimeCts.Token);
    }

    /// <summary>Removes the activity (e.g. playback stopped) instead of leaving a stale status.</summary>
    public void ClearActivity()
    {
        lock (_stateLock)
        {
            _pendingActivity = null;
            _pendingClear = true;
        }

        if (_connected)
            _ = TrySendClearAsync(_lifetimeCts.Token);
    }

    private async Task RunConnectionLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(token);
            }
            catch
            {
                // Discord isn't running, or dropped the pipe — fall through to the retry delay.
            }
            finally
            {
                _connected = false;
                _pipe?.Dispose();
                _pipe = null;
            }

            try
            {
                await Task.Delay(ReconnectDelay, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ConnectOnceAsync(CancellationToken token)
    {
        var pipe = await TryConnectAnyPipeAsync(token);
        if (pipe is null)
            return;

        _pipe = pipe;
        await WriteFrameAsync(pipe, Opcode.Handshake,
            JsonSerializer.Serialize(new { v = 1, client_id = _clientId }), token);

        // The first frame back is Discord's READY dispatch — its contents don't matter, only that
        // the handshake was accepted.
        await ReadFrameAsync(pipe, token);
        _connected = true;

        DiscordActivity? pending;
        bool pendingClear;
        lock (_stateLock)
        {
            pending = _pendingActivity;
            pendingClear = _pendingClear;
        }

        if (pending is { } activity)
            await TrySendActivityAsync(activity, token);
        else if (pendingClear)
            await TrySendClearAsync(token);

        // Keep draining frames so the pipe never backs up; this doubles as disconnect detection
        // (Discord quitting/restarting ends the read with an exception).
        while (!token.IsCancellationRequested)
            await ReadFrameAsync(pipe, token);
    }

    private static async Task<NamedPipeClientStream?> TryConnectAnyPipeAsync(CancellationToken token)
    {
        for (var i = 0; i <= MaxPipeIndex; i++)
        {
            var candidate = new NamedPipeClientStream(
                ".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await candidate.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, token);
                return candidate;
            }
            catch
            {
                candidate.Dispose();
            }
        }

        return null;
    }

    private async Task TrySendActivityAsync(DiscordActivity activity, CancellationToken token)
    {
        if (_pipe is not { } pipe)
            return;

        var hasTimestamps = activity.StartTimestamp is not null || activity.EndTimestamp is not null;
        var payload = JsonSerializer.Serialize(new
        {
            cmd = "SET_ACTIVITY",
            nonce = Guid.NewGuid().ToString(),
            args = new
            {
                pid = Environment.ProcessId,
                activity = new
                {
                    details = Truncate(activity.Details),
                    state = Truncate(activity.State),
                    timestamps = hasTimestamps
                        ? new
                        {
                            start = activity.StartTimestamp?.ToUnixTimeSeconds(),
                            end = activity.EndTimestamp?.ToUnixTimeSeconds(),
                        }
                        : null,
                    assets = new
                    {
                        large_image = activity.LargeImageKey,
                        large_text = Truncate(activity.LargeImageText),
                        small_image = activity.SmallImageKey,
                        small_text = Truncate(activity.SmallImageText),
                    },
                    buttons = activity.ButtonLabel is not null && activity.ButtonUrl is not null
                        ? new[] { new { label = activity.ButtonLabel, url = activity.ButtonUrl } }
                        : null,
                },
            },
        }, ActivityJsonOptions);

        try
        {
            await WriteFrameAsync(pipe, Opcode.Frame, payload, token);
        }
        catch
        {
            _connected = false;
        }
    }

    private async Task TrySendClearAsync(CancellationToken token)
    {
        if (_pipe is not { } pipe)
            return;

        var payload = JsonSerializer.Serialize(new
        {
            cmd = "SET_ACTIVITY",
            nonce = Guid.NewGuid().ToString(),
            args = new { pid = Environment.ProcessId, activity = (object?)null },
        });

        try
        {
            await WriteFrameAsync(pipe, Opcode.Frame, payload, token);
        }
        catch
        {
            _connected = false;
        }
    }

    private async Task WriteFrameAsync(
        NamedPipeClientStream pipe, Opcode opcode, string json, CancellationToken token)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), (int)opcode);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), body.Length);

        await _writeLock.WaitAsync(token);
        try
        {
            await pipe.WriteAsync(header, token);
            await pipe.WriteAsync(body, token);
            await pipe.FlushAsync(token);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task ReadFrameAsync(NamedPipeClientStream pipe, CancellationToken token)
    {
        var header = new byte[8];
        await ReadExactAsync(pipe, header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
        if (length <= 0)
            return;

        var body = new byte[length];
        await ReadExactAsync(pipe, body, token);
    }

    private static async Task ReadExactAsync(
        NamedPipeClientStream pipe, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await pipe.ReadAsync(buffer.AsMemory(offset), token);
            if (read == 0)
                throw new IOException("Discord closed the pipe.");
            offset += read;
        }
    }

    private static string? Truncate(string? value) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= 128 ? value : value[..128];

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _lifetimeCts.Cancel();
        _pipe?.Dispose();
        try
        {
            _connectionLoop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Best-effort shutdown — the process is exiting either way.
        }

        _lifetimeCts.Dispose();
        _writeLock.Dispose();
    }
}
