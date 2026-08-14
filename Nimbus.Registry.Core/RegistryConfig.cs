namespace Nimbus.Registry;

// Top-level registry configuration loaded from nimbus.registry.json.
public sealed class RegistryConfig
{
    // Bind address. Loopback by default, matching the proxy's embedded registry: a standalone
    // registry is only worth running for backends that are not on this box, so widening this is a
    // deliberate step, and the secret below has to be replaced in the same edit.
    public string BindUrl { get; set; } = "http://127.0.0.1:8765";

    // HMAC shared secret used by every backend. To rotate, put the new secret in
    // AcceptedSecrets first, redeploy backends with the new value, then promote it here.
    public string SharedSecret { get; set; } = "change-me-and-keep-secret";

    // Additional accepted secrets during rotation. May be empty.
    public string[] AcceptedSecrets { get; set; } = Array.Empty<string>();

    // Seconds without a heartbeat before a backend is marked Stale in the snapshot.
    public int BackendStaleSeconds { get; set; } = 20;

    // Seconds without a heartbeat before a backend is dropped from the registry.
    public int BackendDropSeconds { get; set; } = 120;

    // Max age of a single nonce kept for replay protection.
    public int NonceWindowSeconds { get; set; } = 90;

    // If non-zero, refuse reservations whose TTL exceeds this many seconds.
    public int MaxReservationTtlSeconds { get; set; } = 300;

    // Must match the proxy's seamless ready wait. Backends receive it in the heartbeat
    // response and use it to calculate the client-side prepare expiry.
    public int SeamlessReadyWaitTimeoutSeconds { get; set; } = 75;

    // If true, log every successful heartbeat at Information level.
    public bool LogHeartbeats { get; set; } = false;

    // Where the ban list and the whitelist are kept so they survive a restart. One file per
    // list, nimbus.bans.json and nimbus.whitelist.json. A relative path resolves against the
    // working directory, which is where nimbus.registry.toml is read from, and the default puts
    // the state files right next to it. A file the registry cannot parse is renamed with a
    // .bad suffix rather than deleted or trusted.
    public string StateDir { get; set; } = ".";

    // Scoped bearer credentials for third-party integrations. Off by default: a registry that
    // nobody has issued a token from should not be answering bearer headers at all.
    public ApiTokensConfig ApiTokens { get; set; } = new();

    // Identity advertised to the VS master server. The registry registers the whole
    // network as a single entry. Off by default.
    public ServerIdentityConfig Identity { get; set; } = new();

    public IEnumerable<string> AllSecrets()
    {
        if (!string.IsNullOrEmpty(SharedSecret)) yield return SharedSecret;
        // Left as a walk: this method is already a lazy iterator, and the Where the analyser asks
        // for would nest a second state machine inside it to save one line, on a path every
        // authenticated request goes through.
        foreach (var s in AcceptedSecrets) // NOSONAR
            if (!string.IsNullOrEmpty(s)) yield return s;
    }
}

// The [api_tokens] section. Scoped bearer tokens are the third-party path (#54); the components
// Nimbus ships keep signing every call with HMAC and are unaffected by everything here.
public sealed class ApiTokensConfig
{
    // Master switch. False refuses bearer authentication outright, whatever tokens exist, so a
    // registry that has never been meant to answer integrations does not start doing so because
    // somebody minted a token.
    public bool Enabled { get; set; } = false;

    // Per-token budget, on top of whatever per-IP limiting sits in front. A non-positive value is
    // a config error and falls back to 60 rather than to no limit at all.
    public int RateLimitPerMinute { get; set; } = 60;

    // A bearer token is only as safe as the transport under it, so token auth is accepted on a
    // loopback connection or on this registry's own TLS listener and nowhere else. Set this true
    // only when a reverse proxy in front terminates TLS and is the sole route to this bind: it
    // makes the registry believe an X-Forwarded-Proto header, and anything that can reach the
    // bind directly can then write that header itself.
    public bool TrustForwardedProto { get; set; } = false;
}

// The configurations that are accepted, start cleanly and then quietly do nothing. The standalone
// registry has no validator of its own, so this is where its complaints live; the proxy carries
// the same two checks for the embedded registry in ProxyConfigValidator, worded the same way.
public static class RegistryConfigWarnings
{
    public static List<string> ApiTokens(RegistryConfig cfg)
    {
        var complaints = new List<string>();
        if (!cfg.ApiTokens.Enabled) return complaints;

        if (cfg.ApiTokens.RateLimitPerMinute <= 0)
            complaints.Add($"api_tokens.rate_limit_per_minute is {cfg.ApiTokens.RateLimitPerMinute}, which is not a rate. Falling back to 60 per token per minute.");

        // A bind that cannot be reached from outside, or one this registry serves TLS on itself,
        // is a bind tokens work over. Anything else is the configuration where bearer auth refuses
        // every request it is given, with no other signal an operator would ever see.
        if (!Uri.TryCreate(cfg.BindUrl, UriKind.Absolute, out var uri)) return complaints;
        // 0.0.0.0 is not loopback: it is every interface, loopback included.
        if (uri.Scheme == "https" || uri.IsLoopback) return complaints;

        complaints.Add(cfg.ApiTokens.TrustForwardedProto
            ? "api_tokens.trust_forwarded_proto = true makes the registry believe an X-Forwarded-Proto header on a plain-HTTP bind. Only set it when a TLS-terminating proxy is the sole route to bind_url."
            : "api_tokens.enabled = true on a non-loopback plain-HTTP bind_url: bearer auth will refuse every request, because a token is only as safe as the transport under it. Serve https, bind loopback, or set api_tokens.trust_forwarded_proto behind a TLS-terminating proxy.");
        return complaints;
    }
}

// Network identity published to the VS master server.
public sealed class ServerIdentityConfig
{
    // Master server advertising kill switch. Off by default.
    public bool AdvertiseOnMasterServer { get; set; } = false;

    // Vanilla master server endpoint. Override only when self-hosting.
    public string MasterServerUrl { get; set; } = "http://masterserver.vintagestory.at/api/v1/servers/";

    // Public host/port clients connect to. Must be the proxy's reachable address.
    public string PublicHost { get; set; } = "127.0.0.1";
    public ushort PublicPort { get; set; } = 42420;

    public string ServerName { get; set; } = "Nimbus Network";
    public string ServerDescription { get; set; } = "";
    public string ServerUrl { get; set; } = "";
    public string ServerIcon { get; set; } = "";
    public string VhIdentifier { get; set; } = "";
    public string GameVersion { get; set; } = "1.22.2";

    // 0 = sum of live backend MaxPlayers.
    public int MaxPlayersOverride { get; set; } = 0;

    public PlaystyleConfig Playstyle { get; set; } = new();

    // Mod list source for the registration packet:
    //   "aggregate"          - union of every live backend's RequiredClientMods
    //   "explicit"           - use ExplicitMods
    //   "backend:<serverId>" - mirror one backend's mod list
    public string ModSource { get; set; } = "aggregate";
    public ExplicitMod[] ExplicitMods { get; set; } = Array.Empty<ExplicitMod>();

    public bool Whitelisted { get; set; } = false;
    public bool HasPassword { get; set; } = false;

    // Master server heartbeat cadence. Vanilla uses 120s, so do not go lower.
    public int HeartbeatIntervalSeconds { get; set; } = 120;
}

public sealed class PlaystyleConfig
{
    public string Id { get; set; } = "surviveandbuild";
    public string LangCode { get; set; } = "preset-surviveandbuild";
}

public sealed class ExplicitMod
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
}
