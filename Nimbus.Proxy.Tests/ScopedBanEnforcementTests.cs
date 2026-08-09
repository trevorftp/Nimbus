using System.Net;
using System.Net.Sockets;
using System.Text;
using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// Drives real socket sessions to pin the three places a per-backend ban can be enforced: the
/// connection gate, the two transfer methods, and the sticky route a reconnect consumes. Route
/// selection is deliberately not one of them: it happens before the client has sent anything
/// carrying an identity (#57), so the UID and the destination are only ever known together from
/// Identification onwards.
/// </summary>
public class ScopedBanEnforcementTests
{
    private const string Uid = "uid-banned-from-creative";
    private const int FirstFrameTimeoutMs = 200;

    [Fact]
    public async Task ScopedBan_OnTheBackendThePlayerLandsOn_DropsThemAtTheDoor()
    {
        using var creative = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var cfg = Config(("creative", creative));
        var bans = await BanCacheWith(cts.Token,
            new NetworkBan { PlayerUid = Uid, PlayerName = "griefer", ServerId = "creative", Reason = "griefing" });

        using var live = await StartAsync(cfg, new StickyRouteTable(), bans, cts.Token);
        await live.SendAsync(ClientFrames.Identification(Uid, "griefer"), cts.Token);

        var back = await live.ReadClientBytesAsync(2000, cts.Token);
        Assert.Contains("banned from this server", Encoding.UTF8.GetString(back));
        // The wording matters: the rest of the network is still open to them.
        Assert.DoesNotContain("banned from this network", Encoding.UTF8.GetString(back));

        // The ban fired before any upstream was dialed, so the backend saw nothing at all.
        Assert.Equal(0, creative.Connections);
        Assert.Equal(0, creative.BytesReceived);
    }

    [Fact]
    public async Task ScopedBan_OnAnotherBackend_LeavesTheJoinAlone()
    {
        using var hub = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var cfg = Config(("hub", hub));
        var bans = await BanCacheWith(cts.Token,
            new NetworkBan { PlayerUid = Uid, ServerId = "creative", Reason = "griefing" });

        using var live = await StartAsync(cfg, new StickyRouteTable(), bans, cts.Token);
        await live.SendAsync(ClientFrames.Identification(Uid, "griefer"), cts.Token);

        await WaitFor(() => hub.Sent(Uid), cts.Token);
        Assert.Equal(1, hub.Connections);
    }

    [Fact]
    public async Task ScopedBan_OnTheDelayedIdentificationPath_StillDropsThePlayer()
    {
        using var creative = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var cfg = Config(("creative", creative));
        // Force the first-frame read to give up, so the pumps are already live on an open
        // upstream when Identification finally turns up.
        cfg.Status.QueryTimeoutMs = FirstFrameTimeoutMs;

        var bans = await BanCacheWith(cts.Token,
            new NetworkBan { PlayerUid = Uid, ServerId = "creative", Reason = "griefing" });

        using var live = await StartAsync(cfg, new StickyRouteTable(), bans, cts.Token);
        await WaitFor(() => creative.Connections > 0, cts.Token);

        await live.SendAsync(ClientFrames.Identification(Uid, "griefer"), cts.Token);
        var back = await live.ReadClientBytesAsync(2000, cts.Token);

        Assert.Contains("banned from this server", Encoding.UTF8.GetString(back));
        Assert.False(creative.Sent(Uid), "the banned player's Identification reached the backend");
    }

    [Fact]
    public async Task ScopedBan_OnARedirectTarget_RefusesTheTransferAndStagesNothing()
    {
        using var hub = new RecordingBackend();
        using var creative = new RecordingBackend();
        using var elsewhere = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var cfg = Config(("hub", hub), ("creative", creative), ("elsewhere", elsewhere));
        var stickies = new StickyRouteTable();
        var bans = await BanCacheWith(cts.Token,
            new NetworkBan { PlayerUid = Uid, ServerId = "creative", Reason = "griefing" });

        using var live = await StartAsync(cfg, stickies, bans, cts.Token);
        await live.SendAsync(ClientFrames.Identification(Uid, "griefer"), cts.Token);
        await WaitFor(() => hub.Sent(Uid), cts.Token);

        var fail = await live.Session.RequestRedirectAsync(creative.Endpoint("creative"), registry: null,
            reason: "admin swap", failOnRegistryError: false);

        Assert.Equal("player is banned from creative", fail);
        Assert.Empty(stickies.Snapshot());
        Assert.Equal(0, creative.Connections);

        // The same ban must not touch a transfer to any other backend.
        var ok = await live.Session.RequestRedirectAsync(elsewhere.Endpoint("elsewhere"), registry: null,
            reason: "admin swap", failOnRegistryError: false);

        Assert.Null(ok);
        var staged = Assert.Single(stickies.Snapshot());
        Assert.Equal(elsewhere.Port, staged.Target.Port);
    }

    [Fact]
    public async Task ScopedBan_OnASeamlessTarget_RefusesTheTransferAndNeverDialsIt()
    {
        using var hub = new RecordingBackend();
        using var creative = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var cfg = Config(("hub", hub), ("creative", creative));
        var bans = await BanCacheWith(cts.Token,
            new NetworkBan { PlayerUid = Uid, ServerId = "creative", Reason = "griefing" });

        using var live = await StartAsync(cfg, new StickyRouteTable(), bans, cts.Token);
        await live.SendAsync(ClientFrames.Identification(Uid, "griefer"), cts.Token);
        await WaitFor(() => hub.Sent(Uid), cts.Token);

        var fail = await live.Session.RequestSeamlessAsync(creative.Endpoint("creative"), registry: null,
            swapReason: "plugin transfer", failOnRegistryError: false);

        Assert.Equal("player is banned from creative", fail);
        Assert.Equal(0, creative.Connections);
        // The refusal released the swap lock, so a later transfer is not stuck behind it.
        Assert.Equal("player is banned from creative",
            await live.Session.RequestSeamlessAsync(creative.Endpoint("creative"), registry: null,
                swapReason: "plugin transfer", failOnRegistryError: false));
    }

    [Fact]
    public async Task StickyRouteToABannedTarget_FallsBackToDefaultRouting()
    {
        using var hub = new RecordingBackend();
        using var creative = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var cfg = Config(("hub", hub), ("creative", creative));
        var stickies = new StickyRouteTable();
        stickies.Stage(Uid, "127.0.0.1", creative.Endpoint("creative"), StickyRouteTable.UidTtl, "test transfer");

        var bans = await BanCacheWith(cts.Token,
            new NetworkBan { PlayerUid = Uid, ServerId = "creative", Reason = "griefing" });

        using var live = await StartAsync(cfg, stickies, bans, cts.Token);
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
    public async Task StickyRouteToABackendTheBanDoesNotCover_IsStillHonoured()
    {
        using var hub = new RecordingBackend();
        using var target = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var cfg = Config(("hub", hub), ("target", target));
        var stickies = new StickyRouteTable();
        stickies.Stage(Uid, "127.0.0.1", target.Endpoint("target"), StickyRouteTable.UidTtl, "test transfer");

        var bans = await BanCacheWith(cts.Token,
            new NetworkBan { PlayerUid = Uid, ServerId = "creative", Reason = "griefing" });

        using var live = await StartAsync(cfg, stickies, bans, cts.Token);
        await live.SendAsync(ClientFrames.LoginTokenQuery(), cts.Token);

        await WaitFor(() => hub.Connections + target.Connections > 0, cts.Token);
        await Task.Delay(300, cts.Token);

        Assert.Equal(1, target.Connections);
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

    private static async Task<BanCache> BanCacheWith(CancellationToken ct, params NetworkBan[] entries)
    {
        var cache = new BanCache(new FakeRegistryClient { Bans = entries.ToList() }, ct);
        await cache.RefreshAsync();
        Assert.Equal(entries.Length, cache.Count);
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

    private static async Task<Live> StartAsync(ProxyConfig cfg, StickyRouteTable stickies, BanCache bans, CancellationToken ct)
    {
        var front = new TcpListener(IPAddress.Loopback, 0);
        front.Start();
        int frontPort = ((IPEndPoint)front.LocalEndpoint).Port;

        var events = new EventBus();
        var router = new BackendRouter(cfg, registry: null);
        var runner = new ClientSessionRunner(router, events,
            new ServerStatusResponder(cfg, registry: null, () => 0, ct), stickies, cfg, ct, bans);

        var accepted = front.AcceptTcpClientAsync(ct).AsTask();
        var player = new TcpClient();
        await player.ConnectAsync(IPAddress.Loopback, frontPort, ct);
        var serverSide = await accepted;

        var session = new ProxySession(1, cfg, serverSide, ct,
            new SessionServices(Stickies: stickies, Events: events, Bans: bans));
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
