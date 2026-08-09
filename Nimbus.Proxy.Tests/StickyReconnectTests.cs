using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// Drives real socket sessions through ClientSessionRunner over the handshake a stock Vintage
/// Story client actually performs: LoginTokenQuery first, Identification only once the backend
/// has answered it. That ordering is what broke transfers in the field (#57), because the sticky
/// reconnect route was resolved from a first frame that carries no identity at all.
/// </summary>
public class StickyReconnectTests
{
    private const string Uid = "uid-transferring-player";
    private const string OtherUid = "uid-someone-else";
    private const int FirstFrameTimeoutMs = 200;

    [Fact]
    public void LoginTokenQueryFrame_MatchesTheClientTagTheProxyDispatchesOn()
    {
        // Guards the hand-built frame the socket tests below rely on: field 33, wire type 2,
        // which is tag 266 in PacketDispatch.ClientTags.
        Assert.Equal("LoginTokenQuery", PacketDispatch.DescribeFrame(clientToServer: true, ClientFrames.LoginTokenQuery()));
    }

    [Fact]
    public async Task LoginTokenQueryFirstFrame_RoutesToTheStickyTargetNotTheFirstCandidate()
    {
        using var tryZero = new RecordingBackend();
        using var target = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var cfg = Config(("default", tryZero), ("target", target));
        var stickies = new StickyRouteTable();
        stickies.Stage(Uid, "127.0.0.1", target.Endpoint("target"), StickyRouteTable.UidTtl, "test transfer");

        using var live = await StartAsync(cfg, stickies, cts.Token);
        await live.SendAsync(ClientFrames.LoginTokenQuery(), cts.Token);

        await WaitFor(() => target.Connections + tryZero.Connections > 0, cts.Token);
        await Task.Delay(300, cts.Token);

        Assert.Equal(1, target.Connections);
        Assert.Equal(0, tryZero.Connections);
        Assert.True(target.BytesReceived > 0, "the token query was not forwarded to the sticky target");
    }

    [Fact]
    public async Task SilentClient_StillRoutesToTheStickyTargetByClientAddress()
    {
        using var tryZero = new RecordingBackend();
        using var target = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var cfg = Config(("default", tryZero), ("target", target));
        // Force the first-frame read to give up, the way a client that is slow to say anything
        // does in production. There is no frame at all to match a UID against.
        cfg.Status.QueryTimeoutMs = FirstFrameTimeoutMs;

        var stickies = new StickyRouteTable();
        stickies.Stage(Uid, "127.0.0.1", target.Endpoint("target"), StickyRouteTable.UidTtl, "test transfer");

        using var live = await StartAsync(cfg, stickies, cts.Token);

        await WaitFor(() => target.Connections + tryZero.Connections > 0, cts.Token);
        await Task.Delay(300, cts.Token);

        Assert.Equal(1, target.Connections);
        Assert.Equal(0, tryZero.Connections);
    }

    [Fact]
    public void TwoRoutesBehindOneAddress_HandOutTheOldestFirst()
    {
        var stickies = new StickyRouteTable();
        var first = new BackendEndpoint { Host = "127.0.0.1", Port = 1111, ServerId = "first" };
        var second = new BackendEndpoint { Host = "127.0.0.1", Port = 2222, ServerId = "second" };

        stickies.Stage("uid-a", "203.0.113.7", first, StickyRouteTable.UidTtl, "a");
        // The table stamps each entry from DateTime.UtcNow, so "oldest" only separates these two
        // once the clock has moved between the stagings. Waiting for exactly that is precise; a
        // fixed sleep would be a guess about the platform's timer resolution, long on the ones
        // where it is fine and short on the ones where it is not.
        var stagedFirstAt = DateTime.UtcNow;
        SpinWait.SpinUntil(() => DateTime.UtcNow > stagedFirstAt);
        stickies.Stage("uid-b", "203.0.113.7", second, StickyRouteTable.UidTtl, "b");

        Assert.True(stickies.TryConsumeByClientIp("203.0.113.7", out var oldest));
        Assert.Equal("uid-a", oldest.Uid);
        Assert.Equal(1111, oldest.Target.Port);

        Assert.True(stickies.TryConsumeByClientIp("203.0.113.7", out var next));
        Assert.Equal("uid-b", next.Uid);

        Assert.False(stickies.TryConsumeByClientIp("203.0.113.7", out _));
        // Both are gone from the UID index too: consuming a route removes it everywhere.
        Assert.False(stickies.Peek("uid-a", out _, out _, out _));
        Assert.False(stickies.Peek("uid-b", out _, out _, out _));
    }

    [Fact]
    public async Task LateStickyRoute_RedirectsInsteadOfReplayingIdentificationAtTheTarget()
    {
        using var landed = new RecordingBackend();
        using var target = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        var cfg = Config(("default", landed), ("target", target));
        cfg.Status.QueryTimeoutMs = FirstFrameTimeoutMs;

        var stickies = new StickyRouteTable();
        // Staged without an address, so the connect-time match cannot see it and the session
        // lands on try[0] and identifies there. That is exactly the state the late fallback is
        // for: the player has already spent their mp token on the wrong backend.
        stickies.Stage(Uid, clientIp: null, target.Endpoint("target"), StickyRouteTable.UidTtl, "late test transfer");

        using var live = await StartAsync(cfg, stickies, cts.Token);
        await WaitFor(() => landed.Connections > 0, cts.Token);

        await live.SendAsync(ClientFrames.Identification(Uid, "Pixnop"), cts.Token);
        await WaitFor(() => landed.Sent(Uid), cts.Token);

        // The client is told to reconnect, and the target is left untouched.
        var back = await live.ReadClientBytesAsync(2000, cts.Token);
        Assert.True(IsRedirectFrame(back), $"client did not receive a redirect frame ({back.Length} bytes)");
        Assert.Equal(0, target.Connections);
        Assert.Equal(0, target.BytesReceived);
        Assert.False(target.Sent(Uid), "the already-spent Identification was replayed at the transfer target");

        // The redirect re-stages the route for the reconnect, with the attempt counter moved on
        // so a route that keeps missing eventually gives up.
        var staged = Assert.Single(stickies.Snapshot());
        Assert.Equal(Uid, staged.Uid);
        Assert.Equal(target.Port, staged.Target.Port);
        Assert.Equal(2, staged.Attempts);
    }

    [Fact]
    public async Task LoginTokenQueryFirstFrame_IsNotMistakenForIdentification()
    {
        using var backend = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var cfg = Config(("default", backend));
        using var live = await StartAsync(cfg, new StickyRouteTable(), cts.Token);
        await live.SendAsync(ClientFrames.LoginTokenQuery(), cts.Token);

        await WaitFor(() => backend.BytesReceived > 0, cts.Token);
        await Task.Delay(200, cts.Token);

        // Nothing was captured from a frame that never held an identity, and the session was not
        // dropped by the ban gate on a frame it could not parse.
        Assert.False(live.Session.HasIdentification);
        Assert.Null(live.Session.PlayerUid);
        Assert.Equal(1, backend.Connections);
    }

    [Fact]
    public async Task AddressMatchedRouteTakenByAnotherPlayer_GoesBackUnderItsOwnUid()
    {
        using var tryZero = new RecordingBackend();
        using var target = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var cfg = Config(("default", tryZero), ("target", target));
        var stickies = new StickyRouteTable();
        stickies.Stage(Uid, "127.0.0.1", target.Endpoint("target"), StickyRouteTable.UidTtl, "test transfer");

        // Someone else behind the same address gets there first and picks up the route.
        using var live = await StartAsync(cfg, stickies, cts.Token);
        await live.SendAsync(ClientFrames.LoginTokenQuery(), cts.Token);
        await WaitFor(() => target.Connections > 0, cts.Token);
        await live.SendAsync(ClientFrames.Identification(OtherUid, "Interloper"), cts.Token);
        await WaitFor(() => target.Sent(OtherUid), cts.Token);
        await Task.Delay(300, cts.Token);

        // The interloper stays where they landed, connected rather than kicked, and the route is
        // back in the table under the UID it was staged for.
        Assert.True(stickies.Peek(Uid, out var restaged, out _, out _), "the route was not re-staged for its owner");
        Assert.Equal(target.Port, restaged.Port);
        // Re-staged under the UID only: matching it on the address again is how the two of them
        // would swap places a second time.
        Assert.False(stickies.TryConsumeByClientIp("127.0.0.1", out _));
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

    private static async Task<Live> StartAsync(ProxyConfig cfg, StickyRouteTable stickies, CancellationToken ct)
    {
        var front = new TcpListener(IPAddress.Loopback, 0);
        front.Start();
        int frontPort = ((IPEndPoint)front.LocalEndpoint).Port;

        var events = new EventBus();
        var router = new BackendRouter(cfg, registry: null);
        var runner = new ClientSessionRunner(router, events,
            new ServerStatusResponder(cfg, registry: null, () => 0, ct), stickies, cfg, ct);

        var accepted = front.AcceptTcpClientAsync(ct).AsTask();
        var player = new TcpClient();
        await player.ConnectAsync(IPAddress.Loopback, frontPort, ct);
        var serverSide = await accepted;

        var session = new ProxySession(1, cfg, serverSide, ct,
            new SessionServices(Stickies: stickies, Events: events));
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

    // Packet_Server envelope with Id (field 90) = 29, the vanilla ServerRedirect. Decoded with
    // the tests' own protobuf reader rather than the proxy's builder.
    private static bool IsRedirectFrame(byte[] bytes)
    {
        if (bytes.Length < 4) return false;
        var (compressed, len, payload) = ProtoWire.ParseFrame(bytes);
        if (compressed || len == 0) return false;
        var fields = ProtoWire.ReadFields(payload);
        return fields.Any(f => f.Number == 90 && f.WireType == 0 && f.Varint == 29);
    }
}
