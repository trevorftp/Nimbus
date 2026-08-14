using System.Net;

namespace Nimbus.Proxy;

internal sealed class ProxyConfigValidation
{
    private readonly List<string> errors = new();
    private readonly List<string> warnings = new();

    public IReadOnlyList<string> Errors => errors;
    public IReadOnlyList<string> Warnings => warnings;
    public bool IsValid => errors.Count == 0;

    public void Error(string message) => errors.Add(message);
    public void Warn(string message) => warnings.Add(message);
}

internal static class ProxyConfigValidator
{
    // Named because the scheme is asked about in four places now, and a validator that accepts a
    // bind it then refuses to serve over is the kind of disagreement a typo here would cause.
    private const string Https = "https";

    // Named for the same reason, and it is the sharper of the two: this is the transfer mode
    // spelling that has to match the one ProxySession compares against and the one an operator
    // types into transfers.default_mode. A validator that accepts a mode the session path then
    // does not recognise sends every transfer down the redirect fallback without a word.
    private const string Seamless = "seamless";

    public static ProxyConfigValidation Validate(ProxyConfig cfg)
    {
        var result = new ProxyConfigValidation();

        ValidateEndpoint(cfg.Bind, "bind", requireIpAddress: true, result);
        ValidateServers(cfg, result);
        ValidateTransfers(cfg, result);
        ValidateAdmin(cfg, result);
        ValidateRegistry(cfg, result);
        ValidateWhitelist(cfg, result);
        ValidateMetrics(cfg, result);
        ValidateStatus(cfg, result);
        ValidatePlugins(cfg, result);
        ValidatePersistence(cfg, result);
        ValidateAdvanced(cfg, result);

        return result;
    }

    private static void ValidateServers(ProxyConfig cfg, ProxyConfigValidation result)
    {
        if (cfg.Servers.Count == 0)
        {
            result.Error("[servers] must contain at least one backend");
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in cfg.Servers)
        {
            if (string.IsNullOrWhiteSpace(kv.Key))
            {
                result.Error("[servers] contains an empty server id");
                continue;
            }
            if (!seen.Add(kv.Key))
                result.Error($"[servers] contains duplicate server id '{kv.Key}'");
            ValidateEndpoint(kv.Value, $"servers.{kv.Key}", requireIpAddress: false, result);
        }

        WarnUnknownServerRefs(cfg, cfg.Try, "try", result);
        WarnUnknownServerRefs(cfg, cfg.ProxyProtocolServers, "proxy_protocol_servers", result);

        foreach (var forced in cfg.ForcedHosts)
        {
            if (string.IsNullOrWhiteSpace(forced.Key))
                result.Warn("[forced-hosts] contains an empty hostname");
            foreach (var serverId in forced.Value.Where(id => !HasServer(cfg, id)))
                result.Warn($"forced-hosts.{forced.Key} references unknown server '{serverId}'");
        }
    }

    // A flat list of server ids that each has to name a configured backend, warned about by its
    // section name. `try` and `proxy_protocol_servers` are the same check verbatim; forced-hosts is
    // not, because it warns per hostname and carries its own empty-key case.
    private static void WarnUnknownServerRefs(ProxyConfig cfg, IEnumerable<string> serverIds, string label, ProxyConfigValidation result)
    {
        foreach (var serverId in serverIds)
        {
            if (string.IsNullOrWhiteSpace(serverId)) continue;
            if (!HasServer(cfg, serverId))
                result.Warn($"{label} references unknown server '{serverId}'");
        }
    }

    private static void ValidateTransfers(ProxyConfig cfg, ProxyConfigValidation result)
    {
        var mode = NormalizeMode(cfg.Transfers.DefaultMode);
        if (mode is not "redirect" and not Seamless)
            result.Error($"transfers.default_mode must be 'redirect' or 'seamless', got '{cfg.Transfers.DefaultMode}'");
        if (mode == Seamless && !cfg.Transfers.AllowSeamless)
            result.Error("transfers.default_mode = 'seamless' requires transfers.allow_seamless = true");
        if (mode == Seamless && cfg.Transfers.RequireSeamlessCapability && !cfg.Transfers.FallbackToRedirectWhenSeamlessUnavailable)
            result.Warn("transfers.default_mode = 'seamless' will reject players without Nimbus client capability instead of falling back to redirect");
        if (cfg.Transfers.AllowSeamless && !cfg.Transfers.RequireSeamlessCapability)
            result.Warn("transfers.require_seamless_capability = false allows seamless requests without the Nimbus client handshake");
        if (cfg.Transfers.EnableUnsafeSeamlessSplice)
            result.Warn("transfers.enable_unsafe_seamless_splice = true allows live splice without Nimbus client capability");

        var redirectAddress = (cfg.Transfers.RedirectAddress ?? "").Trim();
        if (redirectAddress.Length > 0 && !IsHostOrHostPort(redirectAddress))
            result.Error($"transfers.redirect_address must be 'host' or 'host:port', got '{cfg.Transfers.RedirectAddress}'");
    }

    // "host" or "host:port". Hostnames are not resolved here; this only rejects strings a
    // VS client could not dial (schemes, spaces, bad ports).
    private static bool IsHostOrHostPort(string value)
    {
        if (value.Contains("://") || value.Any(char.IsWhiteSpace)) return false;
        int idx = value.LastIndexOf(':');
        string host = value;
        if (idx >= 0 && !value.Contains('['))
        {
            if (idx == 0 || idx == value.Length - 1) return false;
            if (!int.TryParse(value.AsSpan(idx + 1), out int port) || port <= 0 || port > 65535) return false;
            host = value.Substring(0, idx);
        }
        return host.Length > 0;
    }

    private static void ValidateAdmin(ProxyConfig cfg, ProxyConfigValidation result)
    {
        if (!cfg.Admin.Enabled) return;

        var ep = ValidateEndpoint(cfg.Admin.Bind, "admin.bind", requireIpAddress: true, result);
        if (ep != null && !IsLoopback(ep.Address) && string.IsNullOrWhiteSpace(cfg.Admin.Secret))
            result.Error("admin.bind is not loopback, so admin.secret must be set");

        if (cfg.Admin.GrantedPermissions.Count == 0)
            result.Warn("admin.granted_permissions is empty; every admin command will be denied");
    }

    private static void ValidateRegistry(ProxyConfig cfg, ProxyConfigValidation result)
    {
        var mode = (cfg.Registry.Mode ?? "").Trim().ToLowerInvariant();
        if (mode is not "" and not "disabled" and not "embedded" and not "remote")
        {
            result.Error($"registry.mode must be 'disabled', 'embedded', or 'remote', got '{cfg.Registry.Mode}'");
            return;
        }

        if (cfg.Registry.ReservationTtlSeconds <= 0)
            result.Error("registry.reservation_ttl_seconds must be greater than zero");
        if (cfg.Registry.MaxReservationTtlSeconds <= 0)
            result.Error("registry.max_reservation_ttl_seconds must be greater than zero");
        if (cfg.Registry.ReservationTtlSeconds > cfg.Registry.MaxReservationTtlSeconds)
            result.Warn("registry.reservation_ttl_seconds is greater than registry.max_reservation_ttl_seconds and will be clamped");
        if (cfg.Registry.TransferIntentPollMs < 250)
            result.Warn("registry.transfer_intent_poll_ms below 250 will be clamped to 250");
        if (cfg.Registry.SeamlessReadyWaitTimeoutSeconds <= 0)
            result.Error("registry.seamless_ready_wait_timeout_seconds must be greater than zero");

        if (mode == "remote") ValidateRemoteRegistry(cfg, result);
        if (mode == "embedded") ValidateEmbeddedRegistry(cfg, result);

        ValidateApiTokens(cfg, mode, result);
    }

    // What remote mode requires: a URL to reach the standalone registry at, and a shared secret to
    // authenticate to it with.
    private static void ValidateRemoteRegistry(ProxyConfig cfg, ProxyConfigValidation result)
    {
        if (string.IsNullOrWhiteSpace(cfg.Registry.Url))
            result.Error("registry.url is required when registry.mode = 'remote'");
        else if (!Uri.TryCreate(cfg.Registry.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or Https))
            result.Error("registry.url must be an absolute http or https URL");
        if (string.IsNullOrWhiteSpace(cfg.Registry.SharedSecret))
            result.Error("registry.shared_secret is required when registry.mode = 'remote'");
    }

    // What embedded mode requires of an explicitly set bind: it has to be a real http/https URL,
    // and if it faces off-box it may not still be carrying the default shared secret. An empty bind
    // takes the loopback default and needs neither check.
    private static void ValidateEmbeddedRegistry(ProxyConfig cfg, ProxyConfigValidation result)
    {
        if (string.IsNullOrWhiteSpace(cfg.Registry.EmbeddedBind)) return;

        if (!Uri.TryCreate(cfg.Registry.EmbeddedBind, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or Https))
        {
            result.Error("registry.embedded_bind must be an absolute http or https URL, or empty");
        }
        else if (!IsLoopbackOrLocalhost(uri.Host) && IsDefaultSecret(cfg.Registry.EmbeddedSharedSecret))
        {
            result.Error("registry.embedded_bind is not loopback, so registry.embedded_shared_secret must be changed from the default");
        }
    }

    // The [api_tokens] settings on the embedded registry. All of them are inert in remote mode,
    // where the standalone registry reads its own section, and saying so beats letting an
    // operator wonder why the switch they flipped changed nothing.
    private static void ValidateApiTokens(ProxyConfig cfg, string mode, ProxyConfigValidation result)
    {
        if (!cfg.Registry.ApiTokensEnabled) return;

        if (mode != "embedded")
        {
            result.Warn("registry.api_tokens_enabled only applies to the embedded registry; in remote mode the standalone registry's own [api_tokens] section decides");
            return;
        }

        if (cfg.Registry.ApiTokensRateLimitPerMinute <= 0)
            result.Error("registry.api_tokens_rate_limit_per_minute must be greater than zero");

        // The configuration where bearer auth refuses every request it is given: token auth is
        // accepted on loopback or on this registry's own TLS listener, and a plain-HTTP bind that
        // outside callers can reach is neither. Nothing is broken by it, which is exactly why it
        // is worth a line: the tokens simply never work and there is no other signal.
        if (string.IsNullOrWhiteSpace(cfg.Registry.EmbeddedBind)) return;
        if (!Uri.TryCreate(cfg.Registry.EmbeddedBind, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme == Https || IsLoopbackOrLocalhost(uri.Host)) return;
        if (cfg.Registry.ApiTokensTrustForwardedProto)
        {
            result.Warn("registry.api_tokens_trust_forwarded_proto = true makes the registry believe an X-Forwarded-Proto header on a plain-HTTP bind; only set it when a TLS-terminating proxy is the sole route to registry.embedded_bind");
            return;
        }
        result.Warn("registry.api_tokens_enabled = true on a non-loopback plain-HTTP registry.embedded_bind: bearer auth will refuse every request, because a token is only as safe as the transport under it. Serve https, bind loopback, or set registry.api_tokens_trust_forwarded_proto behind a TLS-terminating proxy");
    }

    private static void ValidateWhitelist(ProxyConfig cfg, ProxyConfigValidation result)
    {
        foreach (var serverId in cfg.Whitelist.Servers)
        {
            if (string.IsNullOrWhiteSpace(serverId))
            {
                result.Warn("whitelist.servers contains an empty server id");
                continue;
            }
            if (!HasServer(cfg, serverId))
                result.Warn($"whitelist.servers references unknown server '{serverId}'");
        }

        if (!cfg.Whitelist.Enabled) return;

        var mode = (cfg.Registry.Mode ?? "").Trim().ToLowerInvariant();
        if (mode is "" or "disabled")
            result.Error("whitelist enforcement needs a registry to read the list from, but registry.mode is 'disabled'");
        if (cfg.Whitelist.FailOpenUntilFirstSync)
            result.Warn("whitelist.fail_open_until_first_sync = true lets everyone in until the registry answers once");
    }

    private static void ValidateAdvanced(ProxyConfig cfg, ProxyConfigValidation result)
    {
        if (cfg.Advanced.ConnectTimeoutMs <= 0)
            result.Error("advanced.connect_timeout_ms must be greater than zero");
        if (cfg.Advanced.BufferSize < 1024)
            result.Error("advanced.buffer_size must be at least 1024");
    }

    private static void ValidatePersistence(ProxyConfig cfg, ProxyConfigValidation result)
    {
        if (!cfg.Persistence.PersistDrainFlags) return;
        if (string.IsNullOrWhiteSpace(cfg.Persistence.DrainFlagsFile))
            result.Error("persistence.drain_flags_file must be set when persistence.persist_drain_flags = true");
    }

    private static void ValidateMetrics(ProxyConfig cfg, ProxyConfigValidation result)
    {
        if (!cfg.Metrics.Enabled) return;

        if (!Uri.TryCreate(cfg.Metrics.Bind, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or Https))
            result.Error("metrics.bind must be an absolute http or https URL");
        else if (!IsLoopbackOrLocalhost(uri.Host))
        {
            result.Warn("metrics.bind is not loopback. Metrics are unauthenticated");
            if (cfg.Metrics.StatusApi && string.IsNullOrWhiteSpace(cfg.Metrics.StatusApiToken))
                result.Warn("metrics.bind is not loopback and metrics.status_api_token is empty; /status is readable by anyone who can reach the bind");
        }
        if (string.IsNullOrWhiteSpace(cfg.Metrics.Path) || !cfg.Metrics.Path.StartsWith('/'))
            result.Error("metrics.path must start with '/'");
    }

    private static void ValidateStatus(ProxyConfig cfg, ProxyConfigValidation result)
    {
        if (!cfg.Status.Enabled) return;
        if (string.IsNullOrWhiteSpace(cfg.Status.Name))
            result.Error("status.name must be set when status.enabled = true");
        if (cfg.Status.MaxPlayers < 0)
            result.Error("status.max_players cannot be negative");
        if (cfg.Status.QueryTimeoutMs < 100)
            result.Error("status.query_timeout_ms must be at least 100");
    }

    private static void ValidatePlugins(ProxyConfig cfg, ProxyConfigValidation result)
    {
        if (!cfg.Plugins.Enabled) return;
        if (string.IsNullOrWhiteSpace(cfg.Plugins.Directory))
            result.Error("plugins.directory must be set when plugins.enabled = true");
        foreach (var id in cfg.Plugins.Disabled.Where(id => !IsPluginId(id)))
            result.Error($"plugins.disabled contains invalid plugin id '{id}'");
    }

    private static IPEndPoint? ValidateEndpoint(string value, string label, bool requireIpAddress, ProxyConfigValidation result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result.Error($"{label}: empty");
            return null;
        }

        int idx = value.LastIndexOf(':');
        if (idx <= 0 || idx == value.Length - 1)
        {
            result.Error($"{label}: must be 'host:port', got '{value}'");
            return null;
        }

        string host = value.Substring(0, idx);
        if (!int.TryParse(value.AsSpan(idx + 1), out int port) || port <= 0 || port > 65535)
        {
            result.Error($"{label}: invalid port in '{value}'");
            return null;
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            if (requireIpAddress)
            {
                result.Error($"{label}: host must be an IP address, got '{host}'");
                return null;
            }
            return null;
        }

        return new IPEndPoint(address, port);
    }

    private static string NormalizeMode(string mode)
        => string.Equals(mode, "splice", StringComparison.OrdinalIgnoreCase) ? Seamless : (mode ?? "").Trim().ToLowerInvariant();

    private static bool IsLoopback(IPAddress address)
        => IPAddress.IsLoopback(address);

    private static bool IsLoopbackOrLocalhost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var address) && IsLoopback(address);
    }

    // The list lives in Nimbus.Shared so the standalone registry and the backend mod refuse the
    // same values this does. A literal the proxy rejects and a backend accepts is a network held
    // together by a secret one of its three components considers unset.
    private static bool IsDefaultSecret(string secret)
        => Nimbus.Shared.SecretPlaceholders.IsPlaceholder(secret);

    private static bool IsPluginId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-') continue;
            return false;
        }
        return true;
    }

    private static bool HasServer(ProxyConfig cfg, string serverId)
        => cfg.Servers.Keys.Any(key => string.Equals(key, serverId, StringComparison.OrdinalIgnoreCase));
}
