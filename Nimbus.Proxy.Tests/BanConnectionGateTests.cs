using System.Net;
using System.Net.Sockets;
using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// Drives a real socket session through ClientSessionRunner to pin the ban gate on the path the
/// unit tests could not reach: a client that stays quiet past the first-frame read window, so the
/// pre-connect ban check never runs and the byte pumps are already live when Identification
/// finally arrives.
/// </summary>
public class BanConnectionGateTests
{
    private const string BannedUid = "banned-uid-1";
    private const int FirstFrameTimeoutMs = 200;

    [Fact]
    public async Task DelayedIdentification_FromABannedPlayer_NeverReachesTheBackend()
    {
        using var backend = new RecordingBackend();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var cfg = new ProxyConfig
        {
            Servers = new() { ["default"] = $"127.0.0.1:{backend.Port}" },
            Try = new() { "default" },
        };
        cfg.Registry.Mode = "disabled";
        // Force the pre-connect first-frame read to give up, which is the whole point: the ban
        // check that runs there is skipped and the pumps start unguarded.
        cfg.Status.QueryTimeoutMs = FirstFrameTimeoutMs;

        var registry = new FakeRegistryClient
        {
            Bans = new List<NetworkBan>
            {
                new() { PlayerUid = BannedUid, PlayerName = "griefer", Reason = "griefing", ExpiresAtUnix = 0 },
            },
        };
        var bans = new BanCache(registry, cts.Token);
        await bans.RefreshAsync();
        Assert.Equal(1, bans.Count);

        // Front door: accept one connection and hand the server side to a session.
        var front = new TcpListener(IPAddress.Loopback, 0);
        front.Start();
        int frontPort = ((IPEndPoint)front.LocalEndpoint).Port;

        var events = new EventBus();
        var stickies = new StickyRouteTable();
        var router = new BackendRouter(cfg, registry: null);
        var runner = new ClientSessionRunner(router, events, new ServerStatusResponder(cfg, registry: null, () => 0, cts.Token), stickies, cfg, cts.Token);

        var accepted = front.AcceptTcpClientAsync(cts.Token).AsTask();
        using var player = new TcpClient();
        await player.ConnectAsync(IPAddress.Loopback, frontPort, cts.Token);
        var serverSide = await accepted;

        var session = new ProxySession(1, cfg, serverSide, cts.Token,
            new SessionServices(Stickies: stickies, Events: events, Bans: bans));
        var running = runner.RunAsync(session, serverSide);

        // Stay silent past the first-frame window, then identify as the banned player.
        await Task.Delay(FirstFrameTimeoutMs * 3, cts.Token);
        await player.GetStream().WriteAsync(ClientFrames.Identification(BannedUid, "griefer"), cts.Token);
        await player.GetStream().FlushAsync(cts.Token);

        // Give the pump time to forward, if it were going to.
        await Task.Delay(1000, cts.Token);

        Assert.False(backend.Sent(BannedUid), "the banned player's Identification reached the backend");
        Assert.Equal(0, backend.BytesReceived);

        session.Close();
        try { await running.WaitAsync(TimeSpan.FromSeconds(5), cts.Token); } catch { }
        front.Stop();
    }
}
