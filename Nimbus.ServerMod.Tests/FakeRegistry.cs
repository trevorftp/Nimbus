using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nimbus.ServerMod.Tests;

/// <summary>
/// In-process stand-in for the Nimbus registry, listening on a real loopback HTTP port.
/// The mod under test talks to it through its own <c>NimbusRegistryClient</c>, so the full
/// HTTP + HMAC path is exercised. Signature verification is REIMPLEMENTED here (not shared
/// with Nimbus code) on purpose: it cross-checks the client's canonical-string construction
/// against an independent implementation of the documented format
/// <c>METHOD\nPATH\nPROTOCOL\nTIMESTAMP\nNONCE\nSHA256HEX(body)</c>.
/// </summary>
public sealed class FakeRegistry : IDisposable
{
    public sealed record ReceivedRequest(
        string Method,
        string Path,
        string? Uid,
        string? Target,
        string Body,
        bool HasSignatureHeaders,
        bool SignatureValid);

    private readonly HttpListener listener;
    private readonly Thread pump;
    private readonly string sharedSecret;
    private readonly object gate = new();
    private volatile bool stopped;

    /// <summary>Base URL to put in the mod's RegistryUrl, e.g. "http://127.0.0.1:PORT/".</summary>
    public string Url { get; }

    /// <summary>Every request received so far, in order.</summary>
    public IReadOnlyList<ReceivedRequest> Requests
    {
        get { lock (gate) return requests.ToList(); }
    }

    private readonly List<ReceivedRequest> requests = new();

    /// <summary>
    /// Reservation returned (once) by the next consume-by-uid call, as an anonymous object
    /// serialized to camelCase JSON. Null means "no reservation" ({"ok":false}).
    /// </summary>
    public object? NextReservation { get; set; }

    /// <summary>
    /// Snapshot served by GET /api/servers, camelCase JSON. Null means 404 (the mod treats
    /// that as "no snapshot yet"). Not consumed: served on every poll until changed.
    /// </summary>
    public object? ServersSnapshot { get; set; }

    /// <summary>
    /// Response to POST /api/transfer-intents, camelCase JSON. Null means the registry
    /// rejects the intent ({"ok":false, "error":"target server not registered"}).
    /// Not consumed: served on every post until changed.
    /// </summary>
    public object? TransferIntentResponse { get; set; }

    /// <summary>When true, the next seamless intent response remains successful but the next
    /// source heartbeat carries a failure notice for its client transfer id. This models the
    /// proxy accepting the intent and failing after the source has entered the in-flight map.</summary>
    public bool FailNextSeamlessIntent { get; set; }

    public string FailureReason { get; set; } = "target backend is unavailable";
    public int FailureNoticeDeliveries => Volatile.Read(ref failureNoticeDeliveries);

    private string? failureTransferId;
    private bool failureNoticeDelivered;
    private int failureNoticeDeliveries;

    public FakeRegistry(string sharedSecret)
    {
        this.sharedSecret = sharedSecret;
        int port = FreeLoopbackPort();
        Url = $"http://127.0.0.1:{port}/";
        listener = new HttpListener();
        listener.Prefixes.Add(Url);
        listener.Start();
        pump = new Thread(Loop) { IsBackground = true, Name = "fake-nimbus-registry" };
        pump.Start();
    }

    /// <summary>Convenience: a /api/servers snapshot wrapping the given backends.</summary>
    public static object Snapshot(params object[] backends) => new
    {
        backends,
        totalPlayers = 0,
        totalCapacity = 0,
        generatedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
    };

    /// <summary>Convenience: a healthy backend entry unless flagged otherwise.</summary>
    public static object Backend(string serverId, bool stale = false, bool maintenance = false) => new
    {
        serverId,
        displayName = serverId,
        publicHost = "10.0.0.1",
        publicPort = 42421,
        players = 0,
        maxPlayers = 32,
        stale,
        maintenance,
        lastSeenUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        gameVersion = "1.22.0",
    };

    private static int FreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private void Loop()
    {
        while (!stopped)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch when (stopped) { return; }
            catch (Exception) { return; }
            try { Handle(ctx); }
            catch { try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { } }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        byte[] body;
        using (var ms = new MemoryStream())
        {
            ctx.Request.InputStream.CopyTo(ms);
            body = ms.ToArray();
        }

        string path = ctx.Request.Url!.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(ctx.Request.Url.Query);
        bool hasHeaders = ctx.Request.Headers["X-Nimbus-Signature"] != null
                       && ctx.Request.Headers["X-Nimbus-Timestamp"] != null
                       && ctx.Request.Headers["X-Nimbus-Nonce"] != null
                       && ctx.Request.Headers["X-Nimbus-Protocol"] != null;
        bool sigValid = hasHeaders && VerifySignature(ctx.Request, path, body);

        lock (gate)
        {
            requests.Add(new ReceivedRequest(
                ctx.Request.HttpMethod, path, query["uid"], query["target"],
                Encoding.UTF8.GetString(body), hasHeaders, sigValid));
        }

        object? payload;
        if (path == "/api/reservations/consume-by-uid")
        {
            var reservation = NextReservation;
            NextReservation = null; // single-use, like the real ReservationStore
            payload = reservation is null
                ? new { ok = false, error = "no reservation" }
                : new { ok = true, reservation };
        }
        else if (path == "/api/heartbeat")
        {
            object[] failures = Array.Empty<object>();
            lock (gate)
            {
                if (!failureNoticeDelivered && !string.IsNullOrWhiteSpace(failureTransferId))
                {
                    failureNoticeDelivered = true;
                    failureNoticeDeliveries++;
                    failures = new object[]
                    {
                        new
                        {
                            clientTransferId = failureTransferId,
                            sourceServerId = "backend-test",
                            reason = FailureReason,
                            failedAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        },
                    };
                }
            }

            var heartbeat = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["seamlessReadyWaitTimeoutSeconds"] = 75,
            };
            if (failures.Length > 0) heartbeat["failedTransfers"] = failures;
            payload = heartbeat;
        }
        else if (path == "/api/servers")
        {
            payload = ServersSnapshot;
            if (payload is null)
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }
        }
        else if (path == "/api/transfer-intents")
        {
            if (FailNextSeamlessIntent)
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(body);
                    JsonElement root = document.RootElement;
                    string mode = root.GetProperty("Mode").GetString() ?? "";
                    string transferId = root.GetProperty("ClientTransferId").GetString() ?? "";
                    if ((string.Equals(mode, "seamless", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(mode, "splice", StringComparison.OrdinalIgnoreCase))
                        && transferId.Length > 0)
                    {
                        lock (gate) failureTransferId = transferId;
                        FailNextSeamlessIntent = false;
                    }
                }
                catch { /* the request assertion reports malformed test traffic */ }
            }

            payload = TransferIntentResponse
                ?? new { ok = false, error = "target server not registered" };
        }
        else
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        ctx.Response.OutputStream.Write(json);
        ctx.Response.Close();
    }

    // Independent reimplementation of Nimbus's HmacSigner canonical string.
    private bool VerifySignature(HttpListenerRequest req, string path, byte[] body)
    {
        string? provided = req.Headers["X-Nimbus-Signature"];
        string? ts = req.Headers["X-Nimbus-Timestamp"];
        string? nonce = req.Headers["X-Nimbus-Nonce"];
        string? proto = req.Headers["X-Nimbus-Protocol"];
        if (provided is null || ts is null || nonce is null || proto is null) return false;

        string bodyHash = Convert.ToHexString(SHA256.HashData(body));
        string canonical = string.Concat(
            req.HttpMethod.ToUpperInvariant(), "\n",
            path, "\n",
            proto, "\n",
            ts, "\n",
            nonce, "\n",
            bodyHash);
        byte[] mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(sharedSecret), Encoding.UTF8.GetBytes(canonical));
        string expected = Convert.ToHexString(mac);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(provided.ToUpperInvariant()));
    }

    public void Dispose()
    {
        stopped = true;
        try { listener.Stop(); } catch { }
        try { listener.Close(); } catch { }
    }
}
