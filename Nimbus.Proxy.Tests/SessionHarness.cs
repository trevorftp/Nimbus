using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// One real proxied session end to end: a loopback socket standing in for the player, a
/// ClientSessionRunner in the middle doing the first-frame read and the routing, and
/// RecordingBackends behind it. Everything the transfer paths touch is the production object,
/// including the byte pumps, so what a test asserts is what a player's client and a backend
/// would actually have seen.
///
/// Shaped after the harness in ScopedBanEnforcementTests, widened to carry the event bus, the
/// UDP override table and a registry so the transfer paths have all their collaborators.
/// </summary>
internal sealed class SessionHarness : IDisposable
{
    private readonly TcpListener front;
    private readonly TcpClient player;
    private readonly CancellationTokenSource cts;
    private readonly List<RecordingBackend> owned = new();

    private SessionHarness(TcpListener front, TcpClient player, ProxySession session, Task running,
        CancellationTokenSource cts)
    {
        this.front = front;
        this.player = player;
        this.cts = cts;
        Session = session;
        Running = running;
    }

    public ProxySession Session { get; private set; } = null!;
    public Task Running { get; private set; } = null!;
    public ProxyConfig Cfg { get; private set; } = null!;
    public EventBus Events { get; private set; } = null!;
    public StickyRouteTable Stickies { get; private set; } = null!;
    public UdpRouteOverrides UdpOverrides { get; private set; } = null!;
    public CancellationToken Token => cts.Token;

    /// <summary>Backends by the id they were configured under, in the order they were named.</summary>
    public Dictionary<string, RecordingBackend> Backends { get; } = new(StringComparer.OrdinalIgnoreCase);

    public BackendEndpoint Endpoint(string serverId) => Backends[serverId].Endpoint(serverId);

    public static Task<SessionHarness> StartAsync(params string[] serverIds)
        => StartAsync(null, serverIds);

    /// <summary>Brings up a session in front of one recording backend per id in
    /// <paramref name="serverIds"/>, routed at the first of them.</summary>
    public static async Task<SessionHarness> StartAsync(Action<ProxyConfig>? configure,
        params string[] serverIds)
    {
        if (serverIds.Length == 0) serverIds = new[] { "hub" };

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var backends = serverIds.ToDictionary(id => id, _ => new RecordingBackend(), StringComparer.OrdinalIgnoreCase);

        var cfg = new ProxyConfig
        {
            Servers = serverIds.ToDictionary(id => id, id => $"127.0.0.1:{backends[id].Port}"),
            Try = new List<string> { serverIds[0] },
        };
        cfg.Registry.Mode = "disabled";
        configure?.Invoke(cfg);

        var front = new TcpListener(IPAddress.Loopback, 0);
        front.Start();
        int frontPort = ((IPEndPoint)front.LocalEndpoint).Port;

        var events = new EventBus();
        var stickies = new StickyRouteTable();
        var udpOverrides = new UdpRouteOverrides();
        var runner = new ClientSessionRunner(new BackendRouter(cfg, registry: null), events,
            new ServerStatusResponder(cfg, registry: null, () => 0, cts.Token), stickies, cfg, cts.Token);

        var accepted = front.AcceptTcpClientAsync(cts.Token).AsTask();
        var player = new TcpClient();
        await player.ConnectAsync(IPAddress.Loopback, frontPort, cts.Token);
        var serverSide = await accepted;

        var session = new ProxySession(1, cfg, serverSide, cts.Token,
            new SessionServices(Stickies: stickies, UdpOverrides: udpOverrides, Events: events));

        var harness = new SessionHarness(front, player, session, runner.RunAsync(session, serverSide), cts)
        {
            Cfg = cfg,
            Events = events,
            Stickies = stickies,
            UdpOverrides = udpOverrides,
        };
        foreach (var kv in backends) harness.Backends[kv.Key] = kv.Value;
        harness.owned.AddRange(backends.Values);
        return harness;
    }

    /// <summary>A backend not in the config pool, for transfer targets the router never picks.
    /// The caller owns it.</summary>
    public static RecordingBackend ExtraBackend() => new();

    /// <summary>The address of a port nothing is listening on.</summary>
    public static BackendEndpoint DeadEndpoint(string serverId = "gone")
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new BackendEndpoint { Host = "127.0.0.1", Port = port, ServerId = serverId };
    }

    public async Task SendAsync(byte[] frame)
    {
        await player.GetStream().WriteAsync(frame, cts.Token);
        await player.GetStream().FlushAsync(cts.Token);
    }

    /// <summary>Sends Identification and waits for the session to have picked the uid up, which is
    /// what puts the gates, the sticky reconciliation and the transfer paths in play.</summary>
    public async Task IdentifyAsync(string uid = "uid-1", string name = "alice")
    {
        await SendAsync(ClientFrames.Identification(uid, name));
        await WaitForAsync(() => Session.PlayerUid == uid, $"the session never captured uid {uid}");
    }

    /// <summary>Identifies, then sends the ClientPlaying frame that moves the session to Ready.
    /// The seamless path refuses to run before that phase, so the tests about what it does once
    /// it is allowed to run have to get there first.</summary>
    public async Task ReachReadyAsync(string uid = "uid-1", string name = "alice")
    {
        await IdentifyAsync(uid, name);
        await SendAsync(ClientFrames.ClientPlaying());
        await WaitForAsync(() => Session.Phase == SessionState.Phase.Ready,
            $"the session never reached Ready (phase={Session.Phase})");
    }

    /// <summary>Everything the proxy has written back to the player so far, read until it goes
    /// quiet for <paramref name="millis"/>.</summary>
    public async Task<byte[]> ReadFromProxyAsync(int millis = 2000)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
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
        catch { /* the wait ran out or the proxy hung up; either way what arrived is what matters */ }
        return sink.ToArray();
    }

    /// <summary>The client's own address as the proxy sees it, which is what redirect stages the
    /// sticky route under.</summary>
    public string ClientIp => Session.ClientRemote;

    public IPAddress ClientAddress => IPAddress.Parse(ClientIp);

    public static async Task WaitForAsync(Func<bool> condition, string message, int millis = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(millis);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail(message);
    }

    public void Dispose()
    {
        try { Session.Close(); } catch { /* already torn down */ }
        try { player.Close(); } catch { /* the proxy closed it first */ }
        try { front.Stop(); } catch { /* already stopped */ }
        foreach (var b in owned) b.Dispose();
        cts.Cancel();
        cts.Dispose();
    }
}

/// <summary>Reads a forged Packet_Server frame out of the bytes the proxy wrote to the client.</summary>
internal static class ForgedFrames
{
    /// <summary>The (name, host) a Packet_ServerRedirect carries, or null when the bytes hold
    /// no redirect.</summary>
    public static (string Name, string Host)? Redirect(byte[] fromProxy)
    {
        foreach (var payload in Payloads(fromProxy))
        {
            var envelope = ProtoWire.ReadFields(payload);
            if (!envelope.Any(f => f.Number == 90 && f.Varint == 29)) continue;
            var body = ProtoWire.ReadFields(envelope.Single(f => f.Number == 29).Bytes);
            return (ProtoWire.Utf8(ProtoWire.Single(body, 1)), ProtoWire.Utf8(ProtoWire.Single(body, 2)));
        }
        return null;
    }

    /// <summary>The reason a forged Packet_DisconnectPlayer carries, or null.</summary>
    public static string? Disconnect(byte[] fromProxy)
    {
        foreach (var payload in Payloads(fromProxy))
        {
            var envelope = ProtoWire.ReadFields(payload);
            if (!envelope.Any(f => f.Number == 90 && f.Varint == 9)) continue;
            return ProtoWire.Utf8(ProtoWire.Single(ProtoWire.ReadFields(envelope.Single(f => f.Number == 8).Bytes), 1));
        }
        return null;
    }

    private static IEnumerable<byte[]> Payloads(byte[] stream)
    {
        int pos = 0;
        while (pos + 4 <= stream.Length)
        {
            uint header = (uint)((stream[pos] << 24) | (stream[pos + 1] << 16) | (stream[pos + 2] << 8) | stream[pos + 3]);
            int len = (int)(header & 0x7FFFFFFFu);
            if (len <= 0 || pos + 4 + len > stream.Length) yield break;
            yield return stream[(pos + 4)..(pos + 4 + len)];
            pos += 4 + len;
        }
    }
}

/// <summary>Chatline frames as a client sends them, built with the independent wire writer.</summary>
internal static class ChatFrames
{
    public static byte[] Chatline(string message, int groupId = 0)
    {
        var body = new MemoryStream();
        ProtoWire.WriteString(body, 1, message);
        ProtoWire.WriteTag(body, 2, 0);
        ProtoWire.WriteVarint(body, (ulong)groupId);

        var envelope = new MemoryStream();
        ProtoWire.WriteBytes(envelope, 4, body.ToArray());
        return ProtoWire.Frame(envelope.ToArray());
    }
}
