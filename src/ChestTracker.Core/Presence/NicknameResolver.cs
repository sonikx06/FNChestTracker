namespace ChestTracker.Core.Presence;

public enum NicknameSource
{
    /// <summary>Display name confirmed by the server in an authenticated request during this run.</summary>
    Server,

    /// <summary>Name typed into this app's settings (not verified by anyone).</summary>
    Local,

    /// <summary>Nothing configured: <see cref="NicknameResolver.DefaultName"/>.</summary>
    Default,
}

public sealed record ResolvedNickname(string Name, NicknameSource Source);

/// <summary>
/// Picks the nickname shown in Discord: 1) the authenticated nickname returned by the server,
/// 2) the locally configured nickname, 3) "Player".
/// Only a name the server confirmed for the signed-in account counts as authenticated; a name cached on disk
/// from an earlier run, or one typed locally, never does.
/// </summary>
public static class NicknameResolver
{
    public const string DefaultName = "Player";
    public const int MaxLength = 32;

    /// <param name="verifiedServerName">Server-confirmed display name, null when not authenticated.</param>
    /// <param name="localNames">Locally configured names, in priority order.</param>
    public static ResolvedNickname Resolve(string? verifiedServerName, params string?[]? localNames)
    {
        if (Clean(verifiedServerName) is { } server)
        {
            return new ResolvedNickname(server, NicknameSource.Server);
        }

        foreach (var local in localNames ?? [])
        {
            if (Clean(local) is { } name)
            {
                return new ResolvedNickname(name, NicknameSource.Local);
            }
        }

        return new ResolvedNickname(DefaultName, NicknameSource.Default);
    }

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        var text = new string(s.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (text.Length > MaxLength)
        {
            var cut = char.IsHighSurrogate(text[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
            text = text[..cut].TrimEnd();
        }

        return text.Length == 0 ? null : text;
    }
}
