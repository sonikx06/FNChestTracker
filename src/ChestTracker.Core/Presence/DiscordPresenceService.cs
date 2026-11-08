using ChestTracker.Core.Diagnostics;

namespace ChestTracker.Core.Presence;

public enum PresenceStatus
{
    Disabled,
    MissingApplicationId,
    Connecting,
    DiscordNotRunning,
    Connected,
    Error,
}

/// <summary>
/// Keeps Discord showing the latest desired activity. One background loop owns the connection:
/// it connects when Discord appears, reconnects after Discord restarts, coalesces rapid changes and sends
/// at most one update per <see cref="MinInterval"/> (Discord allows about 5 activity updates per 20 seconds).
/// Every failure is contained here; tracking never depends on Discord.
/// </summary>
public sealed class DiscordPresenceService : IAsyncDisposable
{
    private readonly string _pipePrefix;
    private readonly TimeSpan _retryInterval;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Task _loop;
    private DiscordIpcConnection? _connection;
    private bool _enabled;
    private string _applicationId = "";
    private PresenceActivity? _desired;
    private PresenceActivity? _sent;
    private bool _sentAnything;
    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;

    public DiscordPresenceService(string pipePrefix = "discord-ipc-", TimeSpan? minInterval = null, TimeSpan? retryInterval = null)
    {
        _pipePrefix = pipePrefix;
        MinInterval = minInterval ?? TimeSpan.FromSeconds(4);
        _retryInterval = retryInterval ?? TimeSpan.FromSeconds(15);
        _loop = Task.Run(RunAsync);
    }

    public TimeSpan MinInterval { get; }

    public PresenceStatus Status { get; private set; } = PresenceStatus.Disabled;

    public string? ConnectedUser { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Number of SET_ACTIVITY requests Discord acknowledged (diagnostics/tests).</summary>
    public int UpdatesSent { get; private set; }

    public event EventHandler? StatusChanged;

    public void Configure(bool enabled, string? applicationId)
    {
        lock (_gate)
        {
            _enabled = enabled;
            _applicationId = applicationId?.Trim() ?? "";
        }

        _signal.Release();
    }

    /// <summary>Sets the desired activity (null clears it). Cheap; call on every change.</summary>
    public void Update(PresenceActivity? activity)
    {
        lock (_gate)
        {
            if (Equals(_desired, activity))
            {
                return;
            }

            _desired = activity;
        }

        _signal.Release();
    }

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            bool enabled;
            string appId;
            PresenceActivity? desired;
            lock (_gate)
            {
                enabled = _enabled;
                appId = _applicationId;
                desired = _desired;
            }

            try
            {
                if (!enabled || appId.Length == 0)
                {
                    await DisconnectAsync(clear: true);
                    SetStatus(enabled ? PresenceStatus.MissingApplicationId : PresenceStatus.Disabled, null);
                    await WaitAsync(Timeout.InfiniteTimeSpan, ct);
                    continue;
                }

                if (_connection is null || !_connection.IsConnected || _connection.ApplicationId != appId)
                {
                    await DisconnectAsync(clear: _connection?.IsConnected == true);
                    SetStatus(PresenceStatus.Connecting, null);
                    DiscordIpcConnection? connection;
                    try
                    {
                        connection = await DiscordIpcConnection.TryConnectAsync(_pipePrefix, appId, ct);
                    }
                    catch (DiscordRpcException ex)
                    {
                        Log.Warn($"Discord rejected the application id: {ex.Code} {ex.Message}");
                        SetStatus(PresenceStatus.Error, ex.Message);
                        await WaitAsync(_retryInterval * 4, ct);
                        continue;
                    }

                    if (connection is null)
                    {
                        SetStatus(PresenceStatus.DiscordNotRunning, null);
                        await WaitAsync(_retryInterval, ct);
                        continue;
                    }

                    connection.Closed += (_, _) => _signal.Release();
                    _connection = connection;
                    _sent = null;
                    _sentAnything = false;
                    ConnectedUser = connection.UserName;
                    SetStatus(PresenceStatus.Connected, null);
                }

                var needsSend = !_sentAnything ? desired is not null : !Equals(_sent, desired);
                if (!needsSend)
                {
                    await WaitAsync(Timeout.InfiniteTimeSpan, ct);
                    continue;
                }

                var wait = _nextAllowed - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    // Throttle: later changes replace the pending one while we wait.
                    await Task.Delay(wait, ct);
                    continue;
                }

                try
                {
                    await _connection.SetActivityAsync(desired, Environment.ProcessId, ct);
                    _sent = desired;
                    _sentAnything = true;
                    UpdatesSent++;
                    if (Status != PresenceStatus.Connected || LastError is not null)
                    {
                        SetStatus(PresenceStatus.Connected, null);
                    }
                }
                catch (DiscordRpcException ex)
                {
                    // E.g. a field Discord refused; do not retry the same activity in a loop.
                    Log.Warn($"Discord refused the activity: {ex.Code} {ex.Message}");
                    _sent = desired;
                    _sentAnything = true;
                    SetStatus(PresenceStatus.Connected, ex.Message);
                }
                finally
                {
                    _nextAllowed = DateTimeOffset.UtcNow + MinInterval;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or TimeoutException)
            {
                // Discord closed or restarted: reconnect on the next pass.
                await DisconnectAsync(clear: false);
                SetStatus(PresenceStatus.DiscordNotRunning, null);
                await SafeDelay(TimeSpan.FromSeconds(2), ct);
            }
            catch (Exception ex)
            {
                Log.Error("Discord presence loop error", ex);
                await DisconnectAsync(clear: false);
                SetStatus(PresenceStatus.Error, ex.Message);
                await SafeDelay(_retryInterval, ct);
            }
        }
    }

    private async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        await _signal.WaitAsync(timeout, ct);
    }

    private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DisconnectAsync(bool clear)
    {
        var connection = _connection;
        if (connection is null)
        {
            return;
        }

        _connection = null;
        ConnectedUser = null;
        if (clear && connection.IsConnected)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await connection.SetActivityAsync(null, Environment.ProcessId, timeout.Token);
            }
            catch
            {
                // Best effort; Discord also clears the activity when the pipe closes.
            }
        }

        await connection.DisposeAsync();
    }

    private void SetStatus(PresenceStatus status, string? error)
    {
        if (Status == status && LastError == error)
        {
            return;
        }

        Status = status;
        LastError = error;
        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warn("Presence status listener failed", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            await _loop;
        }
        catch
        {
            // Loop exits on cancellation.
        }

        await DisconnectAsync(clear: true);
    }
}
