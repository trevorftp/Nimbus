using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// The whitelist verbs over the admin socket. The add side is shaped like ban and pinned the same
/// way; the remove side carries the decision #77 was about, which is whether the kick sweep is
/// allowed to trust the cache it reads. A removal that cannot refresh has to err towards kicking,
/// because the alternative is a removal that silently changes nothing.
/// </summary>
public class AdminWhitelistCommandTests
{
    private const string Uid = "uid-listed";

    private static WhitelistEntry Entry(string uid = Uid, string serverId = "", string name = "builder",
        long expiresAtUnix = 0) => new()
    {
        PlayerUid = uid,
        PlayerName = name,
        ServerId = serverId,
        Note = "trusted",
        AddedBy = "admin",
        CreatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ExpiresAtUnix = expiresAtUnix,
    };

    // ---- add ----

    [Fact]
    public async Task AnAddByUid_IsRecordedAndSaysEnforcementIsStillOff()
    {
        await using var harness = await AdminHarness.StartAsync();
        harness.Registry.AddWhitelistResult = Entry();
        harness.Registry.Whitelist = new List<WhitelistEntry> { Entry() };

        var reply = await harness.RunAsync(new { cmd = "whitelist-add", uid = Uid, note = "trusted" });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(Uid, reply.GetProperty("uid").GetString());
        Assert.Equal("network", reply.GetProperty("scope").GetString());
        // Listing somebody never closes the network. The switch is proxy config, and an operator
        // who reads "ok" without this would think the door had just been shut.
        Assert.False(reply.GetProperty("enforcing").GetBoolean());

        var recorded = Assert.IsType<WhitelistRequest>(harness.Registry.LastWhitelistRequest);
        Assert.Equal(Uid, recorded.PlayerUid);
        Assert.Equal("trusted", recorded.Note);
        Assert.Equal("admin", recorded.AddedBy);
    }

    [Fact]
    public async Task AnAddOnAWhitelistedNetwork_SaysEnforcementIsOn()
    {
        await using var harness = await AdminHarness.StartAsync(cfg => cfg.Whitelist.Network = true);
        harness.Registry.AddWhitelistResult = Entry();
        harness.Registry.Whitelist = new List<WhitelistEntry> { Entry() };

        var reply = await harness.RunAsync(new { cmd = "whitelist-add", uid = Uid });

        Assert.True(reply.GetProperty("enforcing").GetBoolean());
    }

    [Fact]
    public async Task AnAddByPlayerName_ResolvesTheUidFromTheLiveSession()
    {
        await using var harness = await AdminHarness.StartAsync();
        await harness.JoinAsync(Uid, "builder");
        harness.Registry.AddWhitelistResult = Entry();
        harness.Registry.Whitelist = new List<WhitelistEntry> { Entry() };

        var reply = await harness.RunAsync(new { cmd = "whitelist-add", player = "builder" });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        // A live player resolves to their uid there and then, so the entry is bound, not pending.
        Assert.False(reply.GetProperty("pending").GetBoolean());
        Assert.Equal("bound", reply.GetProperty("status").GetString());
        var recorded = Assert.IsType<WhitelistRequest>(harness.Registry.LastWhitelistRequest);
        Assert.Equal(Uid, recorded.PlayerUid);
        Assert.Equal("builder", recorded.PlayerName);
    }

    [Fact]
    public async Task AnAddByNameForSomebodyNotConnected_StoresAPendingEntry()
    {
        await using var harness = await AdminHarness.StartAsync();
        // The registry answers with the pending entry it stored: no uid, name kept, IsPending.
        harness.Registry.AddWhitelistResult = new WhitelistEntry { PlayerName = "ghost" };
        harness.Registry.Whitelist = new List<WhitelistEntry>();

        var reply = await harness.RunAsync(new { cmd = "whitelist-add", player = "ghost" });

        // No live session no longer fails the command (#104); it stores the name pending and says so.
        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.True(reply.GetProperty("pending").GetBoolean());
        Assert.Equal("pending", reply.GetProperty("status").GetString());
        Assert.Equal("ghost", reply.GetProperty("player").GetString());

        // The request the command built carried the name and no uid, which is what the registry
        // reads as pending.
        var recorded = Assert.IsType<WhitelistRequest>(harness.Registry.LastWhitelistRequest);
        Assert.Equal("ghost", recorded.PlayerName);
        Assert.Equal("", recorded.PlayerUid);
    }

    [Fact]
    public async Task AnAddWithNeitherUidNorName_AnswersWithTheUsageLine()
    {
        await using var harness = await AdminHarness.StartAsync();

        var reply = await harness.RunAsync(new { cmd = "whitelist-add" });

        Assert.Equal("need either uid or player", reply.GetProperty("reason").GetString());
        Assert.Contains("--uid", reply.GetProperty("usage").GetString());
        Assert.Null(harness.Registry.LastWhitelistRequest);
    }

    [Fact]
    public async Task AnAddScopedToABackendWithADuration_CarriesBothToTheRegistry()
    {
        await using var harness = await AdminHarness.StartAsync(serverIds: new[] { "hub", "staff" });
        long expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400;
        harness.Registry.AddWhitelistResult = Entry(serverId: "staff", expiresAtUnix: expires);
        harness.Registry.Whitelist = new List<WhitelistEntry> { Entry(serverId: "staff") };

        var reply = await harness.RunAsync(new
        {
            cmd = "whitelist-add",
            uid = Uid,
            serverId = "staff",
            duration = 86400,
            note = "trial build access",
        });

        var recorded = Assert.IsType<WhitelistRequest>(harness.Registry.LastWhitelistRequest);
        Assert.Equal("staff", recorded.ServerId);
        Assert.Equal(86400, recorded.DurationSeconds);
        Assert.Equal("trial build access", recorded.Note);
        Assert.Equal("staff", reply.GetProperty("scope").GetString());
        Assert.Equal(expires, reply.GetProperty("expiresAtUnix").GetInt64());
    }

    [Fact]
    public async Task ARegistryThatRefusesTheEntry_IsReportedRatherThanSwallowed()
    {
        await using var harness = await AdminHarness.StartAsync();
        harness.Registry.AddWhitelistResult = null;

        var reply = await harness.RunAsync(new { cmd = "whitelist-add", uid = Uid });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("registry refused the whitelist entry", reply.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task AnAdd_WarmsTheGateCacheSoTheNextJoinAttemptSeesTheEntry()
    {
        await using var harness = await AdminHarness.StartAsync(cfg => cfg.Whitelist.Network = true);
        harness.Registry.AddWhitelistResult = Entry();
        harness.Registry.Whitelist = new List<WhitelistEntry> { Entry() };
        Assert.Null(harness.Proxy.Whitelist.FindCovering(Uid));

        await harness.RunAsync(new { cmd = "whitelist-add", uid = Uid });

        // The operator running this is usually watching somebody try to join, so waiting for the
        // next poll would be waiting in front of them.
        Assert.NotNull(harness.Proxy.Whitelist.FindCovering(Uid));
    }

    [Fact]
    public async Task AnAddWhoseCacheRefreshFails_StillReportsTheEntryAsAdded()
    {
        await using var harness = await AdminHarness.StartAsync(cfg => cfg.Whitelist.Network = true);
        harness.Registry.AddWhitelistResult = Entry();
        // The entry reached the registry; only the follow-up read back cannot.
        harness.Registry.Throw = true;

        var reply = await harness.RunAsync(new { cmd = "whitelist-add", uid = Uid });

        // The write is what the operator asked for and it landed, so the answer is yes and it
        // carries the same detail as a clean run.
        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(Uid, reply.GetProperty("uid").GetString());
        Assert.Equal("network", reply.GetProperty("scope").GetString());
        // This is the cost of the failed refresh, and it is the mirror of the warm-cache test:
        // the gate does not know about the entry yet and will not until the next poll.
        Assert.Null(harness.Proxy.Whitelist.FindCovering(Uid));
    }

    // ---- remove ----

    [Fact]
    public async Task ARemoval_DisconnectsThePlayerWhoJustLostTheirCoverage()
    {
        await using var harness = await AdminHarness.StartAsync(cfg => cfg.Whitelist.Network = true);
        await harness.WhitelistAsync(Entry());
        var player = await harness.JoinAsync(Uid, "builder");

        harness.Registry.RemoveWhitelistResult = true;
        harness.Registry.Whitelist = new List<WhitelistEntry>();
        var reply = await harness.RunAsync(new { cmd = "whitelist-remove", uid = Uid });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(1, reply.GetProperty("kicked").GetInt32());
        Assert.True(reply.GetProperty("cacheRefreshed").GetBoolean());
        Assert.True(await player.DroppedAsync());
    }

    [Fact]
    public async Task ARemovalWhoseRefreshNeverLanded_KicksAnywayAndSaysTheListWasStale()
    {
        await using var harness = await AdminHarness.StartAsync(cfg => cfg.Whitelist.Network = true);
        await harness.WhitelistAsync(Entry());
        var player = await harness.JoinAsync(Uid, "builder");

        harness.Registry.RemoveWhitelistResult = true;
        // The registry took the removal and then went quiet, so the cache still holds the entry
        // that was just dropped. Trusting it would spare every session it used to cover, which
        // is how this command once reported a kick count of zero while the player stayed on.
        harness.Registry.Whitelist = null;
        var reply = await harness.RunAsync(new { cmd = "whitelist-remove", uid = Uid });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(1, reply.GetProperty("kicked").GetInt32());
        Assert.False(reply.GetProperty("cacheRefreshed").GetBoolean());
        Assert.True(await player.DroppedAsync());
        // The entry the refresh could not drop is still sitting in the cache, which is exactly
        // the state the fail-closed sweep exists for.
        Assert.NotNull(harness.Proxy.Whitelist.FindCovering(Uid));
    }

    [Fact]
    public async Task ARemovalTheRegistryThrewOn_StillKicksRatherThanSparingThePlayer()
    {
        await using var harness = await AdminHarness.StartAsync(cfg => cfg.Whitelist.Network = true);
        await harness.WhitelistAsync(Entry());
        var player = await harness.JoinAsync(Uid, "builder");

        harness.Registry.RemoveWhitelistResult = true;
        harness.Registry.Throw = true;
        var reply = await harness.RunAsync(new { cmd = "whitelist-remove", uid = Uid });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.False(reply.GetProperty("cacheRefreshed").GetBoolean());
        Assert.Equal(1, reply.GetProperty("kicked").GetInt32());
        Assert.True(await player.DroppedAsync());
        // The removal is the registry's answer, not the cache's: the stale cache still lists the
        // player, and the kick happened anyway. Sparing them here would leave somebody on the
        // network the operator just took off it.
        Assert.NotNull(harness.Proxy.Whitelist.FindCovering(Uid));
    }

    [Fact]
    public async Task ARemovalThatLeavesOtherCoverage_LeavesThePlayerConnected()
    {
        await using var harness = await AdminHarness.StartAsync(cfg => cfg.Whitelist.Network = true);
        // Listed twice: once for the whole network, once for hub in particular.
        await harness.WhitelistAsync(Entry(), Entry(serverId: "hub"));
        var player = await harness.JoinAsync(Uid, "builder");

        harness.Registry.RemoveWhitelistResult = true;
        harness.Registry.Whitelist = new List<WhitelistEntry> { Entry() };
        var reply = await harness.RunAsync(new { cmd = "whitelist-remove", uid = Uid, serverId = "hub" });

        Assert.Equal("hub", reply.GetProperty("scope").GetString());
        // Dropping the scoped entry costs them nothing while the network-wide one stands.
        Assert.Equal(0, reply.GetProperty("kicked").GetInt32());
        Assert.False(await player.DroppedAsync(500));
        Assert.Equal(Uid, harness.Registry.LastWhitelistRemove!.Value.Uid);
        Assert.Equal("hub", harness.Registry.LastWhitelistRemove!.Value.ServerId);
    }

    [Fact]
    public async Task ARemovalOnANetworkNobodyIsGating_KicksNobody()
    {
        await using var harness = await AdminHarness.StartAsync();
        await harness.WhitelistAsync(Entry());
        var player = await harness.JoinAsync(Uid, "builder");

        harness.Registry.RemoveWhitelistResult = true;
        harness.Registry.Whitelist = new List<WhitelistEntry>();
        var reply = await harness.RunAsync(new { cmd = "whitelist-remove", uid = Uid });

        // With enforcement off the list gates nothing, so losing an entry cannot cost anyone
        // their seat.
        Assert.Equal(0, reply.GetProperty("kicked").GetInt32());
        Assert.False(await player.DroppedAsync(500));
    }

    [Fact]
    public async Task ARemovalTheRegistryFindsNothingFor_ReportsNotOkAndTouchesNobody()
    {
        await using var harness = await AdminHarness.StartAsync(cfg => cfg.Whitelist.Network = true);
        await harness.WhitelistAsync(Entry());
        var player = await harness.JoinAsync(Uid, "builder");

        harness.Registry.RemoveWhitelistResult = false;
        var reply = await harness.RunAsync(new { cmd = "whitelist-remove", uid = "uid-never-listed" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        // No removal happened, so no sweep runs and no count is reported for one.
        Assert.False(reply.TryGetProperty("kicked", out _));
        Assert.False(await player.DroppedAsync(500));
    }

    [Fact]
    public async Task ARemovalWithNoUid_AnswersWithTheUsageLine()
    {
        await using var harness = await AdminHarness.StartAsync();

        var reply = await harness.RunAsync(new { cmd = "whitelist-remove" });

        Assert.Equal("missing 'uid'", reply.GetProperty("reason").GetString());
        Assert.Contains("whitelist-remove --uid", reply.GetProperty("usage").GetString());
        Assert.Null(harness.Registry.LastWhitelistRemove);
    }

    // ---- list ----

    [Fact]
    public async Task TheListing_ShowsTheEntriesAndWhereTheyAreEnforced()
    {
        await using var harness = await AdminHarness.StartAsync(
            cfg => cfg.Whitelist.Servers = new List<string> { "staff" },
            serverIds: new[] { "hub", "staff" });
        await harness.WhitelistAsync(Entry(), Entry(uid: "uid-two", serverId: "staff", name: "mod"));

        var reply = await harness.RunAsync(new { cmd = "whitelist-list" });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(2, reply.GetProperty("count").GetInt32());
        // The list alone says nothing: the switches next to it are what turns an entry into a
        // door, and a listing without them cannot be read.
        Assert.False(reply.GetProperty("network").GetBoolean());
        Assert.Equal("staff", reply.GetProperty("servers").EnumerateArray().Single().GetString());
        Assert.True(reply.GetProperty("synced").GetBoolean());

        var scopes = reply.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetProperty("scope").GetString()).ToList();
        Assert.Contains("network", scopes);
        Assert.Contains("staff", scopes);
    }

    [Fact]
    public async Task TheListing_OnAProxyThatHasNeverReadTheList_SaysItHasNotSynced()
    {
        await using var harness = await AdminHarness.StartAsync(cfg => cfg.Whitelist.Network = true);
        harness.Registry.Whitelist = new List<WhitelistEntry>();

        var reply = await harness.RunAsync(new { cmd = "whitelist-list" });

        // An empty listing on a closed network reads as "nobody can get in", and the operator
        // needs to know whether that is the answer or the absence of one.
        Assert.Equal(0, reply.GetProperty("count").GetInt32());
        Assert.True(reply.GetProperty("network").GetBoolean());
        Assert.False(reply.GetProperty("synced").GetBoolean());
    }

    [Fact]
    public async Task TheListing_WhenTheRegistryIsUnreachable_SaysSoRatherThanShowingNothing()
    {
        await using var harness = await AdminHarness.StartAsync();
        harness.Registry.Whitelist = null;

        var reply = await harness.RunAsync(new { cmd = "whitelist-list" });

        Assert.False(reply.GetProperty("ok").GetBoolean());
        Assert.Equal("registry unreachable", reply.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task WithTheRegistryDisabled_EveryWhitelistVerbSaysWhyItCannotRun()
    {
        await using var harness = await AdminHarness.StartAsync(withRegistry: false);

        foreach (var payload in new object[]
                 {
                     new { cmd = "whitelist-add", uid = Uid },
                     new { cmd = "whitelist-remove", uid = Uid },
                     new { cmd = "whitelist-list" },
                 })
        {
            var reply = await harness.RunAsync(payload);
            Assert.False(reply.GetProperty("ok").GetBoolean());
            Assert.Equal("whitelists need a registry (registry.mode is 'disabled')",
                reply.GetProperty("reason").GetString());
        }
    }
}
