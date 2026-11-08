using System.Security.Cryptography;
using System.Text;
using ChestTracker.Core.Diagnostics;
using ChestTracker.Core.Settings;
using ChestTracker.Sync;

namespace ChestTracker.App.Services;

/// <summary>
/// Keeps one device credential per saved server in settings.json. Secrets are encrypted with Windows DPAPI
/// for the current user, so copying the settings file to another account or PC does not leak them.
/// Passwords are never stored: logging in exchanges them for a device credential.
/// </summary>
public sealed class DpapiAccountStore(AppSettings settings, SettingsStore store) : ISyncAccountStore
{
    private static readonly byte[] Entropy = "FortniteChestTracker.Sync.v1"u8.ToArray();

    private MultiplayerSettings M => settings.Multiplayer;

    /// <summary>Credential for the active server.</summary>
    public SyncAccount? Load() => Find(M.ServerUrl);

    public SyncAccount? Find(string? url)
    {
        var entry = Entry(url);
        if (entry?.PlayerId is not { } id || string.IsNullOrEmpty(entry.ProtectedSecret))
        {
            return null;
        }

        try
        {
            var secret = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(entry.ProtectedSecret), Entropy,
                DataProtectionScope.CurrentUser));
            return new SyncAccount
            {
                ServerUrl = entry.Url, PlayerId = id, Secret = secret, DisplayName = entry.DisplayName, Username = entry.Username,
            };
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Log.Warn("Stored server credential could not be decrypted; sign in again", ex);
            return null;
        }
    }

    public void Save(SyncAccount? account)
    {
        if (account is null)
        {
            // Sign out: forget the credential of the active server, keep the address.
            if (Entry(M.ServerUrl) is { } current)
            {
                current.PlayerId = null;
                current.ProtectedSecret = null;
                current.Username = null;
            }
        }
        else
        {
            var entry = Entry(account.ServerUrl);
            if (entry is null)
            {
                entry = new SavedServer { Url = account.ServerUrl };
                M.SavedServers.Add(entry);
            }

            entry.PlayerId = account.PlayerId;
            entry.DisplayName = account.DisplayName;
            entry.Username = account.Username;
            entry.LastConnected = DateTimeOffset.UtcNow;
            entry.ProtectedSecret = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(account.Secret), Entropy,
                DataProtectionScope.CurrentUser));
            M.ServerUrl = account.ServerUrl;
        }

        store.Save(settings);
    }

    /// <summary>Remembers an address without credentials.</summary>
    public void Remember(string url)
    {
        if (Entry(url) is null)
        {
            M.SavedServers.Add(new SavedServer { Url = url });
        }

        M.ServerUrl = url;
        store.Save(settings);
    }

    public void Forget(string url)
    {
        M.SavedServers.RemoveAll(s => Same(s.Url, url));
        if (Same(M.ServerUrl, url))
        {
            M.ServerUrl = "";
        }

        store.Save(settings);
    }

    private SavedServer? Entry(string? url) =>
        string.IsNullOrWhiteSpace(url) ? null : M.SavedServers.FirstOrDefault(s => Same(s.Url, url));

    private static bool Same(string? a, string? b) =>
        string.Equals(a?.TrimEnd('/'), b?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}
