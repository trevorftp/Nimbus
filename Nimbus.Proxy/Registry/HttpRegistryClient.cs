using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Nimbus.Shared;
using Nimbus.Shared.Models;
using Nimbus.Shared.Security;

namespace Nimbus.Proxy;

// Signed HTTP client for a standalone Nimbus.Registry process. Used when registry.mode is
// "remote". For embedded mode the proxy goes through InProcRegistryClient instead.
internal sealed class HttpRegistryClient : IRegistryClient, IDisposable
{
    // Written once because every request this client makes carries it, and a body the registry
    // reads as the wrong media type is a 415 with nothing in the log to point at.
    private const string JsonContentType = "application/json";

    private readonly HttpClient http;
    private readonly RegistryConfig cfg;

    private readonly object snapshotLock = new();
    private NetworkSnapshot? cachedSnapshot;
    private DateTimeOffset cachedSnapshotAt;
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(3);

    public HttpRegistryClient(RegistryConfig cfg)
    {
        this.cfg = cfg;
        http = new HttpClient
        {
            // The trailing slash is what makes BaseAddress keep any path in registry.url instead
            // of resolving the relative request URIs below against the host root. It is a URL
            // separator, so the filesystem-path rule the analyser applies here does not: Path
            // helpers would emit a backslash on Windows and sign a path the registry never sees.
            BaseAddress = new Uri(cfg.Url.TrimEnd('/') + "/"), // NOSONAR
            Timeout = TimeSpan.FromSeconds(Math.Max(1, cfg.HttpTimeoutSeconds))
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Nimbus-Proxy/{NimbusProtocol.NimbusVersion}");
    }

    /// <summary>
    /// Ask the registry to mint a reservation for (playerUid -> targetServerId). Returns the
    /// reservation on success, null on any error (logged). Reason is recorded for audit only.
    /// </summary>
    public async Task<TransferReservation?> MintReservationAsync(
        string playerUid, string playerName, string targetServerId, string? reason, CancellationToken ct,
        string? realRemoteIp = null, int realRemotePort = 0, string? clientTransferId = null)
    {
        var req = new ReservationRequest
        {
            PlayerUid = playerUid,
            PlayerName = playerName ?? "",
            SourceServerId = cfg.ProxyId,
            TargetServerId = targetServerId,
            TtlSeconds = cfg.ReservationTtlSeconds,
            Reason = reason,
            RealRemoteIp = realRemoteIp ?? "",
            RealRemotePort = realRemotePort,
            ClientTransferId = clientTransferId ?? "",
        };

        try
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(req);
            const string path = "/api/reservations";
            using var msg = SignedPost(path, body);

            using var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string detail = await SafeReadAsync(resp).ConfigureAwait(false);
                Log.Warn($"registry POST {path} -> {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}");
                return null;
            }

            var parsed = await resp.Content.ReadFromJsonAsync<ReservationResponse>(cancellationToken: ct).ConfigureAwait(false);
            if (parsed == null || !parsed.Ok || parsed.Reservation == null)
            {
                Log.Warn($"registry returned not-ok: {parsed?.Error ?? "<null>"}");
                return null;
            }
            return parsed.Reservation;
        }
        catch (Exception ex)
        {
            Log.Warn($"registry mint failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns a cached <see cref="NetworkSnapshot"/> (TTL = 3s) or fetches fresh on miss.
    /// Returns null on error. Callers should treat that as "registry unavailable".
    /// </summary>
    public async Task<NetworkSnapshot?> GetServersAsync(CancellationToken ct, bool forceRefresh = false)
    {
        if (!forceRefresh)
        {
            lock (snapshotLock)
            {
                if (cachedSnapshot != null && DateTimeOffset.UtcNow - cachedSnapshotAt < SnapshotTtl)
                    return cachedSnapshot;
            }
        }

        try
        {
            const string path = "/api/servers";
            using var msg = SignedGet(path);
            using var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"registry GET {path} -> {(int)resp.StatusCode} {resp.ReasonPhrase}");
                return null;
            }
            var snap = await resp.Content.ReadFromJsonAsync<NetworkSnapshot>(cancellationToken: ct).ConfigureAwait(false);
            if (snap != null)
            {
                lock (snapshotLock) { cachedSnapshot = snap; cachedSnapshotAt = DateTimeOffset.UtcNow; }
            }
            return snap;
        }
        catch (Exception ex)
        {
            Log.Warn($"registry GET /api/servers failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public async Task<BackendSnapshot?> ResolveByServerIdAsync(string serverId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(serverId)) return null;
        var snap = await GetServersAsync(ct).ConfigureAwait(false);
        if (snap == null) return null;
        return snap.Backends.FirstOrDefault(b => string.Equals(b.ServerId, serverId, StringComparison.OrdinalIgnoreCase));
    }

    // Drain all pending transfer intents from the registry. Each intent is delivered at most
    // once across the network of proxies, so whichever proxy polls first wins. Returns an
    // Empty list on registry error. The dispatcher logs the poll failure.
    public async Task<List<TransferIntent>> DrainTransferIntentsAsync(CancellationToken ct)
    {
        try
        {
            const string path = "/api/transfer-intents/drain";
            byte[] body = Array.Empty<byte>();
            using var msg = SignedPost(path, body);
            using var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return new List<TransferIntent>();
            var parsed = await resp.Content.ReadFromJsonAsync<TransferIntentDrainResponse>(cancellationToken: ct).ConfigureAwait(false);
            return parsed?.Intents ?? new List<TransferIntent>();
        }
        catch (OperationCanceledException) { return new List<TransferIntent>(); }
        catch { return new List<TransferIntent>(); }
    }

    public async Task<bool> ReportTransferFailureAsync(TransferFailed failure, CancellationToken ct)
    {
        try
        {
            const string path = "/api/transfer-failures";
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(failure);
            using var msg = SignedPost(path, body);
            using var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"registry POST {path} -> {(int)resp.StatusCode} {resp.ReasonPhrase}");
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            Log.Warn($"registry failure report failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public async Task<List<NetworkBan>?> GetBansAsync(CancellationToken ct)
    {
        try
        {
            const string path = "/api/bans";
            using var msg = SignedGet(path);
            using var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"registry GET {path} -> {(int)resp.StatusCode} {resp.ReasonPhrase}");
                return null;
            }
            var parsed = await resp.Content.ReadFromJsonAsync<BanListResponse>(cancellationToken: ct).ConfigureAwait(false);
            return parsed?.Bans;
        }
        catch (Exception ex)
        {
            Log.Warn($"registry GET /api/bans failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public async Task<NetworkBan?> AddBanAsync(BanRequest request, CancellationToken ct)
    {
        try
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(request);
            const string path = "/api/bans";
            using var msg = SignedPost(path, body);

            using var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string detail = await SafeReadAsync(resp).ConfigureAwait(false);
                Log.Warn($"registry POST {path} -> {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}");
                return null;
            }
            var parsed = await resp.Content.ReadFromJsonAsync<BanResponse>(cancellationToken: ct).ConfigureAwait(false);
            return parsed != null && parsed.Ok ? parsed.Ban : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"registry ban add failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> LiftBanAsync(string playerUid, string? serverId, CancellationToken ct)
    {
        try
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(
                new BanLiftRequest { PlayerUid = playerUid, ServerId = serverId ?? "" });
            const string path = "/api/bans/lift";
            using var msg = SignedPost(path, body);

            using var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log.Warn($"registry ban lift failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public async Task<List<WhitelistEntry>?> GetWhitelistAsync(CancellationToken ct)
    {
        try
        {
            const string path = "/api/whitelist";
            using var msg = SignedGet(path);
            using var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"registry GET {path} -> {(int)resp.StatusCode} {resp.ReasonPhrase}");
                return null;
            }
            var parsed = await resp.Content.ReadFromJsonAsync<WhitelistListResponse>(cancellationToken: ct).ConfigureAwait(false);
            return parsed?.Entries;
        }
        catch (Exception ex)
        {
            Log.Warn($"registry GET /api/whitelist failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public async Task<WhitelistEntry?> AddWhitelistAsync(WhitelistRequest request, CancellationToken ct)
    {
        try
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(request);
            const string path = "/api/whitelist";
            using var msg = SignedPost(path, body);

            using var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string detail = await SafeReadAsync(resp).ConfigureAwait(false);
                Log.Warn($"registry POST {path} -> {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}");
                return null;
            }
            var parsed = await resp.Content.ReadFromJsonAsync<WhitelistResponse>(cancellationToken: ct).ConfigureAwait(false);
            return parsed != null && parsed.Ok ? parsed.Entry : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"registry whitelist add failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> RemoveWhitelistAsync(string playerUid, string? serverId, CancellationToken ct)
    {
        try
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(
                new WhitelistRemoveRequest { PlayerUid = playerUid, ServerId = serverId ?? "" });
            const string path = "/api/whitelist/remove";
            using var msg = SignedPost(path, body);

            using var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log.Warn($"registry whitelist remove failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // The three token-management calls are signed like every other call on this client: the
    // registry accepts HMAC and nothing else on /api/tokens, so the proxy reaches them with the
    // shared secret rather than with a token of its own. The plaintext comes back exactly once,
    // in this response, and is handed straight to the operator who asked for it.
    public async Task<ApiTokenCreateResponse?> CreateApiTokenAsync(ApiTokenCreateRequest request, CancellationToken ct)
    {
        try
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(request);
            const string path = "/api/tokens";
            using var msg = SignedPost(path, body);

            using var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // The detail is the registry's refusal reason (an unknown scope, a missing name)
                // and never carries a credential: the only response that ever holds one is a 200.
                string detail = await SafeReadAsync(resp).ConfigureAwait(false);
                Log.Warn($"registry POST {path} -> {(int)resp.StatusCode} {resp.ReasonPhrase} {detail}");
                return null;
            }
            var parsed = await resp.Content.ReadFromJsonAsync<ApiTokenCreateResponse>(cancellationToken: ct).ConfigureAwait(false);
            return parsed != null && parsed.Ok ? parsed : null;
        }
        catch (Exception ex)
        {
            // The exception message, not the request: a token create carries no secret on the way
            // out, but the habit of logging the body is how one eventually does.
            Log.Warn($"registry token create failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public async Task<List<ApiToken>?> GetApiTokensAsync(CancellationToken ct)
    {
        try
        {
            const string path = "/api/tokens";
            using var msg = SignedGet(path);
            using var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"registry GET {path} -> {(int)resp.StatusCode} {resp.ReasonPhrase}");
                return null;
            }
            var parsed = await resp.Content.ReadFromJsonAsync<ApiTokenListResponse>(cancellationToken: ct).ConfigureAwait(false);
            return parsed?.Tokens;
        }
        catch (Exception ex)
        {
            Log.Warn($"registry GET /api/tokens failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> RevokeApiTokenAsync(string id, CancellationToken ct)
    {
        try
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(new ApiTokenRevokeRequest { Id = id });
            const string path = "/api/tokens/revoke";
            using var msg = SignedPost(path, body);

            using var resp = await http.SendAsync(msg, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log.Warn($"registry token revoke failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // The one place that knows the convention: `path` carries its leading slash because that is
    // what the signature covers and what the registry rebuilds from HttpRequest.Path, while the
    // request URI is handed path[1..] so it stays relative to BaseAddress. Every endpoint above
    // used to repeat those three lines, which is how the two halves could drift apart.
    private HttpRequestMessage SignedGet(string path)
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, path[1..]);
        Sign(msg, "GET", path, Array.Empty<byte>());
        return msg;
    }

    private HttpRequestMessage SignedPost(string path, byte[] body)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, path[1..]) { Content = new ByteArrayContent(body) };
        msg.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(JsonContentType);
        Sign(msg, "POST", path, body);
        return msg;
    }

    private void Sign(HttpRequestMessage msg, string method, string canonicalPath, byte[] body)
    {
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string nonce = HmacSigner.NewNonce();
        int q = canonicalPath.IndexOf('?');
        string pathForSig = q >= 0 ? canonicalPath.Substring(0, q) : canonicalPath;
        string canonical = HmacSigner.CanonicalString(method, pathForSig, NimbusProtocol.ProtocolVersion, ts, nonce, body);
        string sig = HmacSigner.Sign(cfg.SharedSecret, canonical);
        msg.Headers.Add(NimbusProtocol.SignatureHeader, sig);
        msg.Headers.Add(NimbusProtocol.TimestampHeader, ts.ToString(System.Globalization.CultureInfo.InvariantCulture));
        msg.Headers.Add(NimbusProtocol.NonceHeader, nonce);
        msg.Headers.Add(NimbusProtocol.ProtocolHeader, NimbusProtocol.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp)
    {
        try { return (await resp.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim(); }
        catch { return ""; }
    }

    public void Dispose()
        => http.Dispose();
}
