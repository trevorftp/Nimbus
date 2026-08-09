using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// A real admin socket in front of a real proxy: AdminListener bound to a loopback port, a
/// ProxyListener holding the session table the commands read and write, and a scripted registry
/// behind both. Nothing here stubs the command layer, so a test drives the same path an operator
/// reaches through nimctl: bytes in, one JSON line back.
/// </summary>
internal sealed class AdminHarness : IAsyncDisposable
{
    private readonly CancellationTokenSource cts = new();
    private readonly ClientSessionRunner runner;
    private readonly List<PlayerHandle> players = new();
    private readonly List<RecordingBackend> backends = new();
    private long nextSessionId;

    private AdminHarness(ProxyConfig cfg, ProxyListener proxy, FakeRegistryClient registry, int port)
    {
        Cfg = cfg;
        Proxy = proxy;
        Registry = registry;
        Port = port;
        runner = new ClientSessionRunner(proxy.Router, proxy.Events,
            new ServerStatusResponder(cfg, registry, () => proxy.Sessions.Count, cts.Token),
            proxy.Stickies, cfg, cts.Token, proxy.Bans, proxy.Whitelist);
    }

    public ProxyConfig Cfg { get; }
    public ProxyListener Proxy { get; }
    public FakeRegistryClient Registry { get; }
    public int Port { get; }

    /// <summary>What the `plugins` command reports. Mutable so a test can load real plugins first
    /// and then ask the socket what an operator would see.</summary>
    public List<LoadedPlugin> Plugins { get; } = new();

    /// <summary>Starts the admin socket in front of a proxy whose only backend is a recording
    /// stand-in named <c>hub</c>, unless <paramref name="serverIds"/> asks for more.</summary>
    public static async Task<AdminHarness> StartAsync(
        Action<ProxyConfig>? configure = null,
        FakeRegistryClient? registry = null,
        bool withRegistry = true,
        params string[] serverIds)
    {
        if (serverIds.Length == 0) serverIds = new[] { "hub" };

        var recorders = serverIds.Select(_ => new RecordingBackend()).ToList();
        var cfg = new ProxyConfig
        {
            Servers = serverIds.Zip(recorders).ToDictionary(p => p.First, p => $"127.0.0.1:{p.Second.Port}"),
            Try = new List<string> { serverIds[0] },
        };
        cfg.Registry.Mode = withRegistry ? "embedded" : "disabled";
        cfg.Admin.Bind = $"127.0.0.1:{FreePort()}";
        configure?.Invoke(cfg);

        registry ??= new FakeRegistryClient();
        var proxy = new ProxyListener(cfg, CancellationToken.None, withRegistry ? registry : null);
        var harness = new AdminHarness(cfg, proxy, registry, cfg.Admin.EndPoint().Port);
        harness.backends.AddRange(recorders);

        var listener = new AdminListener(cfg, proxy, harness.cts.Token, () => harness.Plugins);
        _ = Task.Run(listener.RunAsync);
        await harness.WaitForAdminSocketAsync();
        return harness;
    }

    /// <summary>Opens an admin connection and completes the auth handshake with
    /// <paramref name="secret"/>, asserting the proxy accepted it.</summary>
    public async Task<AdminConnection> ConnectAsync(string? secret = null)
    {
        var conn = await AdminConnection.OpenAsync(Port, cts.Token);
        if (secret != null)
        {
            var reply = await conn.SendAsync(new { cmd = "auth", secret });
            Assert.True(reply.GetProperty("ok").GetBoolean(), $"auth was refused: {reply}");
        }
        return conn;
    }

    /// <summary>One command over a fresh authenticated connection, the way nimctl works.</summary>
    public async Task<JsonElement> RunAsync(object payload, string? secret = null)
    {
        using var conn = await ConnectAsync(secret);
        return await conn.SendAsync(payload);
    }

    /// <summary>The socket and session-table half of a join, before anything is said on the wire.
    /// Both <see cref="JoinAsync"/> and <see cref="ConnectWithoutIdentifyingAsync"/> start here and
    /// differ only in the first frame they send.</summary>
    private async Task<(PlayerHandle Handle, TcpClient Player)> AttachAsync()
    {
        var front = new TcpListener(IPAddress.Loopback, 0);
        front.Start();
        int frontPort = ((IPEndPoint)front.LocalEndpoint).Port;

        var accepted = front.AcceptTcpClientAsync(cts.Token).AsTask();
        var player = new TcpClient();
        await player.ConnectAsync(IPAddress.Loopback, frontPort, cts.Token);
        var serverSide = await accepted;

        long id = Interlocked.Increment(ref nextSessionId);
        var session = new ProxySession(id, Cfg, serverSide, cts.Token,
            new SessionServices(Proxy.Stickies, Proxy.Registry, Proxy.UdpOverrides, Proxy.Events, Proxy.Bans, Proxy.Whitelist));
        Proxy.Sessions[id] = session;
        var running = runner.RunAsync(session, serverSide);

        var handle = new PlayerHandle(id, session, player, front, running);
        players.Add(handle);
        return (handle, player);
    }

    /// <summary>A stock client that has been routed to a backend but has said nothing that
    /// carries an identity: it holds a slot in the session table with no uid and no captured
    /// Identification, which is the session shape the transfer paths have nothing to move.</summary>
    public async Task<PlayerHandle> ConnectWithoutIdentifyingAsync()
    {
        var (handle, player) = await AttachAsync();
        await player.GetStream().WriteAsync(ClientFrames.LoginTokenQuery(), cts.Token);
        await player.GetStream().FlushAsync(cts.Token);
        await WaitFor(() => handle.Session.CurrentBackend != null,
            $"session {handle.Id} was never routed to a backend");
        return handle;
    }

    /// <summary>Puts a player on the proxy for real: a socket, the identification frame that
    /// gives the session its uid, and the entry in the session table the admin commands walk.</summary>
    public async Task<PlayerHandle> JoinAsync(string uid, string name)
    {
        var (handle, player) = await AttachAsync();
        await player.GetStream().WriteAsync(ClientFrames.Identification(uid, name), cts.Token);
        await player.GetStream().FlushAsync(cts.Token);
        await WaitFor(() => handle.Session.PlayerUid == uid, $"session {handle.Id} never picked up uid {uid}");
        return handle;
    }

    /// <summary>Pushes <paramref name="entries"/> into the registry and warms the proxy's cache
    /// from it, which is what whitelist enforcement reads before letting anyone join.</summary>
    public async Task WhitelistAsync(params Nimbus.Shared.Models.WhitelistEntry[] entries)
    {
        Registry.Whitelist = entries.ToList();
        Assert.True(await Proxy.Whitelist.RefreshAsync());
    }

    public async ValueTask DisposeAsync()
    {
        cts.Cancel();
        foreach (var p in players) p.Dispose();
        foreach (var b in backends) b.Dispose();
        await Task.Yield();
        cts.Dispose();
    }

    private async Task WaitForAdminSocketAsync()
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, Port, cts.Token);
                return;
            }
            catch (SocketException) { await Task.Delay(20, cts.Token); }
        }
        Assert.Fail($"the admin socket never came up on 127.0.0.1:{Port}");
    }

    internal static async Task WaitFor(Func<bool> condition, string message, int millis = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(millis);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail(message);
    }

    /// <summary>An ephemeral port the OS has just handed back. AdminListener binds the port from
    /// config itself, so it cannot be asked for one after the fact.</summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

/// <summary>One admin TCP connection, spoken to in the line-JSON the socket expects.</summary>
internal sealed class AdminConnection : IDisposable
{
    private readonly TcpClient tcp;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly CancellationToken ct;

    private AdminConnection(TcpClient tcp, StreamReader reader, StreamWriter writer, CancellationToken ct)
    {
        this.tcp = tcp;
        this.reader = reader;
        this.writer = writer;
        this.ct = ct;
    }

    public static async Task<AdminConnection> OpenAsync(int port, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(IPAddress.Loopback, port, ct);
        var stream = tcp.GetStream();
        var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        return new AdminConnection(tcp, reader, writer, ct);
    }

    public Task<JsonElement> SendAsync(object payload)
        => SendRawAsync(payload is string s ? s : JsonSerializer.Serialize(payload));

    /// <summary>Writes a line verbatim, for the frames a well-behaved client would never send.</summary>
    public async Task<JsonElement> SendRawAsync(string line)
    {
        string? reply = await SendRawForLineAsync(line);
        Assert.NotNull(reply);
        return JsonSerializer.Deserialize<JsonElement>(reply!);
    }

    /// <summary>The raw reply line, or null when the proxy hung up without one.</summary>
    public async Task<string?> SendRawForLineAsync(string line)
    {
        await writer.WriteLineAsync(line.AsMemory(), ct);
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(TimeSpan.FromSeconds(10));
        return await reader.ReadLineAsync(readCts.Token);
    }

    /// <summary>True once the proxy has closed this connection from its end.</summary>
    public async Task<bool> ClosedByProxyAsync(int millis = 3000)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(millis);
        try { return await reader.ReadLineAsync(readCts.Token) == null; }
        catch (OperationCanceledException) { return false; }
        catch (IOException) { return true; }
    }

    public void Dispose()
    {
        reader.Dispose();
        writer.Dispose();
        tcp.Dispose();
    }
}

/// <summary>A player the admin commands can act on, and the socket that shows whether they did.</summary>
internal sealed class PlayerHandle : IDisposable
{
    private readonly TcpClient player;
    private readonly TcpListener front;

    public PlayerHandle(long id, ProxySession session, TcpClient player, TcpListener front, Task running)
    {
        Id = id;
        Session = session;
        this.player = player;
        this.front = front;
        Running = running;
    }

    public long Id { get; }
    public ProxySession Session { get; }
    public Task Running { get; }

    /// <summary>True once the proxy dropped this player's socket. The kick is synchronous inside
    /// the command, so a false here after the command has replied means the player stayed on.</summary>
    public async Task<bool> DroppedAsync(int millis = 3000)
    {
        using var cts = new CancellationTokenSource(millis);
        var buf = new byte[512];
        try
        {
            while (true)
            {
                int read = await player.GetStream().ReadAsync(buf, cts.Token);
                if (read <= 0) return true;
            }
        }
        catch (OperationCanceledException) { return false; }
        catch (IOException) { return true; }
        catch (ObjectDisposedException) { return true; }
    }

    public void Dispose()
    {
        try { Session.Close(); } catch { /* already torn down by the test */ }
        try { player.Close(); } catch { /* the proxy closed it first */ }
        try { front.Stop(); } catch { /* already stopped */ }
    }
}
