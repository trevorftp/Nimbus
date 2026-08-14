using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
// Brings AddNimbusRegistry/UseNimbusRegistry into scope. RegistryConfig is deliberately left
// qualified everywhere below, because the proxy declares one of its own under that name.
using Nimbus.Registry;
using Nimbus.Shared;
using Nimbus.Shared.Models;
using Nimbus.Shared.Security;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// The remote-mode registry client against a real registry: the same pipeline the standalone
/// Nimbus.Registry runs, booted on a loopback port, with the proxy's own HttpRegistryClient
/// talking to it over HTTP. Both halves of the signed protocol are therefore under test at once,
/// which is the point: a client that signs the wrong canonical string and a middleware that
/// verifies the wrong one agree with each other and with nobody else.
///
/// The other half of the contract is what the client does when the answer is not a 200. Every
/// call on this interface is documented to hand back null, false or an empty list rather than
/// throw, because the callers are byte pumps and background loops that have nowhere to put an
/// exception.
/// </summary>
public class HttpRegistryClientTests
{
    private const string Secret = "http-client-test-secret";

    private sealed class Registry : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required string BaseUrl { get; init; }
        public required string StateDir { get; init; }
        public HttpClient Raw { get; } = new();

        public static async Task<Registry> StartAsync(Action<Nimbus.Registry.RegistryConfig>? configure = null)
        {
            var cfg = new Nimbus.Registry.RegistryConfig { SharedSecret = Secret };
            configure?.Invoke(cfg);
            // The registry's ban list and whitelist are file-backed now and default to the
            // working directory, so every registry started here would otherwise share one pair
            // of files: a ban added by one test would be waiting for the next one, and for the
            // next run of the suite.
            cfg.StateDir = Path.Combine(Path.GetTempPath(), "nimbus-http-client-tests-" + Guid.NewGuid().ToString("N"));
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.AddNimbusRegistry(cfg, withMasterServer: false);
            var app = builder.Build();
            app.UseNimbusRegistry();
            await app.StartAsync();
            return new Registry { App = app, BaseUrl = app.Urls.First(), StateDir = cfg.StateDir };
        }

        /// <summary>Registers a backend the way a real one does, so reservations and intents have
        /// something to point at.</summary>
        public async Task<BackendHeartbeat> RegisterBackendAsync(string serverId, string host = "10.0.0.1",
            int port = 42421, int players = 3)
        {
            var hb = new BackendHeartbeat
            {
                ServerId = serverId,
                DisplayName = serverId,
                PublicHost = host,
                PublicPort = port,
                Players = players,
                MaxPlayers = 32,
            };
            var resp = await Raw.SendAsync(Signed(HttpMethod.Post, "/api/heartbeat", hb));
            resp.EnsureSuccessStatusCode();
            return hb;
        }

        public async Task<BackendHeartbeatResponse> HeartbeatResponseAsync(string serverId)
        {
            var hb = new BackendHeartbeat
            {
                ServerId = serverId,
                DisplayName = serverId,
                PublicHost = "10.0.0.1",
                PublicPort = 42421,
            };
            var resp = await Raw.SendAsync(Signed(HttpMethod.Post, "/api/heartbeat", hb));
            resp.EnsureSuccessStatusCode();
            return (await resp.Content.ReadFromJsonAsync<BackendHeartbeatResponse>())!;
        }

        public async Task<TransferIntent> QueueIntentAsync(TransferIntentRequest request)
        {
            var resp = await Raw.SendAsync(Signed(HttpMethod.Post, "/api/transfer-intents", request));
            resp.EnsureSuccessStatusCode();
            var parsed = await resp.Content.ReadFromJsonAsync<TransferIntentResponse>();
            return parsed!.Intent!;
        }

        private HttpRequestMessage Signed(HttpMethod method, string path, object? body)
        {
            byte[] bytes = body is null ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(body);
            var msg = new HttpRequestMessage(method, BaseUrl.TrimEnd('/') + path);
            if (body is not null)
            {
                msg.Content = new ByteArrayContent(bytes);
                msg.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            }
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string nonce = HmacSigner.NewNonce();
            msg.Headers.Add(NimbusProtocol.SignatureHeader, HmacSigner.Sign(Secret,
                HmacSigner.CanonicalString(method.Method, path, NimbusProtocol.ProtocolVersion, ts, nonce, bytes)));
            msg.Headers.Add(NimbusProtocol.TimestampHeader, ts.ToString());
            msg.Headers.Add(NimbusProtocol.NonceHeader, nonce);
            msg.Headers.Add(NimbusProtocol.ProtocolHeader, NimbusProtocol.ProtocolVersion.ToString());
            return msg;
        }

        public async ValueTask DisposeAsync()
        {
            Raw.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            try { Directory.Delete(StateDir, recursive: true); } catch { /* never created, or already gone */ }
        }
    }

    private static HttpRegistryClient ClientFor(Registry registry, string secret = Secret,
        Action<RegistryConfig>? configure = null)
    {
        var cfg = new RegistryConfig
        {
            Url = registry.BaseUrl,
            SharedSecret = secret,
            ProxyId = "test-proxy",
            ReservationTtlSeconds = 60,
            HttpTimeoutSeconds = 5,
        };
        configure?.Invoke(cfg);
        return new HttpRegistryClient(cfg);
    }

    private static CancellationToken Ct => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    // ---- the snapshot ----

    [Fact]
    public async Task TheSnapshot_CarriesTheBackendsTheRegistryHasHeardFrom()
    {
        await using var registry = await Registry.StartAsync();
        await registry.RegisterBackendAsync("hub", players: 7);
        using var client = ClientFor(registry);

        var snapshot = await client.GetServersAsync(Ct);

        Assert.NotNull(snapshot);
        var backend = Assert.Single(snapshot!.Backends);
        Assert.Equal("hub", backend.ServerId);
        Assert.Equal("10.0.0.1", backend.PublicHost);
        Assert.Equal(42421, backend.PublicPort);
        Assert.Equal(7, backend.Players);
        Assert.False(backend.Stale);
        Assert.Equal(7, snapshot.TotalPlayers);
    }

    [Fact]
    public async Task TheSnapshotIsHeldBriefly_AndAForcedRefreshGoesBackToTheRegistry()
    {
        await using var registry = await Registry.StartAsync();
        await registry.RegisterBackendAsync("hub");
        using var client = ClientFor(registry);

        Assert.Single((await client.GetServersAsync(Ct))!.Backends);
        await registry.RegisterBackendAsync("creative", port: 42422);

        // Still one: a backend joining mid-second must not cost every routing decision a round
        // trip, so the cached snapshot stands for its few seconds.
        Assert.Single((await client.GetServersAsync(Ct))!.Backends);
        // `nimctl servers --refresh` is the operator's way past that, and it has to work.
        Assert.Equal(2, (await client.GetServersAsync(Ct, forceRefresh: true))!.Backends.Count);
    }

    [Fact]
    public async Task ResolvingByServerId_FindsTheBackendWhateverTheCase()
    {
        await using var registry = await Registry.StartAsync();
        await registry.RegisterBackendAsync("Creative", port: 42422);
        using var client = ClientFor(registry);

        Assert.Equal(42422, (await client.ResolveByServerIdAsync("creative", Ct))!.PublicPort);
        Assert.Null(await client.ResolveByServerIdAsync("nowhere", Ct));
        Assert.Null(await client.ResolveByServerIdAsync("", Ct));
    }

    // ---- reservations ----

    [Fact]
    public async Task AReservation_IsMintedWithTheProxyAsItsSourceAndTheClientEndpointAttached()
    {
        await using var registry = await Registry.StartAsync();
        await registry.RegisterBackendAsync("creative");
        using var client = ClientFor(registry);

        var reservation = await client.MintReservationAsync("uid-1", "alice", "creative", "admin swap", Ct,
            realRemoteIp: "203.0.113.7", realRemotePort: 51234, clientTransferId: "transfer-9");

        Assert.NotNull(reservation);
        Assert.Equal("uid-1", reservation!.PlayerUid);
        Assert.Equal("creative", reservation.TargetServerId);
        Assert.Equal("test-proxy", reservation.SourceServerId);
        Assert.Equal("admin swap", reservation.Reason);
        // The backend records the player's real address from this rather than the proxy's, and
        // the seamless commit on the far side is keyed on the transfer id.
        Assert.Equal("203.0.113.7", reservation.RealRemoteIp);
        Assert.Equal(51234, reservation.RealRemotePort);
        Assert.Equal("transfer-9", reservation.ClientTransferId);
        Assert.True(reservation.ExpiresAtUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task AReservationForABackendNobodyHasHeardFrom_ComesBackNull()
    {
        await using var registry = await Registry.StartAsync();
        using var client = ClientFor(registry);

        // Minting against an unregistered target would produce a reservation no backend can
        // consume, so the 404 has to reach the caller as "no".
        Assert.Null(await client.MintReservationAsync("uid-1", "alice", "nowhere", null, Ct));
    }

    [Fact]
    public async Task AReservationForABannedPlayer_ComesBackNull()
    {
        await using var registry = await Registry.StartAsync();
        await registry.RegisterBackendAsync("creative");
        using var client = ClientFor(registry);
        await client.AddBanAsync(new BanRequest { PlayerUid = "uid-1", ServerId = "creative" }, Ct);

        // The registry is the backstop for a proxy whose ban cache is seconds out of date, and
        // its refusal has to land as a refusal rather than as an exception on a byte pump.
        Assert.Null(await client.MintReservationAsync("uid-1", "alice", "creative", null, Ct));
    }

    [Fact]
    public async Task ATtlBeyondWhatTheRegistryAllows_IsClampedByTheRegistry()
    {
        await using var registry = await Registry.StartAsync(cfg => cfg.MaxReservationTtlSeconds = 30);
        await registry.RegisterBackendAsync("creative");
        using var client = ClientFor(registry, configure: cfg => cfg.ReservationTtlSeconds = 100000);

        var reservation = await client.MintReservationAsync("uid-1", "alice", "creative", null, Ct);

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.NotNull(reservation);
        Assert.InRange(reservation!.ExpiresAtUnix, now, now + 31);
    }

    // ---- bans ----

    [Fact]
    public async Task ABan_RoundTripsThroughTheRegistryAndCanBeLifted()
    {
        await using var registry = await Registry.StartAsync();
        using var client = ClientFor(registry);

        var added = await client.AddBanAsync(new BanRequest
        {
            PlayerUid = "uid-1",
            PlayerName = "griefer",
            Reason = "griefing",
            BannedBy = "admin",
        }, Ct);

        Assert.NotNull(added);
        Assert.True(added!.IsNetworkWide);
        Assert.Equal(0, added.ExpiresAtUnix);

        var listed = await client.GetBansAsync(Ct);
        Assert.Equal("uid-1", Assert.Single(listed!).PlayerUid);

        Assert.True(await client.LiftBanAsync("uid-1", null, Ct));
        Assert.Empty((await client.GetBansAsync(Ct))!);
    }

    [Fact]
    public async Task ABanWithADuration_ComesBackStampedWithItsExpiry()
    {
        await using var registry = await Registry.StartAsync();
        using var client = ClientFor(registry);

        var added = await client.AddBanAsync(
            new BanRequest { PlayerUid = "uid-1", DurationSeconds = 3600 }, Ct);

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.InRange(added!.ExpiresAtUnix, now + 3590, now + 3610);
    }

    [Fact]
    public async Task AScopedBan_IsLiftedWithTheServerIdItWasMadeWith()
    {
        await using var registry = await Registry.StartAsync();
        using var client = ClientFor(registry);
        await client.AddBanAsync(new BanRequest { PlayerUid = "uid-1", ServerId = "creative" }, Ct);

        // The arguments travel in the signed body, so a scope that does not match finds nothing
        // rather than lifting the wrong entry.
        Assert.False(await client.LiftBanAsync("uid-1", null, Ct));
        Assert.False(await client.LiftBanAsync("uid-1", "hub", Ct));
        Assert.True(await client.LiftBanAsync("uid-1", "creative", Ct));
    }

    [Fact]
    public async Task LiftingABanNobodyHolds_ComesBackFalse()
    {
        await using var registry = await Registry.StartAsync();
        using var client = ClientFor(registry);

        Assert.False(await client.LiftBanAsync("uid-never-banned", null, Ct));
    }

    [Fact]
    public async Task ABanRequestWithNoUid_IsRefusedRatherThanStored()
    {
        await using var registry = await Registry.StartAsync();
        using var client = ClientFor(registry);

        Assert.Null(await client.AddBanAsync(new BanRequest { PlayerName = "griefer" }, Ct));
        Assert.Empty((await client.GetBansAsync(Ct))!);
    }

    // ---- whitelist ----

    [Fact]
    public async Task AWhitelistEntry_RoundTripsThroughTheRegistryAndCanBeRemoved()
    {
        await using var registry = await Registry.StartAsync();
        using var client = ClientFor(registry);

        var added = await client.AddWhitelistAsync(new WhitelistRequest
        {
            PlayerUid = "uid-1",
            PlayerName = "builder",
            Note = "trusted",
            AddedBy = "admin",
        }, Ct);

        Assert.NotNull(added);
        Assert.True(added!.IsNetworkWide);
        Assert.Equal("trusted", added.Note);

        Assert.Equal("uid-1", Assert.Single((await client.GetWhitelistAsync(Ct))!).PlayerUid);
        Assert.True(await client.RemoveWhitelistAsync("uid-1", null, Ct));
        Assert.Empty((await client.GetWhitelistAsync(Ct))!);
    }

    [Fact]
    public async Task AScopedWhitelistEntry_IsRemovedWithTheServerIdItWasMadeWith()
    {
        await using var registry = await Registry.StartAsync();
        using var client = ClientFor(registry);
        await client.AddWhitelistAsync(new WhitelistRequest { PlayerUid = "uid-1", ServerId = "staff" }, Ct);

        Assert.False(await client.RemoveWhitelistAsync("uid-1", null, Ct));
        Assert.True(await client.RemoveWhitelistAsync("uid-1", "staff", Ct));
    }

    [Fact]
    public async Task RemovingAWhitelistEntryNobodyHolds_ComesBackFalse()
    {
        await using var registry = await Registry.StartAsync();
        using var client = ClientFor(registry);

        Assert.False(await client.RemoveWhitelistAsync("uid-never-listed", null, Ct));
    }

    [Fact]
    public async Task AWhitelistRequestWithNoUid_IsRefusedRatherThanStored()
    {
        await using var registry = await Registry.StartAsync();
        using var client = ClientFor(registry);

        Assert.Null(await client.AddWhitelistAsync(new WhitelistRequest { PlayerName = "builder" }, Ct));
        Assert.Empty((await client.GetWhitelistAsync(Ct))!);
    }

    // ---- transfer intents ----

    [Fact]
    public async Task AQueuedIntent_IsDrainedOnceAndOnlyOnce()
    {
        await using var registry = await Registry.StartAsync();
        await registry.RegisterBackendAsync("creative");
        using var client = ClientFor(registry);
        await registry.QueueIntentAsync(new TransferIntentRequest
        {
            PlayerUid = "uid-1",
            PlayerName = "alice",
            TargetServerId = "creative",
            Mode = "seamless",
            Reason = "event start",
        });

        var first = await client.DrainTransferIntentsAsync(Ct);
        var second = await client.DrainTransferIntentsAsync(Ct);

        var intent = Assert.Single(first);
        Assert.Equal("uid-1", intent.PlayerUid);
        Assert.Equal("creative", intent.TargetServerId);
        Assert.Equal("seamless", intent.Mode);
        // Every proxy on the network polls this, and an intent handed to two of them moves the
        // player twice.
        Assert.Empty(second);
    }

    [Fact]
    public async Task ASeamlessFailure_IsReportedAndReturnedOnTheSourceHeartbeat()
    {
        await using var registry = await Registry.StartAsync();
        await registry.RegisterBackendAsync("source");
        using var client = ClientFor(registry);

        Assert.True(await client.ReportTransferFailureAsync(new TransferFailed
        {
            SourceServerId = "source",
            ClientTransferId = "transfer-51",
            Reason = "proxy timed out",
        }, Ct));

        var response = await registry.HeartbeatResponseAsync("source");
        var failure = Assert.Single(response.FailedTransfers);
        Assert.Equal("transfer-51", failure.ClientTransferId);
        Assert.Equal("proxy timed out", failure.Reason);

        Assert.Empty((await registry.HeartbeatResponseAsync("source")).FailedTransfers);
    }

    // ---- api tokens ----

    [Fact]
    public async Task ATokenIsMintedAndComesBackWithItsSecretExactlyOnce()
    {
        await using var registry = await Registry.StartAsync();
        using var client = ClientFor(registry);

        var created = await client.CreateApiTokenAsync(new ApiTokenCreateRequest
        {
            Name = "discord-bot",
            Scopes = new List<string> { "whitelist:write" },
            CreatedBy = "admin",
        }, Ct);

        Assert.NotNull(created);
        Assert.True(created.Ok);
        Assert.StartsWith("nsk_", created.Token);
        Assert.Equal("discord-bot", created.Record!.Name);

        // The listing that follows carries the record and nothing else, which is the whole point:
        // the secret existed in that one response and nowhere afterwards.
        var listed = await client.GetApiTokensAsync(Ct);
        var record = Assert.Single(listed!);
        Assert.Equal(created.Record.Id, record.Id);
        Assert.Equal("", record.Hash);
    }

    [Fact]
    public async Task ATokenTheRegistryRefuses_ComesBackAsANullRatherThanAThrow()
    {
        await using var registry = await Registry.StartAsync();
        using var client = ClientFor(registry);

        Assert.Null(await client.CreateApiTokenAsync(new ApiTokenCreateRequest
        { Name = "bot", Scopes = new List<string> { "bans:destroy" } }, Ct));
        Assert.Null(await client.CreateApiTokenAsync(new ApiTokenCreateRequest
        { Scopes = new List<string> { "bans:read" } }, Ct));
        Assert.Empty((await client.GetApiTokensAsync(Ct))!);
    }

    [Fact]
    public async Task RevokingATokenAnswersTrueOnceAndFalseAfterwards()
    {
        await using var registry = await Registry.StartAsync();
        using var client = ClientFor(registry);
        var created = await client.CreateApiTokenAsync(new ApiTokenCreateRequest
        { Name = "bot", Scopes = new List<string> { "bans:read" } }, Ct);

        Assert.True(await client.RevokeApiTokenAsync(created!.Record!.Id, Ct));
        Assert.False(await client.RevokeApiTokenAsync(created.Record.Id, Ct));
        Assert.False(await client.RevokeApiTokenAsync("no-such-id", Ct));

        // Still listed, and now marked: the record is the audit trail.
        Assert.True(Assert.Single((await client.GetApiTokensAsync(Ct))!).Revoked);
    }

    [Fact]
    public async Task AMintedTokenAuthenticatesAgainstTheSameRegistry()
    {
        await using var registry = await Registry.StartAsync(cfg => cfg.ApiTokens.Enabled = true);
        using var client = ClientFor(registry);
        var created = await client.CreateApiTokenAsync(new ApiTokenCreateRequest
        { Name = "discord-bot", Scopes = new List<string> { "whitelist:write", "whitelist:read" } }, Ct);

        // The two-line integration a bot author actually writes: one header, no signing.
        using var bot = new HttpClient();
        var add = new HttpRequestMessage(HttpMethod.Post, registry.BaseUrl.TrimEnd('/') + "/api/whitelist")
        {
            Content = JsonContent.Create(new WhitelistRequest { PlayerUid = "uid-1", Note = "invited" }),
        };
        add.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", created!.Token);
        var resp = await bot.SendAsync(add, Ct);

        Assert.True(resp.IsSuccessStatusCode);
        // And the proxy, reading the same list over HMAC, sees the token's name on the entry.
        var entries = await client.GetWhitelistAsync(Ct);
        Assert.Equal("token:discord-bot", Assert.Single(entries!).AddedBy);
    }

    // ---- what happens when the answer is not a 200 ----

    [Fact]
    public async Task AClientSigningWithTheWrongSecret_GetsNothingAndBreaksNothing()
    {
        await using var registry = await Registry.StartAsync();
        await registry.RegisterBackendAsync("creative");
        using var client = ClientFor(registry, secret: "the-wrong-secret");

        // Every one of these is called from a byte pump or a background loop with nowhere to put
        // an exception, so a 401 has to arrive as an ordinary "no".
        Assert.Null(await client.GetServersAsync(Ct));
        Assert.Null(await client.ResolveByServerIdAsync("creative", Ct));
        Assert.Null(await client.MintReservationAsync("uid-1", "alice", "creative", null, Ct));
        Assert.Null(await client.GetBansAsync(Ct));
        Assert.Null(await client.AddBanAsync(new BanRequest { PlayerUid = "uid-1" }, Ct));
        Assert.False(await client.LiftBanAsync("uid-1", null, Ct));
        Assert.Null(await client.GetWhitelistAsync(Ct));
        Assert.Null(await client.AddWhitelistAsync(new WhitelistRequest { PlayerUid = "uid-1" }, Ct));
        Assert.False(await client.RemoveWhitelistAsync("uid-1", null, Ct));
        Assert.Empty(await client.DrainTransferIntentsAsync(Ct));
        Assert.False(await client.ReportTransferFailureAsync(new TransferFailed
        {
            SourceServerId = "source",
            ClientTransferId = "transfer-1",
        }, Ct));
        Assert.Null(await client.CreateApiTokenAsync(new ApiTokenCreateRequest
        { Name = "bot", Scopes = new List<string> { "bans:read" } }, Ct));
        Assert.Null(await client.GetApiTokensAsync(Ct));
        Assert.False(await client.RevokeApiTokenAsync("a1b2c3", Ct));

        // And nothing it tried got through: the registry is exactly as it was.
        using var honest = ClientFor(registry);
        Assert.Empty((await honest.GetBansAsync(Ct))!);
        Assert.Empty((await honest.GetWhitelistAsync(Ct))!);
    }

    [Fact]
    public async Task AClientTalkingToNothingAtAll_GetsNothingAndBreaksNothing()
    {
        // Registry process down, wrong port in the config, network partition: the proxy keeps
        // serving players either way, on whatever its caches already hold.
        var cfg = new RegistryConfig
        {
            Url = "http://127.0.0.1:1",
            SharedSecret = Secret,
            HttpTimeoutSeconds = 2,
        };
        using var client = new HttpRegistryClient(cfg);

        Assert.Null(await client.GetServersAsync(Ct));
        Assert.Null(await client.MintReservationAsync("uid-1", "alice", "creative", null, Ct));
        Assert.Null(await client.GetBansAsync(Ct));
        Assert.Null(await client.AddBanAsync(new BanRequest { PlayerUid = "uid-1" }, Ct));
        Assert.False(await client.LiftBanAsync("uid-1", null, Ct));
        Assert.Null(await client.GetWhitelistAsync(Ct));
        Assert.Null(await client.AddWhitelistAsync(new WhitelistRequest { PlayerUid = "uid-1" }, Ct));
        Assert.False(await client.RemoveWhitelistAsync("uid-1", null, Ct));
        Assert.Empty(await client.DrainTransferIntentsAsync(Ct));
        Assert.False(await client.ReportTransferFailureAsync(new TransferFailed
        {
            SourceServerId = "source",
            ClientTransferId = "transfer-1",
        }, Ct));
        Assert.Null(await client.CreateApiTokenAsync(new ApiTokenCreateRequest
        { Name = "bot", Scopes = new List<string> { "bans:read" } }, Ct));
        Assert.Null(await client.GetApiTokensAsync(Ct));
        Assert.False(await client.RevokeApiTokenAsync("a1b2c3", Ct));
    }

    [Fact]
    public async Task AUrlWithATrailingSlash_ReachesTheSameEndpoints()
    {
        await using var registry = await Registry.StartAsync();
        using var client = ClientFor(registry, configure: cfg => cfg.Url = registry.BaseUrl.TrimEnd('/') + "/");

        // A doubled slash in the path would not match the route the signature was built for, so
        // this is a signing question as much as a routing one.
        Assert.NotNull(await client.GetBansAsync(Ct));
    }
}
