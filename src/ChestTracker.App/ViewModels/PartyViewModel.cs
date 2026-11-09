using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using ChestTracker.App.Localization;
using ChestTracker.Protocol;
using ChestTracker.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChestTracker.App.ViewModels;

public sealed class PartyMemberItem
{
    public PartyMemberItem(MemberDto m, bool isMe, bool viewerIsOwner, bool matchActive)
    {
        Member = m;
        IsMe = isMe;
        Name = isMe ? $"{m.DisplayName} ({Loc.T("Party.You")})" : m.DisplayName;
        IsOwner = m.Role == PartyRole.Owner;
        Online = m.Online;
        Hidden = m.Lifetime is null;
        MatchText = Hidden ? "—" : matchActive && m.Match is { } mc ? Format(mc) : "—";
        LifetimeText = Hidden ? Loc.T("Party.Hidden") : Format(m.Lifetime!);
        ResponseText = m.MatchFinished ? Loc.T("Party.Finished") : m.MatchResponse switch
        {
            ParticipantResponse.Accepted => Loc.T("Party.InMatch"),
            ParticipantResponse.Pending => Loc.T("Party.Invited"),
            ParticipantResponse.Declined => Loc.T("Party.Declined"),
            _ => "",
        };
        CanManage = viewerIsOwner && !isMe;
    }

    public MemberDto Member { get; }
    public bool IsMe { get; }
    public string Name { get; }
    public bool IsOwner { get; }
    public bool Online { get; }
    public bool Hidden { get; }
    public string MatchText { get; }
    public string LifetimeText { get; }
    public string ResponseText { get; }
    public bool CanManage { get; }

    /// <summary>"verified" or "verified (reported)" when they differ.</summary>
    public static string Format(CountPair c) =>
        c.Verified == c.Reported ? c.Verified.ToString(CultureInfo.InvariantCulture) : $"{c.Verified} ({c.Reported})";
}

public sealed class LeaderboardItem(LeaderboardEntryDto e, bool isMe)
{
    public string Rank { get; } = "#" + e.Rank;
    public string Name { get; } = e.DisplayName;
    public string Verified { get; } = e.Verified.ToString(CultureInfo.InvariantCulture);
    public string Reported { get; } = e.Reported.ToString(CultureInfo.InvariantCulture);
    public bool IsMe { get; } = isMe;
    public bool IsFirst { get; } = e.Rank == 1;
}

public sealed class SharedHistoryItem(SharedMatchHistoryDto h)
{
    public string StartText { get; } = h.StartedAt.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
    public string DurationText { get; } = h.EndedAt is { } e ? Loc.Duration(e - h.StartedAt) : Loc.T("Hist.Active");
    public string TeamText { get; } = PartyMemberItem.Format(h.Team);
    public string PlayersText { get; } = string.Join(", ",
        h.Participants.Select(p => $"{p.DisplayName} {(p.Counts is { } c ? PartyMemberItem.Format(c) : "—")}"));
}

/// <summary>Party page: account, party management, team counters, shared matches, leaderboards.</summary>
public sealed partial class PartyViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly Dispatcher _dispatcher;
    private SyncClient Sync => _main.Services.Sync;

    [ObservableProperty] private string _serverUrl;
    [ObservableProperty] private string _displayName;
    [ObservableProperty] private string _newPartyName = "";
    [ObservableProperty] private int _newPartySize = 4;
    [ObservableProperty] private string _joinCode = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private SyncStatus _status;
    [ObservableProperty] private bool _isRegistered;
    [ObservableProperty] private bool _inParty;
    [ObservableProperty] private bool _isOwner;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _partyName = "";
    [ObservableProperty] private string _inviteCode = "";
    [ObservableProperty] private string _inviteExpires = "";
    [ObservableProperty] private string _membersText = "";
    [ObservableProperty] private string _teamMatchText = "—";
    [ObservableProperty] private string _teamLifetimeText = "0";
    [ObservableProperty] private string _sharedMatchText = "";
    [ObservableProperty] private bool _hasActiveMatch;
    [ObservableProperty] private bool _hasPendingInvite;
    [ObservableProperty] private bool _shareCounts = true;
    [ObservableProperty] private bool _shareActivity = true;
    [ObservableProperty] private LocalizedOption<LeaderboardPeriod> _selectedPeriod;
    [ObservableProperty] private bool _hasLeaderboard;
    [ObservableProperty] private bool _hasHistory;

    private bool _applyingSharing;

    public PartyViewModel(MainViewModel main)
    {
        _main = main;
        _dispatcher = Dispatcher.CurrentDispatcher;
        var m = main.Services.Settings.Multiplayer;
        _serverUrl = m.ServerUrl;
        _displayName = string.IsNullOrWhiteSpace(m.DisplayName) ? Environment.UserName : m.DisplayName;
        Periods = Enum.GetValues<LeaderboardPeriod>().Select(p => new LocalizedOption<LeaderboardPeriod>(p, "Party.Period." + p)).ToList();
        _selectedPeriod = Periods[0];
        Sync.StateChanged += (_, _) => _dispatcher.BeginInvoke(() =>
        {
            Refresh();
            ScheduleRemoteRefresh();
        });
        Loc.LanguageChanged += (_, _) => Refresh();
        Refresh();
    }

    public IReadOnlyList<LocalizedOption<LeaderboardPeriod>> Periods { get; }
    public IReadOnlyList<int> PartySizes { get; } = [1, 2, 3, 4];
    public ObservableCollection<PartyMemberItem> Members { get; } = [];
    public ObservableCollection<LeaderboardItem> Leaderboard { get; } = [];
    public ObservableCollection<SharedHistoryItem> History { get; } = [];
    public SettingsViewModel Settings => _main.Settings;

    public void OnNavigatedTo() => _ = RefreshRemoteAsync();

    private DateTime _lastRemoteRefresh;
    private bool _remoteRefreshQueued;

    /// <summary>Live updates refresh leaderboard/history at most every 3 seconds while the page is open.</summary>
    private void ScheduleRemoteRefresh()
    {
        if (_main.CurrentPage != this || _remoteRefreshQueued) return;
        var wait = TimeSpan.FromSeconds(3) - (DateTime.UtcNow - _lastRemoteRefresh);
        _remoteRefreshQueued = true;
        _ = Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero).ContinueWith(_ => _dispatcher.BeginInvoke(async () =>
        {
            _remoteRefreshQueued = false;
            _lastRemoteRefresh = DateTime.UtcNow;
            await RefreshRemoteAsync();
        }));
    }

    private void Refresh()
    {
        var s = Sync;
        Status = s.Status;
        IsRegistered = s.Account is not null;
        StatusText = Loc.T("Party.Status." + s.Status)
                     + (s.PendingCount > 0 ? "  ·  " + Loc.F("Party.Pending", s.PendingCount) : "")
                     + (s.Status == SyncStatus.Offline && s.LastError is { } err ? "  ·  " + err : "");
        if (s.Account is { } a)
        {
            DisplayNameIfIdle(a.DisplayName);
        }

        var party = s.Party;
        InParty = party is not null;
        IsOwner = s.IsOwner;
        Members.Clear();
        if (party is null)
        {
            HasActiveMatch = false;
            HasPendingInvite = false;
            return;
        }

        var me = s.Account?.PlayerId;
        PartyName = party.Name;
        InviteCode = party.InviteCode is { } code ? InviteCodes.Format(code) : "—";
        InviteExpires = party.InviteExpiresAt is { } exp ? Loc.F("Party.Expires", exp.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture)) : "";
        MembersText = $"{party.Members.Count}/{party.MaxPlayers}";
        foreach (var member in party.Members)
        {
            Members.Add(new PartyMemberItem(member, member.PlayerId == me, IsOwner, party.ActiveMatch is not null));
        }

        HasActiveMatch = party.ActiveMatch is not null;
        TeamMatchText = HasActiveMatch ? PartyMemberItem.Format(party.TeamMatch) : "—";
        TeamLifetimeText = PartyMemberItem.Format(party.TeamLifetime);
        SharedMatchText = party.ActiveMatch is { } am
            ? Loc.F("Party.MatchRunning", Loc.Duration(DateTimeOffset.UtcNow - am.StartedAt))
            : Loc.T("Party.NoSharedMatch");
        var mine = party.Members.FirstOrDefault(x => x.PlayerId == me);
        HasPendingInvite = mine?.MatchResponse == ParticipantResponse.Pending;
        if (mine is not null)
        {
            _applyingSharing = true;
            ShareCounts = mine.ShareCounts;
            ShareActivity = mine.ShareActivity;
            _applyingSharing = false;
        }
    }

    private void DisplayNameIfIdle(string name)
    {
        if (!string.IsNullOrWhiteSpace(name) && !IsBusy)
        {
            DisplayName = name;
        }
    }

    private async Task RefreshRemoteAsync()
    {
        if (!InParty || Status != SyncStatus.Online)
        {
            Leaderboard.Clear();
            History.Clear();
            HasLeaderboard = HasHistory = false;
            return;
        }

        try
        {
            var me = Sync.Account?.PlayerId;
            var board = await Sync.GetLeaderboardAsync(SelectedPeriod.Value);
            var history = await Sync.GetHistoryAsync(10);
            Leaderboard.Clear();
            foreach (var e in board) Leaderboard.Add(new LeaderboardItem(e, e.PlayerId == me));
            History.Clear();
            foreach (var h in history) History.Add(new SharedHistoryItem(h));
            HasLeaderboard = Leaderboard.Count > 0;
            HasHistory = History.Count > 0;
        }
        catch (Exception ex)
        {
            _main.ShowToast(Loc.F("Toast.Error", ex.Message), ToastKind.Error);
        }
    }

    partial void OnSelectedPeriodChanged(LocalizedOption<LeaderboardPeriod> value) => _ = RefreshRemoteAsync();

    partial void OnShareCountsChanged(bool value) => PushSharing();

    partial void OnShareActivityChanged(bool value) => PushSharing();

    private void PushSharing()
    {
        if (_applyingSharing || !InParty) return;
        _ = Run(() => Sync.SetSharingAsync(ShareCounts, ShareActivity));
    }

    [RelayCommand]
    private Task Register() => Run(async () =>
    {
        _main.Services.Settings.Multiplayer.ServerUrl = ServerUrl.Trim();
        await Sync.RegisterAsync(ServerUrl, DisplayName);
    });

    [RelayCommand]
    private Task SaveName() => Run(() => Sync.RenameAsync(DisplayName));

    [RelayCommand]
    private Task CreateParty() => Run(async () =>
    {
        await Sync.CreatePartyAsync(string.IsNullOrWhiteSpace(NewPartyName) ? Loc.F("Party.DefaultName", DisplayName) : NewPartyName, NewPartySize);
        await RefreshRemoteAsync();
    });

    [RelayCommand]
    private Task JoinParty() => Run(async () =>
    {
        await Sync.JoinPartyAsync(JoinCode);
        JoinCode = "";
        await RefreshRemoteAsync();
    });

    [RelayCommand]
    private void CopyCode()
    {
        if (!string.IsNullOrEmpty(InviteCode) && InviteCode != "—")
        {
            try
            {
                Clipboard.SetText(InviteCode);
                _main.ShowToast(Loc.T("Party.Copied"), ToastKind.Success);
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Clipboard busy; ignore.
            }
        }
    }

    [RelayCommand]
    private Task RegenerateCode() => Run(() => Sync.RegenerateInviteAsync());

    [RelayCommand]
    private Task StartShared() => Run(() => Sync.StartSharedMatchAsync());

    [RelayCommand]
    private Task EndShared() => Run(() => Sync.EndSharedMatchAsync());

    [RelayCommand]
    private Task AcceptInvite() => Run(() => Sync.Party?.ActiveMatch is { } m ? Sync.RespondSharedMatchAsync(m.Id, true) : Task.CompletedTask);

    [RelayCommand]
    private Task DeclineInvite() => Run(() => Sync.Party?.ActiveMatch is { } m ? Sync.RespondSharedMatchAsync(m.Id, false) : Task.CompletedTask);

    [RelayCommand]
    private Task Kick(PartyMemberItem? m) => m is null ? Task.CompletedTask : Run(() => Sync.KickAsync(m.Member.PlayerId));

    [RelayCommand]
    private Task MakeOwner(PartyMemberItem? m) => m is null ? Task.CompletedTask : Run(() => Sync.TransferOwnershipAsync(m.Member.PlayerId));

    [RelayCommand]
    private Task Leave()
    {
        if (!_main.Services.Dialogs.Confirm(Loc.T("Party.Leave"), Loc.T("Party.LeaveConfirm")))
        {
            return Task.CompletedTask;
        }

        return Run(() => Sync.LeavePartyAsync());
    }

    [RelayCommand]
    private Task DeleteServerData()
    {
        if (!_main.Services.Dialogs.Confirm(Loc.T("Party.DeleteData"), Loc.T("Party.DeleteDataConfirm"), danger: true))
        {
            return Task.CompletedTask;
        }

        return Run(async () =>
        {
            await Sync.DeleteServerDataAsync();
            _main.ShowToast(Loc.T("Party.DataDeleted"), ToastKind.Success);
        });
    }

    [RelayCommand]
    private Task SignOut() => Run(() => Sync.SignOutAsync());

    [RelayCommand]
    private Task RefreshAll() => Run(async () =>
    {
        await Sync.RefreshPartyAsync();
        await RefreshRemoteAsync();
    });

    private async Task Run(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await action();
        }
        catch (SyncApiException ex)
        {
            var key = "Party.Error." + ex.Code;
            var text = Loc.T(key);
            _main.ShowToast(text == key ? ex.Message : text, ToastKind.Error);
        }
        catch (Exception ex)
        {
            _main.ShowToast(Loc.F("Toast.Error", ex.Message), ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }
}
