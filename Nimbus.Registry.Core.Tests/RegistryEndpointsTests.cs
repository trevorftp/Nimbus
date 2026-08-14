using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Nimbus.Shared;
using Nimbus.Shared.Models;
using Nimbus.Shared.Security;
using Xunit;

namespace Nimbus.Registry.Core.Tests;

/// <summary>
/// Boots the real registry pipeline (RegistryHosting + HmacAuthMiddleware + Endpoints) on
/// a loopback port and drives it over actual HTTP, so the auth middleware, the routing and
/// the JSON contracts are exercised exactly as a backend or proxy sees them.
/// </summary>
public class RegistryEndpointsTests
{
    private const string Secret = "endpoint-test-secret";

    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required string BaseUrl { get; init; }
        public required string StateDir { get; init; }
        public HttpClient Client { get; } = new();

        public static async Task<Host> StartAsync(RegistryConfig? cfg = null)
        {
            cfg ??= new RegistryConfig { SharedSecret = Secret };
            // The ban list and the whitelist are now file-backed, and the default directory is
            // the working one. Every host in this class would otherwise read and write the same
            // two files, so a ban added by one test would be waiting for the next one, and for
            // the next run of the suite. A directory per host keeps each one on its own state.
            cfg.StateDir = Path.Combine(Path.GetTempPath(), "nimbus-endpoint-tests-" + Guid.NewGuid().ToString("N"));
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.AddNimbusRegistry(cfg, withMasterServer: false);
            var app = builder.Build();
            app.UseNimbusRegistry();
            await app.StartAsync();
            return new Host { App = app, BaseUrl = app.Urls.First(), StateDir = cfg.StateDir };
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            try { Directory.Delete(StateDir, recursive: true); } catch { /* never created, or already gone */ }
        }
    }

    /// <summary>
    /// A JSON payload that refuses to say how long it is. HttpClient answers unknown content
    /// length on HTTP/1.1 by framing the request with Transfer-Encoding: chunked, which is the
    /// shape third-party clients hit in #91 and the one ByteArrayContent can never produce.
    /// </summary>
    private sealed class ChunkedJsonContent : HttpContent
    {
        private readonly byte[] _bytes;

        public ChunkedJsonContent(byte[] bytes)
        {
            _bytes = bytes;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(_bytes, 0, _bytes.Length);
    }

    /// <summary>Builds a request carrying the four X-Nimbus-* headers. Every knob can be
    /// bent out of shape to probe a specific middleware rejection.</summary>
    private static HttpRequestMessage Signed(
        HttpMethod method, string baseUrl, string pathAndQuery,
        string secret = Secret, object? body = null,
        long? timestamp = null, string? nonce = null, int? protocol = null, string? signature = null,
        bool chunked = false)
    {
        byte[] bytes = body is null ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(body);
        var msg = new HttpRequestMessage(method, baseUrl.TrimEnd('/') + pathAndQuery);
        if (body is not null)
        {
            msg.Content = chunked ? new ChunkedJsonContent(bytes) : new ByteArrayContent(bytes);
            msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        long ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string n = nonce ?? HmacSigner.NewNonce();
        int proto = protocol ?? NimbusProtocol.ProtocolVersion;
        string path = pathAndQuery.Split('?')[0];
        string sig = signature
            ?? HmacSigner.Sign(secret, HmacSigner.CanonicalString(method.Method, path, proto, ts, n, bytes));

        msg.Headers.Add(NimbusProtocol.SignatureHeader, sig);
        msg.Headers.Add(NimbusProtocol.TimestampHeader, ts.ToString());
        msg.Headers.Add(NimbusProtocol.NonceHeader, n);
        msg.Headers.Add(NimbusProtocol.ProtocolHeader, proto.ToString());
        return msg;
    }

    /// <summary>Writes a hand-built request straight onto the socket and hands back what came
    /// out, for the framings HttpClient will not produce.</summary>
    private static async Task<string> SendRawAsync(string baseUrl, string request)
    {
        var uri = new Uri(baseUrl);
        using var conn = new TcpClient();
        await conn.ConnectAsync(uri.Host, uri.Port);
        using var stream = conn.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        var buffer = new byte[4096];
        int read = await stream.ReadAsync(buffer);
        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    /// <summary>The four X-Nimbus-* header lines for a bodyless request, ready to paste into a
    /// raw request.</summary>
    private static string SignedHeaderLines(string method, string path)
    {
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string nonce = HmacSigner.NewNonce();
        int proto = NimbusProtocol.ProtocolVersion;
        string sig = HmacSigner.Sign(Secret,
            HmacSigner.CanonicalString(method, path, proto, ts, nonce, Array.Empty<byte>()));
        return $"{NimbusProtocol.SignatureHeader}: {sig}\r\n"
            + $"{NimbusProtocol.TimestampHeader}: {ts}\r\n"
            + $"{NimbusProtocol.NonceHeader}: {nonce}\r\n"
            + $"{NimbusProtocol.ProtocolHeader}: {proto}\r\n";
    }

    private static BackendHeartbeat Heartbeat(string id = "backend-1") => new()
    {
        ServerId = id,
        DisplayName = id,
        PublicHost = "10.0.0.1",
        PublicPort = 42421,
        Players = 3,
        MaxPlayers = 32,
    };

    // ---- middleware ----

    [Fact]
    public async Task HealthAndRoot_AreUnauthenticated()
    {
        await using var host = await Host.StartAsync();

        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(host.BaseUrl + "/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(host.BaseUrl + "/")).StatusCode);
    }

    [Fact]
    public async Task Api_WithoutNimbusHeaders_Is401()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.GetAsync(host.BaseUrl + "/api/servers");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("missing nimbus headers", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Api_WithTheWrongSecret_Is401()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.SendAsync(
            Signed(HttpMethod.Get, host.BaseUrl, "/api/servers", secret: "not-the-secret"));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("bad signature", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Api_OutsideTheClockSkewWindow_Is401()
    {
        await using var host = await Host.StartAsync();
        long stale = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - NimbusProtocol.MaxClockSkewSeconds - 5;

        var resp = await host.Client.SendAsync(
            Signed(HttpMethod.Get, host.BaseUrl, "/api/servers", timestamp: stale));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("clock skew", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Api_ReplayingANonce_Is401()
    {
        await using var host = await Host.StartAsync();
        string nonce = HmacSigner.NewNonce();

        var first = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/servers", nonce: nonce));
        var replay = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/servers", nonce: nonce));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Contains("replay", await replay.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Api_WithAForeignProtocolVersion_Is401()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.SendAsync(
            Signed(HttpMethod.Get, host.BaseUrl, "/api/servers", protocol: NimbusProtocol.ProtocolVersion + 1));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("protocol mismatch", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Api_SignedWithARotationSecret_IsAccepted()
    {
        // Rotation story: promote the new secret, keep the old one in AcceptedSecrets
        // until every backend has redeployed.
        var cfg = new RegistryConfig { SharedSecret = "brand-new", AcceptedSecrets = new[] { Secret } };
        await using var host = await Host.StartAsync(cfg);

        var withOld = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/servers", secret: Secret));
        var withNew = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/servers", secret: "brand-new"));

        Assert.Equal(HttpStatusCode.OK, withOld.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withNew.StatusCode);
    }

    // ---- endpoints ----

    [Fact]
    public async Task Heartbeat_UpsertsTheBackend_VisibleInTheSnapshot()
    {
        await using var host = await Host.StartAsync();

        var beat = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: Heartbeat()));
        Assert.Equal(HttpStatusCode.OK, beat.StatusCode);
        var beatBody = await beat.Content.ReadFromJsonAsync<BackendHeartbeatResponse>();
        Assert.True(beatBody!.Ok);

        var servers = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/servers"));
        var snapshot = await servers.Content.ReadFromJsonAsync<NetworkSnapshot>();

        var backend = Assert.Single(snapshot!.Backends);
        Assert.Equal("backend-1", backend.ServerId);
        Assert.False(backend.Stale);
        Assert.Equal(3, snapshot.TotalPlayers);
        Assert.Equal(32, snapshot.TotalCapacity);
    }

    [Fact]
    public async Task Heartbeat_DeliversFailuresOnlyToTheirSource_AndOnlyOnce()
    {
        await using var host = await Host.StartAsync(new RegistryConfig
        {
            SharedSecret = Secret,
            SeamlessReadyWaitTimeoutSeconds = 41,
        });
        var failure = new TransferFailed
        {
            ClientTransferId = "transfer-51",
            SourceServerId = "backend-1",
            Reason = "target is in maintenance",
            FailedAtUnix = 123,
        };

        var report = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/transfer-failures", body: failure));
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);

        var retry = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/transfer-failures", body: failure));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);

        var other = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: Heartbeat("backend-2")));
        var otherBody = await other.Content.ReadFromJsonAsync<BackendHeartbeatResponse>();
        Assert.Empty(otherBody!.FailedTransfers);

        var first = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: Heartbeat()));
        var firstBody = await first.Content.ReadFromJsonAsync<BackendHeartbeatResponse>();
        var delivered = Assert.Single(firstBody!.FailedTransfers);
        Assert.Equal("transfer-51", delivered.ClientTransferId);
        Assert.Equal("target is in maintenance", delivered.Reason);
        Assert.Equal(41, firstBody.SeamlessReadyWaitTimeoutSeconds);

        var second = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: Heartbeat()));
        var secondBody = await second.Content.ReadFromJsonAsync<BackendHeartbeatResponse>();
        Assert.Empty(secondBody!.FailedTransfers);
    }

    [Fact]
    public async Task Heartbeat_WithoutAServerId_Is400()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: new { DisplayName = "anon" }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Reservation_MintedThenConsumedById_IsSingleUse()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: Heartbeat()));

        var mint = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations",
            body: new { PlayerUid = "uid-1", PlayerName = "alice", TargetServerId = "backend-1", RealRemoteIp = "203.0.113.9" }));
        Assert.Equal(HttpStatusCode.OK, mint.StatusCode);
        var minted = await mint.Content.ReadFromJsonAsync<ReservationResponse>();
        string id = minted!.Reservation!.Id;
        Assert.Equal("203.0.113.9", minted.Reservation.RealRemoteIp);

        var consume = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, $"/api/reservations/{id}/consume?target=backend-1"));
        Assert.Equal(HttpStatusCode.OK, consume.StatusCode);

        var again = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, $"/api/reservations/{id}/consume?target=backend-1"));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Ban_MintedThenListedThenLifted()
    {
        await using var host = await Host.StartAsync();

        var added = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans",
            body: new { PlayerUid = "uid-b1", PlayerName = "griefer", Reason = "grief", BannedBy = "admin" }));
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var ban = await added.Content.ReadFromJsonAsync<BanResponse>();
        Assert.True(ban!.Ok);
        Assert.True(ban.Ban!.IsNetworkWide);
        Assert.Equal(0, ban.Ban.ExpiresAtUnix);

        var listed = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/bans"));
        var list = await listed.Content.ReadFromJsonAsync<BanListResponse>();
        Assert.Single(list!.Bans);
        Assert.Equal("griefer", list.Bans[0].PlayerName);

        var lifted = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans/lift",
            body: new { PlayerUid = "uid-b1" }));
        Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);

        var again = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans/lift",
            body: new { PlayerUid = "uid-b1" }));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        var empty = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/bans"));
        Assert.Empty((await empty.Content.ReadFromJsonAsync<BanListResponse>())!.Bans);
    }

    [Fact]
    public async Task Ban_WithADuration_GetsAnExpiry()
    {
        await using var host = await Host.StartAsync();

        var added = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans",
            body: new { PlayerUid = "uid-b2", DurationSeconds = 3600 }));

        var ban = await added.Content.ReadFromJsonAsync<BanResponse>();
        Assert.True(ban!.Ban!.ExpiresAtUnix > 0);
    }

    [Fact]
    public async Task ScopedBan_IsLiftedOnlyWithItsServerId()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans",
            body: new { PlayerUid = "uid-b3", ServerId = "creative" }));

        // Lifting without a scope targets the network-wide entry, which does not exist here.
        var wrongScope = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans/lift",
            body: new { PlayerUid = "uid-b3" }));
        Assert.Equal(HttpStatusCode.NotFound, wrongScope.StatusCode);

        var rightScope = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans/lift",
            body: new { PlayerUid = "uid-b3", ServerId = "creative" }));
        Assert.Equal(HttpStatusCode.OK, rightScope.StatusCode);
    }

    // Deprecated path, kept covered on purpose: a proxy built before the arguments moved into
    // the body sends no body at all, and must keep working against a newer registry.
    [Fact]
    public async Task BanLift_WithNoBody_FallsBackToTheQuery()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans",
            body: new { PlayerUid = "uid-b5", ServerId = "creative" }));

        var lifted = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/bans/lift?uid=uid-b5&server=creative"));

        Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);
    }

    // The query is outside the signed material, so an on-path attacker can rewrite it on a
    // request already in flight. The body decides, and the rewritten query buys nothing.
    [Fact]
    public async Task BanLift_IgnoresAQueryThatContradictsTheBody()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans",
            body: new { PlayerUid = "uid-asked" }));
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans",
            body: new { PlayerUid = "uid-tampered" }));

        var lifted = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl,
            "/api/bans/lift?uid=uid-tampered", body: new { PlayerUid = "uid-asked" }));
        Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);

        var listed = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/bans"));
        var left = Assert.Single((await listed.Content.ReadFromJsonAsync<BanListResponse>())!.Bans);
        Assert.Equal("uid-tampered", left.PlayerUid);
    }

    // The other side of the same decision, and the one the fallback exists for: a request that
    // announces no body by any means is still answered from the query. Hand-built because
    // HttpClient always writes a zero Content-Length on a bodyless POST and so cannot leave
    // both headers out.
    [Fact]
    public async Task BanLift_WithNeitherALengthNorChunking_StillFallsBackToTheQuery()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans",
            body: new { PlayerUid = "uid-b7", ServerId = "creative" }));

        var uri = new Uri(host.BaseUrl);
        string response = await SendRawAsync(host.BaseUrl,
            "POST /api/bans/lift?uid=uid-b7&server=creative HTTP/1.1\r\n"
            + $"Host: {uri.Host}:{uri.Port}\r\n"
            + SignedHeaderLines("POST", "/api/bans/lift")
            + "Connection: close\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 200 OK", response, StringComparison.Ordinal);
    }

    // Proof, before anything is read into the two tests below, that ChunkedJsonContent really
    // does put chunked framing on the wire. A bare socket stands in for the registry and keeps
    // the bytes HttpClient wrote, so the framing is observed rather than assumed.
    [Fact]
    public async Task ChunkedJsonContent_PutsChunkedFramingOnTheWire()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var capture = Task.Run(async () =>
        {
            using var conn = await listener.AcceptTcpClientAsync();
            using var stream = conn.GetStream();
            var buffer = new byte[4096];
            var raw = new StringBuilder();
            // The terminating zero-length chunk closes the request; no header line can be a
            // lone "0", so this never trips on the header block.
            while (!raw.ToString().Contains("\r\n0\r\n\r\n", StringComparison.Ordinal))
            {
                int read = await stream.ReadAsync(buffer);
                if (read == 0) break;
                raw.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            return raw.ToString();
        });

        string payload = JsonSerializer.Serialize(new { PlayerUid = "uid-wire" });
        using var client = new HttpClient();
        var msg = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/bans/lift")
        {
            Content = new ChunkedJsonContent(Encoding.UTF8.GetBytes(payload)),
        };
        await client.SendAsync(msg);

        string request = await capture;

        Assert.Contains("Transfer-Encoding: chunked", request, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Content-Length", request, StringComparison.OrdinalIgnoreCase);
        // Size line in hex, payload, terminating chunk: the body only exists as chunks.
        Assert.Contains($"\r\n{payload.Length:x}\r\n{payload}\r\n0\r\n\r\n", request, StringComparison.Ordinal);
    }

    // #91: a client whose HTTP stack chunks announces no Content-Length, and the registry used
    // to read that as "no body at all" and answer from the query instead, losing the arguments.
    // Also the proof that a chunked body survives HmacAuthMiddleware: the signature covers those
    // bytes, so anything other than an exact read would come back 401 rather than 200.
    [Fact]
    public async Task BanLift_WithAChunkedBody_LiftsTheBan()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans",
            body: new { PlayerUid = "uid-b6", ServerId = "creative" }));

        var lifted = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans/lift",
            body: new { PlayerUid = "uid-b6", ServerId = "creative" }, chunked: true));
        Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);

        var empty = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/bans"));
        Assert.Empty((await empty.Content.ReadFromJsonAsync<BanListResponse>())!.Bans);
    }

    // The body still settles both arguments when it arrives in chunks: were it dropped, the
    // contradictory query would take over and lift the wrong ban.
    [Fact]
    public async Task BanLift_WithAChunkedBody_IgnoresAQueryThatContradictsIt()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans",
            body: new { PlayerUid = "uid-asked" }));
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans",
            body: new { PlayerUid = "uid-tampered" }));

        var lifted = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl,
            "/api/bans/lift?uid=uid-tampered", body: new { PlayerUid = "uid-asked" }, chunked: true));
        Assert.Equal(HttpStatusCode.OK, lifted.StatusCode);

        var listed = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/bans"));
        var left = Assert.Single((await listed.Content.ReadFromJsonAsync<BanListResponse>())!.Bans);
        Assert.Equal("uid-tampered", left.PlayerUid);
    }

    [Fact]
    public async Task Ban_WithoutAPlayerUid_Is400()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans",
            body: new { Reason = "no uid" }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Bans_RequireASignature()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.GetAsync($"{host.BaseUrl}/api/bans");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task WhitelistEntry_AddedThenListedThenRemoved()
    {
        await using var host = await Host.StartAsync();

        var added = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist",
            body: new { PlayerUid = "uid-w1", PlayerName = "builder", Note = "beta tester", AddedBy = "admin" }));
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var entry = await added.Content.ReadFromJsonAsync<WhitelistResponse>();
        Assert.True(entry!.Ok);
        Assert.True(entry.Entry!.IsNetworkWide);
        Assert.Equal(0, entry.Entry.ExpiresAtUnix);

        var listed = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/whitelist"));
        var list = await listed.Content.ReadFromJsonAsync<WhitelistListResponse>();
        Assert.Single(list!.Entries);
        Assert.Equal("builder", list.Entries[0].PlayerName);

        var removed = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist/remove",
            body: new { PlayerUid = "uid-w1" }));
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        var again = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist/remove",
            body: new { PlayerUid = "uid-w1" }));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        var empty = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/whitelist"));
        Assert.Empty((await empty.Content.ReadFromJsonAsync<WhitelistListResponse>())!.Entries);
    }

    [Fact]
    public async Task WhitelistEntry_WithADuration_GetsAnExpiry()
    {
        await using var host = await Host.StartAsync();

        var added = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist",
            body: new { PlayerUid = "uid-w2", DurationSeconds = 3600 }));

        var entry = await added.Content.ReadFromJsonAsync<WhitelistResponse>();
        Assert.True(entry!.Entry!.ExpiresAtUnix > 0);
    }

    [Fact]
    public async Task ScopedWhitelistEntry_CoversItsOwnBackendOnly()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist",
            body: new { PlayerUid = "uid-w3", ServerId = "staff" }));

        var listed = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/whitelist"));
        var scoped = Assert.Single((await listed.Content.ReadFromJsonAsync<WhitelistListResponse>())!.Entries);
        Assert.False(scoped.IsNetworkWide);
        Assert.True(scoped.Covers("staff"));
        Assert.False(scoped.Covers("creative"));
        // An empty serverId asks about the network itself, which a scoped entry never covers.
        Assert.False(scoped.Covers(""));

        // Removing without the scope targets the network-wide entry, which does not exist here.
        var wrongScope = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist/remove",
            body: new { PlayerUid = "uid-w3" }));
        Assert.Equal(HttpStatusCode.NotFound, wrongScope.StatusCode);

        var rightScope = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist/remove",
            body: new { PlayerUid = "uid-w3", ServerId = "staff" }));
        Assert.Equal(HttpStatusCode.OK, rightScope.StatusCode);
    }

    // Deprecated path, kept covered on purpose: see BanLift_WithNoBody_FallsBackToTheQuery.
    [Fact]
    public async Task WhitelistRemove_WithNoBody_FallsBackToTheQuery()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist",
            body: new { PlayerUid = "uid-w5", ServerId = "staff" }));

        var removed = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist/remove?uid=uid-w5&server=staff"));

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
    }

    // Same tamper case as BanLift_IgnoresAQueryThatContradictsTheBody: the query is unsigned,
    // so it never gets to choose the entry.
    [Fact]
    public async Task WhitelistRemove_IgnoresAQueryThatContradictsTheBody()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist",
            body: new { PlayerUid = "uid-asked", ServerId = "staff" }));
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist",
            body: new { PlayerUid = "uid-tampered", ServerId = "staff" }));

        var removed = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl,
            "/api/whitelist/remove?uid=uid-tampered&server=staff",
            body: new { PlayerUid = "uid-asked", ServerId = "staff" }));
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        var listed = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/whitelist"));
        var left = Assert.Single((await listed.Content.ReadFromJsonAsync<WhitelistListResponse>())!.Entries);
        Assert.Equal("uid-tampered", left.PlayerUid);
    }

    // The other half of #91: same chunked framing, same scoped argument in the body, against
    // the second endpoint that accepts an optional body.
    [Fact]
    public async Task WhitelistRemove_WithAChunkedBody_RemovesTheEntry()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist",
            body: new { PlayerUid = "uid-w6", ServerId = "staff" }));

        var removed = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist/remove",
            body: new { PlayerUid = "uid-w6", ServerId = "staff" }, chunked: true));
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);

        var empty = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/whitelist"));
        Assert.Empty((await empty.Content.ReadFromJsonAsync<WhitelistListResponse>())!.Entries);
    }

    [Fact]
    public async Task NetworkWideWhitelistEntry_CoversEveryBackend()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist",
            body: new { PlayerUid = "uid-w4" }));

        var listed = await host.Client.SendAsync(Signed(HttpMethod.Get, host.BaseUrl, "/api/whitelist"));
        var entry = Assert.Single((await listed.Content.ReadFromJsonAsync<WhitelistListResponse>())!.Entries);
        Assert.True(entry.Covers("creative"));
        Assert.True(entry.Covers("staff"));
        Assert.True(entry.Covers(null));
    }

    [Fact]
    public async Task WhitelistEntry_WithoutAPlayerUid_Is400()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist",
            body: new { Note = "no uid" }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Whitelist_RequiresASignature()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.GetAsync($"{host.BaseUrl}/api/whitelist");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Whitelist_DoesNotRefuseAReservation()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: Heartbeat()));
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/whitelist",
            body: new { PlayerUid = "uid-w5", ServerId = "backend-1" }));

        // Deliberate: enforcement lives in proxy config, so an unlisted player still gets a
        // reservation here and the proxy is the one that decides whether to seat them.
        var minted = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations",
            body: new { PlayerUid = "uid-not-listed", TargetServerId = "backend-1" }));

        Assert.Equal(HttpStatusCode.OK, minted.StatusCode);
    }

    [Fact]
    public async Task Reservation_CarriesTheClientTransferId_ThroughMintAndConsume()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: Heartbeat()));

        var mint = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations",
            body: new { PlayerUid = "uid-9", PlayerName = "carol", TargetServerId = "backend-1", ClientTransferId = "A1B2C3D4E5" }));
        Assert.Equal(HttpStatusCode.OK, mint.StatusCode);
        var minted = await mint.Content.ReadFromJsonAsync<ReservationResponse>();
        Assert.Equal("A1B2C3D4E5", minted!.Reservation!.ClientTransferId);

        // The target backend consumes by uid on join; the transferId must survive the trip
        // so the target can send the seamless commit (#19).
        var consume = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations/consume-by-uid?uid=uid-9&target=backend-1"));
        Assert.Equal(HttpStatusCode.OK, consume.StatusCode);
        var consumed = await consume.Content.ReadFromJsonAsync<ReservationResponse>();
        Assert.Equal("A1B2C3D4E5", consumed!.Reservation!.ClientTransferId);
    }

    [Fact]
    public async Task Reservation_WithoutAClientTransferId_DefaultsToEmpty()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: Heartbeat()));

        var mint = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations",
            body: new { PlayerUid = "uid-10", TargetServerId = "backend-1" }));

        var minted = await mint.Content.ReadFromJsonAsync<ReservationResponse>();
        Assert.Equal("", minted!.Reservation!.ClientTransferId);
    }

    [Fact]
    public async Task Reservation_ForAnUnregisteredTarget_Is404()
    {
        await using var host = await Host.StartAsync();

        var resp = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations",
            body: new { PlayerUid = "uid-1", TargetServerId = "nowhere" }));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Reservation_ForABannedPair_IsRefused_ButOtherBackendsStillMint()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: Heartbeat()));
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: Heartbeat("backend-2")));
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans",
            body: new { PlayerUid = "uid-1", ServerId = "backend-1", Reason = "grief" }));

        var refused = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations",
            body: new { PlayerUid = "uid-1", TargetServerId = "backend-1" }));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<ReservationResponse>();
        Assert.False(body!.Ok);
        Assert.Null(body.Reservation);

        // The ban covers one backend, so the rest of the network still mints for this player.
        var allowed = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations",
            body: new { PlayerUid = "uid-1", TargetServerId = "backend-2" }));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        // And a network-wide ban closes every backend to them.
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/bans",
            body: new { PlayerUid = "uid-1", Reason = "grief" }));
        var everywhere = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations",
            body: new { PlayerUid = "uid-1", TargetServerId = "backend-2" }));
        Assert.Equal(HttpStatusCode.Forbidden, everywhere.StatusCode);
    }

    [Fact]
    public async Task Reservation_TtlIsClampedToTheConfiguredMaximum()
    {
        var cfg = new RegistryConfig { SharedSecret = Secret, MaxReservationTtlSeconds = 120 };
        await using var host = await Host.StartAsync(cfg);
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: Heartbeat()));

        var mint = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations",
            body: new { PlayerUid = "uid-1", TargetServerId = "backend-1", TtlSeconds = 99999 }));
        var minted = await mint.Content.ReadFromJsonAsync<ReservationResponse>();

        long lifetime = minted!.Reservation!.ExpiresAtUnix - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.InRange(lifetime, 1, 121);
    }

    [Fact]
    public async Task ConsumeByUid_WorksOverHttp_AndIsSingleUse()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: Heartbeat()));
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations",
            body: new { PlayerUid = "uid-7", TargetServerId = "backend-1" }));

        var consume = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations/consume-by-uid?uid=uid-7&target=backend-1"));
        Assert.Equal(HttpStatusCode.OK, consume.StatusCode);
        var consumed = await consume.Content.ReadFromJsonAsync<ReservationResponse>();
        Assert.Equal("uid-7", consumed!.Reservation!.PlayerUid);

        var again = await host.Client.SendAsync(
            Signed(HttpMethod.Post, host.BaseUrl, "/api/reservations/consume-by-uid?uid=uid-7&target=backend-1"));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task TransferIntents_PostedThenDrained_AtMostOnce()
    {
        await using var host = await Host.StartAsync();
        await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/heartbeat", body: Heartbeat()));

        var post = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/transfer-intents",
            body: new { PlayerUid = "uid-1", TargetServerId = "backend-1", Mode = "redirect" }));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var unknown = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/transfer-intents",
            body: new { PlayerUid = "uid-1", TargetServerId = "nowhere" }));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var drain = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/transfer-intents/drain"));
        var drained = await drain.Content.ReadFromJsonAsync<TransferIntentDrainResponse>();
        Assert.Single(drained!.Intents);
        Assert.Equal("uid-1", drained.Intents[0].PlayerUid);

        var empty = await host.Client.SendAsync(Signed(HttpMethod.Post, host.BaseUrl, "/api/transfer-intents/drain"));
        var second = await empty.Content.ReadFromJsonAsync<TransferIntentDrainResponse>();
        Assert.Empty(second!.Intents);
    }
}
