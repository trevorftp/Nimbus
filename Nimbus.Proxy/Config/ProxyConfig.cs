using System.Net;

namespace Nimbus.Proxy;

// Velocity-shaped proxy config. Loaded from nimbus.proxy.toml next to the binary. A single
// flat-ish layout where named servers are a dict (name -> "host:port") and the connect order
// is a top-level `try` list, mirroring how a Minecraft server.properties + velocity.toml read.
//
// Example:
//   bind = "0.0.0.0:42420"
//
//   [servers]
//   hub = "127.0.0.1:42421"
//   factions = "127.0.0.1:42422"
//
//   try = [ "hub" ]
//
//   [transfers]
//   default_mode = "redirect"
//   allow_seamless = false
//   require_seamless_capability = true
//   fallback_to_redirect_when_seamless_unavailable = true
//
//   [admin]
//   bind = "127.0.0.1:42499"
//   secret = ""
//
//   [registry]
//   mode = "embedded"          # "embedded" | "remote" | "disabled"
//
//   [whitelist]
//   network = false            # true closes the whole network to unlisted players
//   servers = []               # backend ids closed to unlisted players even when network = false
internal sealed class ProxyConfig
{
    public string Bind { get; set; } = "0.0.0.0:42420";

    // Named backend pool. Key = serverId. Value = "host:port". Case-insensitive on lookup.
    public Dictionary<string, string> Servers { get; set; } = new()
    {
        ["default"] = "127.0.0.1:42421",
    };

    // Ordered connect attempts on initial join.
    // Unknown names are skipped with a warning.
    public List<string> Try { get; set; } = new() { "default" };

    // Server names that should receive a PROXY protocol v2 header on every upstream TCP.
    // The backend must list this proxy's IP in its trusted-proxy CIDRs or it will reject the
    // connection. Opt-in so unmodded backends still accept plain TCP.
    public List<string> ProxyProtocolServers { get; set; } = new();

    // Reserved for the SNI / direct-connect hostname routing pass. Map of incoming hostname
    // -> ordered try-list of server names. Not consumed yet (no SNI source in VS handshake).
    public Dictionary<string, List<string>> ForcedHosts { get; set; } = new();

    public TransfersConfig Transfers { get; set; } = new();
    public AdminConfig Admin { get; set; } = new();
    public RegistryConfig Registry { get; set; } = new();
    public WhitelistConfig Whitelist { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();
    public MetricsConfig Metrics { get; set; } = new();
    public StatusConfig Status { get; set; } = new();
    public PluginsConfig Plugins { get; set; } = new();
    public PersistenceConfig Persistence { get; set; } = new();
    public AdvancedConfig Advanced { get; set; } = new();

    // --- Runtime helpers (getter-only / methods, so Tomlyn ignores them on serialization). ---

    private List<BackendEndpoint>? _resolvedBackends;

    public IReadOnlyList<BackendEndpoint> Backends()
    {
        if (_resolvedBackends != null) return _resolvedBackends;
        var list = new List<BackendEndpoint>(Servers.Count);
        foreach (var kv in Servers)
        {
            var (h, p) = SplitHostPort(kv.Value, $"servers.{kv.Key}");
            bool pp = ProxyProtocolServers.Any(n => string.Equals(n, kv.Key, StringComparison.OrdinalIgnoreCase));
            list.Add(new BackendEndpoint { Host = h, Port = p, ServerId = kv.Key, ProxyProtocol = pp });
        }
        _resolvedBackends = list;
        return list;
    }

    // Copy hot-reloadable fields from a freshly-loaded config in-place so all existing
    // references (ProxyListener, BackendRouter, etc.) see the updated values immediately.
    // Structural settings that require restart (bind, admin, registry, metrics) are left unchanged.
    public void UpdateFrom(ProxyConfig fresh)
    {
        Servers = fresh.Servers;
        Try = fresh.Try;
        ProxyProtocolServers = fresh.ProxyProtocolServers;
        Transfers = fresh.Transfers;
        Whitelist = fresh.Whitelist;
        Logging = fresh.Logging;
        Status = fresh.Status;
        Plugins = fresh.Plugins;
        Advanced = fresh.Advanced;
        _resolvedBackends = null;
    }

    public BackendEndpoint? FindBackend(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return Backends().FirstOrDefault(b => string.Equals(b.ServerId, name, StringComparison.OrdinalIgnoreCase));
    }

    public BackendEndpoint DefaultBackend()
    {
        var list = Backends();
        if (list.Count == 0) throw new InvalidOperationException("no backends configured in [servers]");
        return list[0];
    }

    public IPEndPoint ListenEndPoint()
    {
        var (h, p) = SplitHostPort(Bind, "bind");
        return new IPEndPoint(IPAddress.Parse(h), p);
    }

    private static (string host, int port) SplitHostPort(string s, string label)
    {
        if (string.IsNullOrWhiteSpace(s)) throw new InvalidDataException($"{label}: empty");
        int idx = s.LastIndexOf(':');
        if (idx <= 0 || idx == s.Length - 1) throw new InvalidDataException($"{label}: must be 'host:port', got '{s}'");
        string host = s.Substring(0, idx);
        if (!int.TryParse(s.AsSpan(idx + 1), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int port))
            throw new InvalidDataException($"{label}: invalid port in '{s}'");
        if (port <= 0 || port > 65535) throw new InvalidDataException($"{label}: port out of range in '{s}'");
        return (host, port);
    }
}

internal sealed class BackendEndpoint
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 42421;

    // Logical name. Matches the key in `[servers]`. Used for sticky routes and reservations.
    public string ServerId { get; set; } = "";

    // Set from ProxyProtocolServers at resolve time.
    public bool ProxyProtocol { get; set; } = false;

    public override string ToString()
        => string.IsNullOrEmpty(ServerId) ? $"{Host}:{Port}" : $"{ServerId}@{Host}:{Port}";
}

internal sealed class TransfersConfig
{
    // "redirect" is vanilla reconnect. "seamless" is the Nimbus visual handoff path.
    public string DefaultMode { get; set; } = "redirect";

    // Master switch for the optional Nimbus client/server mod transfer path.
    public bool AllowSeamless { get; set; } = false;

    // Keep seamless tied to the optional Nimbus mod handshake.
    public bool RequireSeamlessCapability { get; set; } = true;

    // Redirect is the production fallback when seamless was requested but the client did not
    // prove it can handle the optional Nimbus path.
    public bool FallbackToRedirectWhenSeamlessUnavailable { get; set; } = true;

    // "host" or "host:port" stamped into redirect packets instead of the target backend's
    // PublicHost. Set it to the proxy's own player-facing address (usually the same one as
    // `bind` reaches). RedirectFix clients ignore the stamped host either way, but a vanilla
    // client with the redirect crash fixed will dial it literally: stamping the proxy keeps
    // such clients on the proxy, stamping the backend would let them bypass it (#18).
    // Empty keeps the legacy backend stamping.
    public string RedirectAddress { get; set; } = "";

    // Re-enables the old live TCP splice experiment. Leave this off for normal servers.
    public bool EnableUnsafeSeamlessSplice { get; set; } = false;
}

internal sealed class AdminConfig
{
    // "host:port" for the line-JSON admin socket. Localhost-only by default.
    public string Bind { get; set; } = "127.0.0.1:42499";

    // When non-empty the first admin frame must be {"cmd":"auth","secret":"..."}. Required
    // whenever Bind is not loopback.
    public string Secret { get; set; } = "";

    // Permissions granted after the admin secret succeeds.
    // "*" keeps today's operator model.
    public List<string> GrantedPermissions { get; set; } = new() { "*" };

    // Set false to disable the admin socket entirely.
    public bool Enabled { get; set; } = true;

    public IPEndPoint EndPoint()
    {
        if (string.IsNullOrWhiteSpace(Bind)) throw new InvalidDataException("admin.bind: empty");
        int idx = Bind.LastIndexOf(':');
        if (idx <= 0 || idx == Bind.Length - 1) throw new InvalidDataException($"admin.bind: must be 'host:port', got '{Bind}'");
        string host = Bind.Substring(0, idx);
        if (!int.TryParse(Bind.AsSpan(idx + 1), out int port)) throw new InvalidDataException($"admin.bind: invalid port in '{Bind}'");
        return new IPEndPoint(IPAddress.Parse(host), port);
    }
}

internal sealed class RegistryConfig
{
    // "embedded" -> proxy hosts the registry in-process (and optionally serves HTTP for
    //               external backends to heartbeat against).
    // "remote"   -> proxy talks to a standalone Nimbus.Registry over HTTP.
    // "disabled" -> no registry. Single-backend deployments work via [servers].
    public string Mode { get; set; } = "embedded";

    // Common to embedded + remote. SourceServerId on minted reservations.
    public string ProxyId { get; set; } = "nimbus-proxy";
    public int ReservationTtlSeconds { get; set; } = 60;
    public bool FailOnError { get; set; } = true;
    public int TransferIntentPollMs { get; set; } = 1000;

    // The maximum time the dispatcher waits for a source session to become ready.
    // Standalone registries must use the same value so heartbeat responses can give
    // backends an accurate client-side expiry budget.
    public int SeamlessReadyWaitTimeoutSeconds { get; set; } = 75;

    // Remote mode only.
    public string Url { get; set; } = "";
    public string SharedSecret { get; set; } = "";
    public int HttpTimeoutSeconds { get; set; } = 5;

    // Embedded mode only. Empty Bind disables the HTTP listener.
    // The proxy still keeps its in-process registry path.
    //
    // Loopback by default, so the file written on a first run passes the validator below it and a
    // single-machine install serves players without being edited first. Backends on another host
    // or in another container cannot reach a loopback bind: widen this to "http://0.0.0.0:8765"
    // and, in the same edit, replace embedded_shared_secret, which the validator requires and
    // refuses to start without. The Pterodactyl eggs write both lines from panel variables, so a
    // panel install never sees these defaults.
    public string EmbeddedBind { get; set; } = "http://127.0.0.1:8765";
    public string EmbeddedSharedSecret { get; set; } = "change-me-and-keep-secret";
    public int BackendStaleSeconds { get; set; } = 20;
    public int BackendDropSeconds { get; set; } = 120;
    public int NonceWindowSeconds { get; set; } = 90;
    public int MaxReservationTtlSeconds { get; set; } = 300;
    public bool AdvertiseOnMasterServer { get; set; } = false;

    // Where the embedded registry keeps its ban list, whitelist and issued API tokens so they
    // survive a restart: nimbus.bans.json, nimbus.whitelist.json and nimbus.tokens.json. Relative
    // paths resolve next to the proxy executable, the same rule [persistence] uses for the drain
    // flags.
    public string EmbeddedStateDir { get; set; } = ".";

    // Embedded mode only, and the same three settings the standalone registry reads from its own
    // [api_tokens] section. They gate how a scoped bearer token is accepted, never how one is
    // created: `nimctl token create` works either way, because minting a credential the registry
    // is not yet answering is how an operator gets ready to turn it on.
    public bool ApiTokensEnabled { get; set; } = false;
    public int ApiTokensRateLimitPerMinute { get; set; } = 60;
    public bool ApiTokensTrustForwardedProto { get; set; } = false;
}

// Whitelist enforcement. The list itself lives in the registry; these switches decide where it
// is a requirement. Nothing is inferred from the list: an empty whitelist with `network = true`
// means nobody gets in, which is the only reading that does not turn "the last entry was just
// removed" into "the door is now open to everyone".
internal sealed class WhitelistConfig
{
    // True closes the whole network: only listed players get past the proxy door.
    public bool Network { get; set; } = false;

    // Backend ids that require coverage even when the network as a whole is open. The staff or
    // build server sitting inside a public network.
    public List<string> Servers { get; set; } = new();

    // Cold start only. The proxy has never managed to read the list, so it cannot tell an empty
    // whitelist from an unread one, and by default it keeps the door shut. Set this true to let
    // players in until the first successful fetch instead: an availability choice that trades
    // the closed network for the risk of a wide-open one while the registry is unreachable.
    public bool FailOpenUntilFirstSync { get; set; } = false;

    public bool Enabled => Network || Servers.Count > 0;

    // True when joining (or being moved to) this backend requires a whitelist entry. A backend
    // with no ServerId, configured as host:port, can only be gated network-wide: there is no id
    // for `servers` to name it by.
    public bool RequiresCoverage(string? serverId)
    {
        if (Network) return true;
        if (string.IsNullOrEmpty(serverId)) return false;
        return Servers.Any(s => string.Equals(s, serverId, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class LoggingConfig
{
    public bool Verbose { get; set; } = false;
    public bool SniffFrames { get; set; } = false;
    public bool LogTrafficBytes { get; set; } = false;
}

internal sealed class MetricsConfig
{
    public bool Enabled { get; set; } = true;
    public string Bind { get; set; } = "http://127.0.0.1:42500";
    public string Path { get; set; } = "/metrics";

    // Read-only JSON network status at /status on the same host, meant for game panels
    // (Pterodactyl, AMP, ...) and dashboards.
    public bool StatusApi { get; set; } = true;

    // Optional bearer token for /status. Leave it empty to keep /status open, which is fine on
    // the loopback default bind; set it before exposing the bind beyond localhost. Send it in
    // the Authorization header as a bearer token. The ?token= query parameter exists only as a
    // compatibility fallback for panels that cannot set headers, and query strings can end up in
    // access logs, so prefer the header wherever possible.
    public string StatusApiToken { get; set; } = "";
}

internal sealed class StatusConfig
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "Nimbus";
    public string Motd { get; set; } = "Vintage Story proxy";
    public string GameMode { get; set; } = "survival";
    public bool Password { get; set; } = false;
    public string ServerVersion { get; set; } = "";
    public int MaxPlayers { get; set; } = 100;
    public int QueryTimeoutMs { get; set; } = 1500;
}

internal sealed class PluginsConfig
{
    public bool Enabled { get; set; } = true;

    // Relative paths resolve next to the proxy executable.
    public string Directory { get; set; } = "plugins";

    public List<string> Disabled { get; set; } = new();
}

internal sealed class PersistenceConfig
{
    public bool PersistDrainFlags { get; set; } = true;

    // Relative paths resolve next to the proxy executable.
    public string DrainFlagsFile { get; set; } = "nimbus.drain-state.json";
}

internal sealed class AdvancedConfig
{
    public int ConnectTimeoutMs { get; set; } = 5000;
    public int BufferSize { get; set; } = 16 * 1024;
}
