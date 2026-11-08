using ChestTracker.Core.Presence;
using ChestTracker.Sync;

namespace ChestTracker.Server.Tests;

/// <summary>Fix 2: the Discord nickname comes from the server only after an authenticated answer.</summary>
[Collection("server")]
public class NicknameVerificationTests(ServerFixture fx)
{
    private const string Url = "http://localhost/";

    [SkippableFact]
    public async Task Server_nickname_is_used_only_once_the_server_confirms_it()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        var store = new InMemoryAccountStore();
        await using var a = new TestDevice(fx, store);
        a.Client.Start();
        Assert.Null(a.Client.VerifiedIdentity);

        await a.Client.RegisterAccountAsync(Url, "nick_" + Guid.NewGuid().ToString("N")[..8], "correct horse battery", "Anna");
        await Wait.UntilAsync(() => a.Client.VerifiedIdentity is not null, "server confirms the identity");
        var verified = a.Client.VerifiedIdentity!;
        Assert.Equal("Anna", verified.DisplayName);
        Assert.False(verified.IsGuest);
        Assert.Equal(a.Client.Account!.PlayerId, verified.PlayerId);
        Assert.Equal(new ResolvedNickname("Anna", NicknameSource.Server), NicknameResolver.Resolve(verified.DisplayName, "LocalName"));

        // Rename: the server's answer updates the verified name (and triggers a presence update via StateChanged).
        var changed = 0;
        a.Client.StateChanged += (_, _) => Interlocked.Increment(ref changed);
        await a.Client.RenameAsync("Anna Two");
        Assert.Equal("Anna Two", a.Client.VerifiedIdentity!.DisplayName);
        Assert.True(changed > 0);

        // Restart with a tampered cached name: it is not trusted; after reconnecting the server's name wins.
        await a.GoOfflineAsync();
        store.Load()!.DisplayName = "Impostor";
        a.ComeBackOnline();
        await Wait.UntilAsync(() => a.Client.VerifiedIdentity is not null, "server confirms again");
        Assert.Equal("Anna Two", a.Client.VerifiedIdentity!.DisplayName);
        Assert.Equal("Anna Two", a.Client.Account!.DisplayName); // cache repaired from the server

        // Signing out removes the authenticated identity: back to the local name.
        await a.Client.SignOutAsync();
        Assert.Null(a.Client.VerifiedIdentity);
        Assert.Equal(NicknameSource.Local, NicknameResolver.Resolve(a.Client.VerifiedIdentity?.DisplayName, "LocalName").Source);
    }

    [SkippableFact]
    public async Task Detaching_to_another_server_drops_the_verified_name()
    {
        Skip.IfNot(fx.Available, fx.SkipReason);
        await using var a = new TestDevice(fx);
        a.Client.Start();
        await a.Client.RegisterAsync(Url, "Guest Gary");
        await Wait.UntilAsync(() => a.Client.VerifiedIdentity is not null, "guest confirmed");
        Assert.True(a.Client.VerifiedIdentity!.IsGuest);

        await a.Client.DetachAsync();
        Assert.Null(a.Client.VerifiedIdentity);
    }
}
