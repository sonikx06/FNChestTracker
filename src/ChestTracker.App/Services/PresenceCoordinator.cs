using System.Diagnostics;
using System.Windows.Threading;
using ChestTracker.App.Localization;
using ChestTracker.App.ViewModels;
using ChestTracker.Core.Diagnostics;
using ChestTracker.Core.Presence;
using ChestTracker.Core.Settings;

namespace ChestTracker.App.Services;

/// <summary>
/// Turns tracker state into the Discord activity. Recomputes on chest/match changes, party changes
/// (members, nickname), tracking mode and settings changes, language changes and Fortnite start/exit.
/// The <see cref="DiscordPresenceService"/> drops duplicates and throttles what actually reaches Discord.
/// </summary>
public sealed class PresenceCoordinator : IDisposable
{
    private readonly MainViewModel _main;
    private readonly DiscordPresenceService _service;
    private readonly Dispatcher _dispatcher;
    private readonly Timer _fortniteWatcher;
    private bool _fortniteRunning;

    public PresenceCoordinator(MainViewModel main, DiscordPresenceService service)
    {
        _main = main;
        _service = service;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _fortniteRunning = IsFortniteRunning();

        main.SnapshotChanged += (_, _) => Recompute();
        main.Services.Sync.StateChanged += (_, _) => _dispatcher.BeginInvoke(Recompute);
        Loc.LanguageChanged += (_, _) => Recompute();
        _fortniteWatcher = new Timer(_ => CheckFortnite(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        ApplyConfiguration();
    }

    /// <summary>What Discord shows (or should show) right now; null = hidden.</summary>
    public PresenceActivity? Current { get; private set; }

    public bool FortniteRunning => _fortniteRunning;

    /// <summary>Whether the art assets (logo/solo/team) exist in the Discord application.</summary>
    public AssetCheckResult AssetCheck { get; private set; } = new(AssetCheckStatus.Unknown, [], []);

    /// <summary>Checks the art asset keys against the Discord application (public asset list; only the app ID is sent).</summary>
    public async Task CheckAssetsAsync()
    {
        var result = await new DiscordAssetChecker().CheckAsync(DiscordApp.ApplicationId, DiscordApp.RequiredAssets);
        if (result.Status == AssetCheckStatus.Ok)
        {
            Log.Info($"Discord art assets found: {string.Join(", ", result.Found)}");
        }
        else
        {
            Log.Warn($"Discord art assets {result.Status}: missing [{string.Join(", ", result.Missing)}] found [{string.Join(", ", result.Found)}] {result.Error}");
        }

        await _dispatcher.InvokeAsync(() =>
        {
            AssetCheck = result;
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>The nickname used for the presence and where it came from (shown in the settings).</summary>
    public ResolvedNickname Nickname { get; private set; } = new(NicknameResolver.DefaultName, NicknameSource.Default);

    public DiscordPresenceService Service => _service;

    public event EventHandler? Changed;

    public void ApplyConfiguration()
    {
        var d = _main.Services.Settings.Discord;
        _service.Configure(d.Enabled, DiscordApp.ApplicationId);
        if (d.Enabled && AssetCheck.Status is AssetCheckStatus.Unknown or AssetCheckStatus.Unavailable)
        {
            _ = CheckAssetsAsync();
        }

        Recompute();
    }

    public void Recompute()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(Recompute);
            return;
        }

        try
        {
            var settings = _main.Services.Settings;
            var snapshot = _main.Snapshot;
            var sync = _main.Services.Sync;
            var party = sync.Party;
            // 1) name the server confirmed for the signed-in account, 2) local nickname, 3) "Player".
            Nickname = NicknameResolver.Resolve(sync.VerifiedIdentity?.DisplayName, settings.Discord.Nickname);

            var context = new PresenceContext
            {
                MatchCount = snapshot.MatchCount,
                SessionCount = snapshot.SessionCount,
                LifetimeCount = snapshot.LifetimeCount,
                MatchStartedAt = snapshot.CurrentMatch?.StartedAt,
                Mode = snapshot.Mode,
                Nickname = Nickname.Name,
                FortniteRunning = _fortniteRunning,
                Team = party is null
                    ? null
                    : new TeamPresence(party.Members.Count, party.MaxPlayers,
                        party.ActiveMatch is null ? null : party.TeamMatch.Verified, party.TeamLifetime.Verified),
            };

            Current = PresenceBuilder.Build(context, settings.Discord, Texts());
            _service.Update(Current);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warn("Presence update failed", ex);
        }
    }

    private static PresenceTexts Texts() => new()
    {
        Solo = Loc.T("Presence.Solo"),
        Team = Loc.T("Presence.Team"),
        MatchChests = Loc.T("Presence.MatchChests"),
        SessionChests = Loc.T("Presence.SessionChests"),
        LifetimeChests = Loc.T("Presence.LifetimeChests"),
        TeamMatchChests = Loc.T("Presence.TeamMatchChests"),
        TeamLifetimeChests = Loc.T("Presence.TeamLifetimeChests"),
        InLobby = Loc.T("Presence.InLobby"),
        LifetimeTooltip = Loc.T("Presence.LifetimeChests"),
        ButtonLabel = "GitHub",
        AppName = "Chest Tracker",
    };

    private void CheckFortnite()
    {
        var running = IsFortniteRunning();
        if (running != _fortniteRunning)
        {
            _fortniteRunning = running;
            _dispatcher.BeginInvoke(Recompute);
        }
    }

    /// <summary>Only checks whether a process with the configured name exists; the game is never touched.</summary>
    private bool IsFortniteRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName(_main.Services.Settings.Detection.ProcessName);
            var running = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
            return running;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _fortniteWatcher.Dispose();
}
