using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ChestTracker.Core.Presence;

public sealed class DiscordRpcException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Minimal client for the Discord desktop client's local RPC pipe (\\.\pipe\discord-ipc-N), the transport used
/// for Rich Presence. Only HANDSHAKE and SET_ACTIVITY are used: no OAuth, no user token, no password, and no
/// access to anything but the user's own activity.
/// Frame: little-endian int32 opcode, int32 length, UTF-8 JSON payload.
/// </summary>
public sealed class DiscordIpcConnection : IAsyncDisposable
{
    private const int OpHandshake = 0;
    private const int OpFrame = 1;
    private const int OpClose = 2;
    private const int OpPing = 3;
    private const int OpPong = 4;
    private const int MaxPayload = 64 * 1024;

    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _readLoop;

    private DiscordIpcConnection(NamedPipeClientStream pipe, string applicationId)
    {
        _pipe = pipe;
        ApplicationId = applicationId;
    }

    public string ApplicationId { get; }

    /// <summary>Discord user name reported in READY (display only).</summary>
    public string? UserName { get; private set; }

    public bool IsConnected => _pipe.IsConnected && !_cts.IsCancellationRequested;

    public event EventHandler? Closed;

    /// <summary>Tries pipes 0вЂ“9. Returns null when Discord is not running.</summary>
    /// <exception cref="DiscordRpcException">Discord rejected the handshake (e.g. unknown application id).</exception>
    public static async Task<DiscordIpcConnection?> TryConnectAsync(string pipePrefix, string applicationId, CancellationToken ct)
    {
        foreach (var name in ExistingPipes(pipePrefix))
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(250, ct);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                await pipe.DisposeAsync();
                continue;
            }

            var connection = new DiscordIpcConnection(pipe, applicationId);
            try
            {
                await connection.HandshakeAsync(ct);
                return connection;
            }
            catch (DiscordRpcException)
            {
                await connection.DisposeAsync();
                throw;
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or JsonException or TimeoutException)
            {
                await connection.DisposeAsync();
            }
        }

        return null;
    }

    /// <summary>
    /// Pipe names that currently exist. Connecting to a missing pipe blocks a thread for the whole timeout,
    /// so listing \\.\pipe\ first keeps "Discord not running" cheap.
    /// </summary>
    private static IEnumerable<string> ExistingPipes(string prefix)
    {
        HashSet<string>? existing = null;
        try
        {
            existing = Directory.GetFiles(@"\\.\pipe\")
                .Select(p => p[(p.LastIndexOf('\\') + 1)..])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Fall back to trying every name.
        }

        for (var i = 0; i < 10; i++)
        {
            var name = prefix + i;
            if (existing is null || existing.Contains(name))
            {
                yield return name;
            }
        }
    }

    /// <summary>Sets (or with null, clears) the activity and waits for Discord's acknowledgement.</summary>
    public async Task SetActivityAsync(PresenceActivity? activity, int processId, CancellationToken ct)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var payload = new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["nonce"] = nonce,
            ["args"] = new JsonObject
            {
                ["pid"] = processId,
                ["activity"] = activity is null ? null : ToJson(activity),
            },
        };

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = tcs;
        try
        {
            await WriteAsync(OpFrame, payload, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var response = await tcs.Task.WaitAsync(timeout.Token);
            if (response?["evt"]?.GetValue<string>() == "ERROR")
            {
                var data = response["data"];
                throw new DiscordRpcException(data?["code"]?.ToString() ?? "error", data?["message"]?.GetValue<string>() ?? "Discord error");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("Discord did not answer.");
        }
        finally
        {
            _pending.TryRemove(nonce, out _);
        }
    }

    internal static JsonObject ToJson(PresenceActivity a)
    {
        var json = new JsonObject
        {
            ["details"] = a.Details,
            ["state"] = a.State,
            ["instance"] = false,
        };

        if (a.Start is { } start)
        {
            json["timestamps"] = new JsonObject { ["start"] = start.ToUnixTimeMilliseconds() };
        }

        var assets = new JsonObject();
        if (a.LargeImage is not null) assets["large_image"] = a.LargeImage;
        if (a.LargeText is not null) assets["large_text"] = a.LargeText;
        if (a.SmallImage is not null) assets["small_image"] = a.SmallImage;
        if (a.SmallText is not null) assets["small_text"] = a.SmallText;
        if (assets.Count > 0) json["assets"] = assets;

        if (a.Buttons.Count > 0)
        {
            var buttons = new JsonArray();
            foreach (var b in a.Buttons.Take(PresenceBuilder.MaxButtons))
            {
                buttons.Add(new JsonObject { ["label"] = b.Label, ["url"] = b.Url });
            }

            json["buttons"] = buttons;
        }

        return json;
    }

    private async Task HandshakeAsync(CancellationToken ct)
    {
        await WriteAsync(OpHandshake, new JsonObject { ["v"] = 1, ["client_id"] = ApplicationId }, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var (op, json) = await ReadFrameAsync(timeout.Token);
        if (op == OpClose)
        {
            throw new DiscordRpcException(json?["code"]?.ToString() ?? "closed", json?["message"]?.GetValue<string>() ?? "Handshake rejected");
        }

        if (op != OpFrame || json?["evt"]?.GetValue<string>() != "READY")
        {
            throw new IOException("Unexpected handshake response.");
        }

        var user = json["data"]?["user"];
        UserName = user?["global_name"]?.GetValue<string>() ?? user?["username"]?.GetValue<string>();
        _readLoop = Task.Run(ReadLoopAsync);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var (op, json) = await ReadFrameAsync(_cts.Token);
                if (op == OpPing)
                {
                    // Never write from the read loop: if both ends block on writes the pipe deadlocks.
                    var pong = json ?? new JsonObject();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await WriteAsync(OpPong, pong, _cts.Token);
                        }
                        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                        {
                        }
                    });
                }
                else if (op == OpClose)
                {
                    return;
                }
                else if (op == OpFrame && json?["nonce"] is JsonValue nonceValue
                         && nonceValue.TryGetValue<string>(out var nonce) && _pending.TryGetValue(nonce, out var tcs))
                {
                    tcs.TrySetResult(json);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException or ObjectDisposedException or JsonException)
        {
        }
        finally
        {
            _cts.Cancel();
            foreach (var tcs in _pending.Values)
            {
                tcs.TrySetException(new IOException("Discord connection closed."));
            }

            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task WriteAsync(int opcode, JsonNode payload, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, 8);
        await _writeLock.WaitAsync(ct);
        try
        {
            await _pipe.WriteAsync(frame, ct);
            await _pipe.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<(int Op, JsonNode? Json)> ReadFrameAsync(CancellationToken ct)
    {
        var header = new byte[8];
        await _pipe.ReadExactlyAsync(header, ct);
        var op = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length is < 0 or > MaxPayload)
        {
            throw new IOException("Invalid frame length.");
        }

        var body = new byte[length];
        await _pipe.ReadExactlyAsync(body, ct);
        return (op, length == 0 ? null : JsonNode.Parse(body));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _pipe.DisposeAsync();
        }
        catch (IOException)
        {
        }

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop;
            }
            catch
            {
                // Already handled inside the loop.
            }
        }
    }
}

