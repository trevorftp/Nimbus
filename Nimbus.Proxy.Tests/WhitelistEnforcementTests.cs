using System.Net;
using System.Net.Sockets;
using System.Text;
using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// Drives real socket sessions to pin the whitelist gate in the same four places a scoped ban is
/// enforced (#64): the connection gate, the two transfer methods, and the sticky route a
/// reconnect consumes. The switches live in proxy config, so every case here is the pair
/// "enforcement on for X" plus "this player is or is not covered on X".
/// </summary>
public class WhitelistEnforcementTests
{
    private const string Uid = "uid-not-on-the-list";
    private const string ListedUid = "uid-on-the-list";

    [Fact]
    public async Task UnlistedPlayer_LandingOnAWhitelistedBackend_IsDroppedAtTheDoor()
    {
        using var creative = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var cfg = Config(("creative", creative));
        cfg.Whitelist.Servers = new() { "creative" };
        var whitelist = await WhitelistWith(cts.Token,
            new WhitelistEntry { PlayerUid = ListedUid, ServerId = "creative" });

        using var live = await StartAsync(cfg, new StickyRouteTable(), whitelist, cts.Token);
        await live.SendAsync(ClientFrames.Identification(Uid, "stranger"), cts.Token);

        var back = Encoding.UTF8.GetString(await live.ReadClientBytesAsync(2000, cts.Token));
        Assert.Contains("This server is whitelisted.", back);
        // The per-server scope leaves the rest of the network reachable, so the wording must not
        // send them away from the whole thing.
        Assert.DoesNotContain("This network is whitelisted.", back);

        // The gate fired before any upstream was dialed, so the backend saw nothing at all.
        Assert.Equal(0, creative.Connections);
        Assert.Equal(0, creative.BytesReceived);
    }

    [Fact]
    public async Task ListedPlayer_LandingOnAWhitelistedBackend_GoesStraightThrough()
    {
        using var creative = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var cfg = Config(("creative", creative));
        cfg.Whitelist.Servers = new() { "creative" };
        var whitelist = await WhitelistWith(cts.Token,
            new WhitelistEntry { PlayerUid = ListedUid, PlayerName = "builder", ServerId = "creative" });

        using var live = await StartAsync(cfg, new StickyRouteTable(), whitelist, cts.Token);
        await live.SendAsync(ClientFrames.Identification(ListedUid, "builder"), cts.Token);

        await WaitFor(() => creative.Sent(ListedUid), cts.Token);
        Assert.Equal(1, creative.Connections);
    }

    [Fact]
    public async Task EntryScopedToAnotherBackend_IsNotCoverageHere()
    {
        using var creative = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var cfg = Config(("creative", creative));
        cfg.Whitelist.Servers = new() { "creative" };
        // Listed on staff, landing on creative: a scoped entry covers its own backend and no
        // other, which is the whole point of the scope.
        var whitelist = await WhitelistWith(cts.Token,
            new WhitelistEntry { PlayerUid = Uid, ServerId = "staff" });

        using var live = await StartAsync(cfg, new StickyRouteTable(), whitelist, cts.Token);
        await live.SendAsync(ClientFrames.Identification(Uid, "stranger"), cts.Token);

        var back = Encoding.UTF8.GetString(await live.ReadClientBytesAsync(2000, cts.Token));
        Assert.Contains("This server is whitelisted.", back);
        Assert.Equal(0, creative.Connections);
    }

    [Fact]
    public async Task NetworkEnforcement_TellsThePlayerTheNetworkIsClosed()
    {
        using var hub = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var cfg = Config(("hub", hub));
        cfg.Whitelist.Network = true;
        var whitelist = await WhitelistWith(cts.Token,
            new WhitelistEntry { PlayerUid = ListedUid });

        using var live = await StartAsync(cfg, new StickyRouteTable(), whitelist, cts.Token);
        await live.SendAsync(ClientFrames.Identification(Uid, "stranger"), cts.Token);

        var back = Encoding.UTF8.GetString(await live.ReadClientBytesAsync(2000, cts.Token));
        Assert.Contains("This network is whitelisted.", back);
        Assert.Equal(0, hub.Connections);
    }

    [Fact]
    public async Task NetworkWideEntry_CoversEveryBackend()
    {
        using var creative = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var cfg = Config(("creative", creative));
        cfg.Whitelist.Network = true;
        var whitelist = await WhitelistWith(cts.Token,
            new WhitelistEntry { PlayerUid = ListedUid, PlayerName = "builder" });

        using var live = await StartAsync(cfg, new StickyRouteTable(), whitelist, cts.Token);
        await live.SendAsync(ClientFrames.Identification(ListedUid, "builder"), cts.Token);

        await WaitFor(() => creative.Sent(ListedUid), cts.Token);
        Assert.Equal(1, creative.Connections);
    }

    [Fact]
    public async Task WithEnforcementOff_AnEmptyListLetsEveryoneIn()
    {
        using var hub = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var cfg = Config(("hub", hub));
        var whitelist = await WhitelistWith(cts.Token);

        using var live = await StartAsync(cfg, new StickyRouteTable(), whitelist, cts.Token);
        await live.SendAsync(ClientFrames.Identification(Uid, "stranger"), cts.Token);

        await WaitFor(() => hub.Sent(Uid), cts.Token);
        Assert.Equal(1, hub.Connections);
    }

    [Fact]
    public async Task AnEmptyListWithEnforcementOn_MeansNobody()
    {
        using var hub = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var cfg = Config(("hub", hub));
        cfg.Whitelist.Network = true;
        // Fetched and genuinely empty, which is "nobody" and never "everybody": the first
        // operator to clear the list must not open the network by accident.
        var whitelist = await WhitelistWith(cts.Token);

        using var live = await StartAsync(cfg, new StickyRouteTable(), whitelist, cts.Token);
        await live.SendAsync(ClientFrames.Identification(ListedUid, "builder"), cts.Token);

        var back = Encoding.UTF8.GetString(await live.ReadClientBytesAsync(2000, cts.Token));
        Assert.Contains("This network is whitelisted.", back);
        Assert.Equal(0, hub.Connections);
    }

    [Fact]
    public async Task ABannedPlayerWhoIsAlsoWhitelisted_StaysOutWithTheBanMessage()
    {
        using var hub = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var cfg = Config(("hub", hub));
        cfg.Whitelist.Network = true;
        var bans = await BansWith(cts.Token,
            new NetworkBan { PlayerUid = ListedUid, Reason = "griefing" });
        var whitelist = await WhitelistWith(cts.Token,
            new WhitelistEntry { PlayerUid = ListedUid });

        using var live = await StartAsync(cfg, new StickyRouteTable(), whitelist, cts.Token, bans);
        await live.SendAsync(ClientFrames.Identification(ListedUid, "builder"), cts.Token);

        var back = Encoding.UTF8.GetString(await live.ReadClientBytesAsync(2000, cts.Token));
        Assert.Contains("banned from this network", back);
        Assert.DoesNotContain("whitelisted", back);
        Assert.Equal(0, hub.Connections);
    }

    [Fact]
    public async Task ColdCache_RefusesEveryJoinByDefault()
    {
        using var hub = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var cfg = Config(("hub", hub));
        cfg.Whitelist.Network = true;
        // Never refreshed: the registry has not answered once since boot, so an empty list here
        // is not an answer and the door stays shut.
        var whitelist = new WhitelistCache(new FakeRegistryClient(), cts.Token);
        Assert.False(whitelist.HasSynced);

        using var live = await StartAsync(cfg, new StickyRouteTable(), whitelist, cts.Token);
        await live.SendAsync(ClientFrames.Identification(ListedUid, "builder"), cts.Token);

        var back = Encoding.UTF8.GetString(await live.ReadClientBytesAsync(2000, cts.Token));
        Assert.Contains("This network is whitelisted.", back);
        Assert.Equal(0, hub.Connections);
    }

    [Fact]
    public async Task ColdCache_LetsPlayersInWhenTheOperatorAskedForThat()
    {
        using var hub = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var cfg = Config(("hub", hub));
        cfg.Whitelist.Network = true;
        cfg.Whitelist.FailOpenUntilFirstSync = true;
        var whitelist = new WhitelistCache(new FakeRegistryClient(), cts.Token);

        using var live = await StartAsync(cfg, new StickyRouteTable(), whitelist, cts.Token);
        await live.SendAsync(ClientFrames.Identification(Uid, "stranger"), cts.Token);

        await WaitFor(() => hub.Sent(Uid), cts.Token);
        Assert.Equal(1, hub.Connections);
    }

    // The kick sweep in whitelist-remove reads the cache to decide who has lost their last
    // coverage, so it has to be able to tell an answered refresh from an unanswered one. When it
    // could not, a removal whose refresh failed left the dropped entry sitting in the cache,
    // spared every session it used to cover, and reported a kick count of zero.
    [Fact]
    public async Task Refresh_SaysWhetherTheRegistryAnswered()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var registry = new FakeRegistryClient
        {
            Whitelist = new List<WhitelistEntry> { new() { PlayerUid = ListedUid } },
        };
        var cache = new WhitelistCache(registry, cts.Token);

        Assert.True(await cache.RefreshAsync());
        Assert.NotNull(cache.FindCovering(ListedUid));

        // Registry error: the previous list stands, and the caller is told the new state never
        // landed rather than being handed a stale answer as if it were fresh.
        registry.Whitelist = null;
        Assert.False(await cache.RefreshAsync());
        Assert.NotNull(cache.FindCovering(ListedUid));

        // And the entry really is gone once an answer does arrive.
        registry.Whitelist = new List<WhitelistEntry>();
        Assert.True(await cache.RefreshAsync());
        Assert.Null(cache.FindCovering(ListedUid));
    }

    [Fact]
    public async Task Refresh_WithoutARegistry_ReportsNothingLanded()
    {
        var cache = new WhitelistCache(registry: null, CancellationToken.None);

        Assert.False(await cache.RefreshAsync());
        Assert.False(cache.HasSynced);
    }

    [Fact]
    public async Task RedirectToAWhitelistedBackend_IsRefusedForAnUnlistedPlayer()
    {
        using var hub = new RecordingBackend();
        using var creative = new RecordingBackend();
        using var elsewhere = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var cfg = Config(("hub", hub), ("creative", creative), ("elsewhere", elsewhere));
        cfg.Whitelist.Servers = new() { "creative" };
        var stickies = new StickyRouteTable();
        var whitelist = await WhitelistWith(cts.Token);

        using var live = await StartAsync(cfg, stickies, whitelist, cts.Token);
        await live.SendAsync(ClientFrames.Identification(Uid, "stranger"), cts.Token);
        await WaitFor(() => hub.Sent(Uid), cts.Token);

        var fail = await live.Session.RequestRedirectAsync(creative.Endpoint("creative"), registry: null,
            reason: "admin swap", failOnRegistryError: false);

        Assert.Equal("player is not whitelisted on creative", fail);
        Assert.Empty(stickies.Snapshot());
        Assert.Equal(0, creative.Connections);

        // A backend nobody is gating stays reachable for the same player.
        var ok = await live.Session.RequestRedirectAsync(elsewhere.Endpoint("elsewhere"), registry: null,
            reason: "admin swap", failOnRegistryError: false);

        Assert.Null(ok);
        var staged = Assert.Single(stickies.Snapshot());
        Assert.Equal(elsewhere.Port, staged.Target.Port);
    }

    [Fact]
    public async Task RedirectToAWhitelistedBackend_GoesAheadForACoveredPlayer()
    {
        using var hub = new RecordingBackend();
        using var creative = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var cfg = Config(("hub", hub), ("creative", creative));
        cfg.Whitelist.Servers = new() { "creative" };
        var stickies = new StickyRouteTable();
        var whitelist = await WhitelistWith(cts.Token,
            new WhitelistEntry { PlayerUid = ListedUid, ServerId = "creative" });

        using var live = await StartAsync(cfg, stickies, whitelist, cts.Token);
        await live.SendAsync(ClientFrames.Identification(ListedUid, "builder"), cts.Token);
        await WaitFor(() => hub.Sent(ListedUid), cts.Token);

        var ok = await live.Session.RequestRedirectAsync(creative.Endpoint("creative"), registry: null,
            reason: "admin swap", failOnRegistryError: false);

        Assert.Null(ok);
        var staged = Assert.Single(stickies.Snapshot());
        Assert.Equal(creative.Port, staged.Target.Port);
    }

    [Fact]
    public async Task SeamlessToAWhitelistedBackend_IsRefusedForAnUnlistedPlayer()
    {
        using var hub = new RecordingBackend();
        using var creative = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var cfg = Config(("hub", hub), ("creative", creative));
        cfg.Whitelist.Servers = new() { "creative" };
        var whitelist = await WhitelistWith(cts.Token);

        using var live = await StartAsync(cfg, new StickyRouteTable(), whitelist, cts.Token);
        await live.SendAsync(ClientFrames.Identification(Uid, "stranger"), cts.Token);
        await WaitFor(() => hub.Sent(Uid), cts.Token);

        var fail = await live.Session.RequestSeamlessAsync(creative.Endpoint("creative"), registry: null,
            swapReason: "plugin transfer", failOnRegistryError: false);

        Assert.Equal("player is not whitelisted on creative", fail);
        Assert.Equal(0, creative.Connections);
        // The refusal released the swap lock, so a later transfer is not stuck behind it.
        Assert.Equal("player is not whitelisted on creative",
            await live.Session.RequestSeamlessAsync(creative.Endpoint("creative"), registry: null,
                swapReason: "plugin transfer", failOnRegistryError: false));
    }

    [Fact]
    public async Task SeamlessToAWhitelistedBackend_GetsPastTheGateForACoveredPlayer()
    {
        using var hub = new RecordingBackend();
        using var creative = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var cfg = Config(("hub", hub), ("creative", creative));
        cfg.Whitelist.Servers = new() { "creative" };
        var whitelist = await WhitelistWith(cts.Token,
            new WhitelistEntry { PlayerUid = ListedUid, ServerId = "creative" });

        using var live = await StartAsync(cfg, new StickyRouteTable(), whitelist, cts.Token);
        await live.SendAsync(ClientFrames.Identification(ListedUid, "builder"), cts.Token);
        await WaitFor(() => hub.Sent(ListedUid), cts.Token);

        var fail = await live.Session.RequestSeamlessAsync(creative.Endpoint("creative"), registry: null,
            swapReason: "plugin transfer", failOnRegistryError: false);

        // It stops later, on the single-use mp token in the captured Identification (#57), which
        // is exactly what proves the whitelist let it through.
        Assert.NotNull(fail);
        Assert.DoesNotContain("whitelist", fail);
        Assert.Contains("already delivered", fail);
    }

    [Fact]
    public async Task StickyRouteToAWhitelistedBackend_FallsBackToDefaultRouting()
    {
        using var hub = new RecordingBackend();
        using var creative = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var cfg = Config(("hub", hub), ("creative", creative));
        cfg.Whitelist.Servers = new() { "creative" };
        var stickies = new StickyRouteTable();
        stickies.Stage(Uid, "127.0.0.1", creative.Endpoint("creative"), StickyRouteTable.UidTtl, "test transfer");

        var whitelist = await WhitelistWith(cts.Token);

        using var live = await StartAsync(cfg, stickies, whitelist, cts.Token);
        await live.SendAsync(ClientFrames.LoginTokenQuery(), cts.Token);

        await WaitFor(() => hub.Connections + creative.Connections > 0, cts.Token);
        await Task.Delay(300, cts.Token);

        // try[0] rather than the staged target, and the route is gone rather than left to fire
        // again on the next reconnect.
        Assert.Equal(1, hub.Connections);
        Assert.Equal(0, creative.Connections);
        Assert.Empty(stickies.Snapshot());
    }

    [Fact]
    public async Task StickyRouteToAWhitelistedBackend_IsHonouredForACoveredPlayer()
    {
        using var hub = new RecordingBackend();
        using var creative = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var cfg = Config(("hub", hub), ("creative", creative));
        cfg.Whitelist.Servers = new() { "creative" };
        var stickies = new StickyRouteTable();
        stickies.Stage(ListedUid, "127.0.0.1", creative.Endpoint("creative"), StickyRouteTable.UidTtl, "test transfer");

        var whitelist = await WhitelistWith(cts.Token,
            new WhitelistEntry { PlayerUid = ListedUid, ServerId = "creative" });

        using var live = await StartAsync(cfg, stickies, whitelist, cts.Token);
        await live.SendAsync(ClientFrames.LoginTokenQuery(), cts.Token);

        await WaitFor(() => hub.Connections + creative.Connections > 0, cts.Token);
        await Task.Delay(300, cts.Token);

        Assert.Equal(1, creative.Connections);
        Assert.Equal(0, hub.Connections);
    }

    // ---- harness ----

    private static ProxyConfig Config(params (string Name, RecordingBackend Backend)[] servers)
    {
        var cfg = new ProxyConfig
        {
            Servers = servers.ToDictionary(s => s.Name, s => $"127.0.0.1:{s.Backend.Port}"),
            Try = new() { servers[0].Name },
        };
        cfg.Registry.Mode = "disabled";
        return cfg;
    }

    private static async Task<WhitelistCache> WhitelistWith(CancellationToken ct, params WhitelistEntry[] entries)
    {
        var cache = new WhitelistCache(new FakeRegistryClient { Whitelist = entries.ToList() }, ct);
        await cache.RefreshAsync();
        Assert.True(cache.HasSynced);
        Assert.Equal(entries.Length, cache.Count);
        return cache;
    }

    private static async Task<BanCache> BansWith(CancellationToken ct, params NetworkBan[] entries)
    {
        var cache = new BanCache(new FakeRegistryClient { Bans = entries.ToList() }, ct);
        await cache.RefreshAsync();
        return cache;
    }

    private sealed class Live : IDisposable
    {
        private readonly TcpListener front;
        private readonly TcpClient player;

        public Live(TcpListener front, TcpClient player, ProxySession session, Task running)
        {
            this.front = front;
            this.player = player;
            Session = session;
            Running = running;
        }

        public ProxySession Session { get; }
        public Task Running { get; }

        public async Task SendAsync(byte[] frame, CancellationToken ct)
        {
            await player.GetStream().WriteAsync(frame, ct);
            await player.GetStream().FlushAsync(ct);
        }

        /// <summary>Everything the proxy wrote back to the client, until it stops or the wait runs out.</summary>
        public async Task<byte[]> ReadClientBytesAsync(int millis, CancellationToken ct)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(millis);
            var sink = new MemoryStream();
            var buf = new byte[8192];
            try
            {
                while (true)
                {
                    int read = await player.GetStream().ReadAsync(buf, readCts.Token);
                    if (read <= 0) break;
                    sink.Write(buf, 0, read);
                }
            }
            catch { }
            return sink.ToArray();
        }

        public void Dispose()
        {
            try { Session.Close(); } catch { }
            try { player.Close(); } catch { }
            try { front.Stop(); } catch { }
        }
    }

    private static async Task<Live> StartAsync(ProxyConfig cfg, StickyRouteTable stickies, WhitelistCache whitelist,
        CancellationToken ct, BanCache? bans = null)
    {
        var front = new TcpListener(IPAddress.Loopback, 0);
        front.Start();
        int frontPort = ((IPEndPoint)front.LocalEndpoint).Port;

        var events = new EventBus();
        var router = new BackendRouter(cfg, registry: null);
        var runner = new ClientSessionRunner(router, events,
            new ServerStatusResponder(cfg, registry: null, () => 0, ct), stickies, cfg, ct, bans, whitelist);

        var accepted = front.AcceptTcpClientAsync(ct).AsTask();
        var player = new TcpClient();
        await player.ConnectAsync(IPAddress.Loopback, frontPort, ct);
        var serverSide = await accepted;

        var session = new ProxySession(1, cfg, serverSide, ct,
            new SessionServices(Stickies: stickies, Events: events, Bans: bans, Whitelist: whitelist));
        return new Live(front, player, session, runner.RunAsync(session, serverSide));
    }

    private static async Task WaitFor(Func<bool> condition, CancellationToken ct, int millis = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(millis);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20, ct);
        }
        Assert.Fail("condition never became true");
    }
}
