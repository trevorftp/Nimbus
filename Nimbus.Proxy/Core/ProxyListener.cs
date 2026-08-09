using System.Collections.Concurrent;
using System.Net.Sockets;

namespace Nimbus.Proxy;

// Accepts client TCP and tracks live sessions for admin and plugins.
internal sealed class ProxyListener
{
    private readonly ProxyConfig cfg;
    private readonly CancellationToken stopToken;
    private long sessionCounter;
    public ConcurrentDictionary<long, ProxySession> Sessions { get; } = new();

    // Non-null when a registry is configured (mode = "embedded" or "remote").
    public IRegistryClient? Registry { get; }

    public BackendRouter Router { get; }

    // Redirect transfers stage their next reconnect here by UID.
    public StickyRouteTable Stickies { get; } = new();

    // UDP follows TCP swaps through this per-client-IP table.
    public UdpRouteOverrides UdpOverrides { get; } = new();

    public RegistryConfig RegistryCfg => cfg.Registry;
    public ProxyConfig Cfg => cfg;

    // Plugin/event surface. Subscribed handlers run sequentially per-event.
    public EventBus Events { get; } = new();

    // Warm list of network bans, consulted synchronously by the connection gate.
    public BanCache Bans { get; }

    // Warm whitelist, consulted by the same gate right after the bans.
    public WhitelistCache Whitelist { get; }

    public ProxyListener(ProxyConfig cfg, CancellationToken stopToken, IRegistryClient? registry = null,
        PersistentDrainStore? drainStore = null)
    {
        this.cfg = cfg;
        this.stopToken = stopToken;
        Registry = registry;
        Router = new BackendRouter(cfg, registry, drainStore);
        Bans = new BanCache(registry, stopToken);
        Whitelist = new WhitelistCache(registry, stopToken);
        Events.WarningSink = Log.Warn;
    }

    public async Task RunAsync()
    {
        var listenEp = cfg.ListenEndPoint();
        var listener = new TcpListener(listenEp);
        listener.Start();
        var backends = cfg.Backends();
        if (backends.Count > 1)
            Log.Info($"listening on {listenEp} -> pool of {backends.Count} backend(s)");
        else
            Log.Info($"listening on {listenEp} -> backend {backends[0]}");

        _ = Task.Run(() => new StickyRouteSweeper(Stickies, stopToken).RunAsync(), stopToken);
        if (Registry != null)
        {
            _ = Task.Run(() => new TransferIntentDispatcher(cfg, Registry, Sessions, stopToken).RunAsync(), stopToken);
            _ = Task.Run(() => Bans.RunAsync(), stopToken);
            _ = Task.Run(() => Whitelist.RunAsync(), stopToken);
        }
        if (cfg.Whitelist.Enabled)
        {
            string scope = cfg.Whitelist.Network
                ? "the whole network"
                : $"servers [{string.Join(", ", cfg.Whitelist.Servers)}]";
            Log.Info($"whitelist enforcement on for {scope} " +
                     $"(fail_open_until_first_sync={cfg.Whitelist.FailOpenUntilFirstSync})");
        }
        var statusResponder = new ServerStatusResponder(cfg, Registry, () => Sessions.Count, stopToken);
        var sessionRunner = new ClientSessionRunner(Router, Events, statusResponder, Stickies, cfg, stopToken, Bans, Whitelist);

        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stopToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                long id = Interlocked.Increment(ref sessionCounter);
                var session = new ProxySession(id, cfg, client, stopToken,
                    new SessionServices(Stickies, Registry, UdpOverrides, Events, Bans, Whitelist));
                Sessions[id] = session;
                ProxyMetrics.SessionAccepted();
                _ = Task.Run(async () =>
                {
                    try { await sessionRunner.RunAsync(session, client).ConfigureAwait(false); }
                    finally
                    {
                        Sessions.TryRemove(id, out _);
                        ProxyMetrics.SessionClosed();
                    }
                }, stopToken);
            }
        }
        finally
        {
            listener.Stop();
            Log.Info("listener stopped");
        }
    }
}
