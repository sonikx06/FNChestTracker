using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Threading;
using ChestTracker.App.Localization;
using ChestTracker.Protocol;
using ChestTracker.Sync;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChestTracker.App.ViewModels;

public enum LatencyLevel
{
    None,
    Good,
    Fair,
    Poor,
}

public enum SignInMode
{
    Guest,
    Login,
    CreateAccount,
}

public sealed class SavedServerItem(string url, string? account, DateTimeOffset? lastConnected)
{
    public string Url { get; } = url;
    public string AccountText { get; } = account ?? Loc.T("Server.NotSignedIn");
    public string LastText { get; } = lastConnected is { } t ? t.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) : "";
}

/// <summary>Server Connection page: address, saved servers, status, ping, connect/disconnect, sign-in.</summary>
public sealed partial class ServerViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly Dispatcher _dispatcher;

    [ObservableProperty] private string _serverUrl;
    [ObservableProperty] private SavedServerItem? _selectedSaved;
    [ObservableProperty] private SyncStatus _status;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private string _latencyText = "—";
    [ObservableProperty] private LatencyLevel _latency;
    [ObservableProperty] private string? _serverInfoText;
    [ObservableProperty] private bool _isInsecure;
    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private bool _isGuest;
    [ObservableProperty] private string _accountText = "";
    [ObservableProperty] private bool _canConnect;
    [ObservableProperty] private bool _canDisconnect;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private SignInMode _mode = SignInMode.Guest;
    [ObservableProperty] private string _displayName;
    [ObservableProperty] private string _username = "";

    public ServerViewModel(MainViewModel main)
    {
        _main = main;
        _dispatcher = Dispatcher.CurrentDispatcher;
        var m = main.Services.Settings.Multiplayer;
        _serverUrl = m.ServerUrl;
        _displayName = m.SavedServers.FirstOrDefault()?.DisplayName is { Length: > 0 } n ? n : "";
        Sync.StateChanged += (_, _) => _dispatcher.BeginInvoke(Refresh);
        Loc.LanguageChanged += (_, _) => Refresh();
        Refresh();
    }

    private SyncClient Sync => _main.Services.Sync;

    public ObservableCollection<SavedServerItem> SavedServers { get; } = [];

    public SettingsViewModel Settings => _main.Settings;

    public bool AutoConnect
    {
        get => _main.Services.Settings.Multiplayer.AutoConnect;
        set
        {
            _main.Services.Settings.Multiplayer.AutoConnect = value;
            OnPropertyChanged();
            _main.Settings.SaveNow();
        }
    }

    /// <summary>The address in the box has a saved credential, so Connect needs no sign-in.</summary>
    public bool HasCredentialForUrl => _main.Services.Accounts.Find(Normalized(ServerUrl)) is not null;

    partial void OnServerUrlChanged(string value)
    {
        IsInsecure = SyncClient.IsInsecure(Normalized(value));
        OnPropertyChanged(nameof(HasCredentialForUrl));
        UpdateButtons();
    }

    partial void OnSelectedSavedChanged(SavedServerItem? value)
    {
        if (value is not null) ServerUrl = value.Url;
    }

    public void Refresh()
    {
        var s = Sync;
        Status = s.Status;
        StatusText = Loc.T("Server.Status." + s.Status);
        ErrorText = s.Status is SyncStatus.Offline or SyncStatus.Incompatible && s.LastError is { } e ? Describe(s.LastErrorCode, e) : ErrorText;
        if (s.Status == SyncStatus.Online) ErrorText = null;
        Latency = s.LatencyMs switch
        {
            null => LatencyLevel.None,
            < 80 => LatencyLevel.Good,
            < 200 => LatencyLevel.Fair,
            _ => LatencyLevel.Poor,
        };
        LatencyText = s.LatencyMs is { } ms ? $"{ms} ms" : "—";
        ServerInfoText = s.ServerStatus is { } st && s.Status == SyncStatus.Online
            ? Loc.F("Server.Info", st.ServerVersion, st.ProtocolVersion, st.OnlinePlayers)
            : null;

        var account = s.Account;
        IsSignedIn = account is not null;
        IsGuest = account?.IsGuest ?? false;
        AccountText = account is null ? Loc.T("Server.NotSignedIn")
            : account.IsGuest ? Loc.F("Server.Guest", account.DisplayName)
            : Loc.F("Server.Account", account.Username, account.DisplayName);

        SavedServers.Clear();
        foreach (var saved in _main.Services.Settings.Multiplayer.SavedServers.OrderByDescending(x => x.LastConnected))
        {
            var label = saved.PlayerId is null ? null : saved.Username is { } u ? $"{u} ({saved.DisplayName})" : Loc.F("Server.Guest", saved.DisplayName);
            SavedServers.Add(new SavedServerItem(saved.Url, label, saved.LastConnected));
        }

        OnPropertyChanged(nameof(HasCredentialForUrl));
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var active = Sync.Account is { } a && string.Equals(a.ServerUrl, Normalized(ServerUrl), StringComparison.OrdinalIgnoreCase);
        CanDisconnect = active && Status is SyncStatus.Online or SyncStatus.Connecting or SyncStatus.Offline;
        CanConnect = !string.IsNullOrWhiteSpace(ServerUrl) && !(active && Status is SyncStatus.Online or SyncStatus.Connecting);
    }

    [RelayCommand]
    private Task Test() => Run(async () =>
    {
        var st = await Sync.CheckServerAsync(ServerUrl);
        ServerInfoText = Loc.F("Server.Info", st.ServerVersion, st.ProtocolVersion, st.OnlinePlayers);
        _main.ShowToast(Loc.T("Server.TestOk"), ToastKind.Success);
    });

    /// <summary>Connects with a saved credential for this address; otherwise the sign-in form is used.</summary>
    [RelayCommand]
    private Task Connect() => Run(async () =>
    {
        var url = Normalized(ServerUrl) ?? throw new SyncApiException("invalid_url", "", 0);
        AutoConnect = true;
        if (Sync.Account is { } current && string.Equals(current.ServerUrl, url, StringComparison.OrdinalIgnoreCase))
        {
            Sync.Connect();
            return;
        }

        if (_main.Services.Accounts.Find(url) is { } saved)
        {
            await Sync.UseAccountAsync(saved);
            return;
        }

        await Sync.CheckServerAsync(url);
        // New server without a credential: leave the previous one (its saved credential stays) and ask to sign in.
        await Sync.DetachAsync();
        _main.Services.Accounts.Remember(url);
        _main.ShowToast(Loc.T("Server.SignInNeeded"), ToastKind.Info);
    });

    [RelayCommand]
    private Task Disconnect() => Run(async () =>
    {
        AutoConnect = false;
        await Sync.DisconnectAsync();
    });

    [RelayCommand]
    private Task SignIn(PasswordBox? box) => Run(async () =>
    {
        var password = box?.Password ?? "";
        switch (Mode)
        {
            case SignInMode.Guest:
                await Sync.RegisterAsync(ServerUrl, string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName);
                break;
            case SignInMode.Login:
                await Sync.LoginAsync(ServerUrl, Username, password);
                break;
            default:
                await Sync.RegisterAccountAsync(ServerUrl, Username, password, string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName);
                break;
        }

        box?.Clear();
        AutoConnect = true;
        _main.ShowToast(Loc.T("Server.SignedIn"), ToastKind.Success);
    });

    [RelayCommand]
    private Task Claim(PasswordBox? box) => Run(async () =>
    {
        await Sync.ClaimAccountAsync(Username, box?.Password ?? "");
        box?.Clear();
        _main.ShowToast(Loc.T("Server.Claimed"), ToastKind.Success);
    });

    [RelayCommand]
    private Task SignOut()
    {
        if (!_main.Services.Dialogs.Confirm(Loc.T("Server.SignOut"), Loc.T("Server.SignOutConfirm")))
        {
            return Task.CompletedTask;
        }

        return Run(() => Sync.SignOutAsync());
    }

    [RelayCommand]
    private Task UseSaved(SavedServerItem? item)
    {
        if (item is null) return Task.CompletedTask;
        ServerUrl = item.Url;
        return Connect();
    }

    [RelayCommand]
    private void Forget(SavedServerItem? item)
    {
        if (item is null) return;
        if (Sync.Account is { } a && string.Equals(a.ServerUrl, item.Url, StringComparison.OrdinalIgnoreCase))
        {
            _main.ShowToast(Loc.T("Server.ForgetActive"), ToastKind.Warning);
            return;
        }

        _main.Services.Accounts.Forget(item.Url);
        Refresh();
    }

    private async Task Run(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorText = null;
        try
        {
            await action();
        }
        catch (SyncApiException ex)
        {
            ErrorText = Describe(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            ErrorText = Loc.F("Toast.Error", ex.Message);
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    /// <summary>Localized message for a stable error code; falls back to the server's text.</summary>
    private static string Describe(string? code, string message)
    {
        if (code is null)
        {
            return message;
        }

        var key = "Server.Error." + code;
        var text = Loc.T(key);
        return text == key ? message : text;
    }

    private static string? Normalized(string? url)
    {
        try
        {
            return string.IsNullOrWhiteSpace(url) ? null : SyncClient.NormalizeUrl(url);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
