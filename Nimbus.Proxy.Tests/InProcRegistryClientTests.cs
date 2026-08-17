using Nimbus.Registry.Services;
using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// The embedded-mode registry client, driven against the real stores it wraps rather than
/// against mocks of them. Embedded mode is the default in nimbus.proxy.toml, so this is the
/// path most deployments actually run, and it is the one with no HTTP layer in front of it to
/// catch a malformed request.
///
/// What matters here is that it answers the same way the remote client does. Every refusal in
/// Endpoints.cs has a twin in this class, and a twin that drifted would mean a player banned
/// from a backend can still be transferred onto it as long as the operator runs embedded. So
/// the assertions are about the decisions, not about the plumbing: who gets a reservation, who
/// does not, and what the stores hold afterwards.
/// </summary>
public class InProcRegistryClientTests
{
    private const string Uid = "uid-1";

    /// <summary>Deterministic clock so the stamped expiries can be asserted exactly rather than
    /// within a window that a slow machine can fall outside of.</summary>
    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
        public long NowUnix => now.ToUnixTimeSeconds();
    }

    /// <summary>The client plus the five stores behind it, all real. Nothing here is a double:
    /// a test that adds a ban and then mints against it is walking the same objects the proxy
    /// walks in embedded mode.</summary>
    private sealed class Embedded
    {
        public required InProcRegistryClient Client { get; init; }
        public required BackendRegistry Backends { get; init; }
        public required ReservationStore Reservations { get; init; }
        public required TransferIntentStore Intents { get; init; }
        public required TransferFailureStore Failures { get; init; }
        public required BanStore Bans { get; init; }
        public required WhitelistStore Whitelist { get; init; }
        public required ApiTokenStore Tokens { get; init; }
        public required FixedClock Clock { get; init; }

        public static Embedded Create(Action<RegistryConfig>? configure = null)
        {
            var clock = new FixedClock();
            var proxyCfg = new RegistryConfig { ProxyId = "embedded-proxy" };
            configure?.Invoke(proxyCfg);
            var registryCfg = new Nimbus.Registry.RegistryConfig();
            var backends = new BackendRegistry(registryCfg, clock);
            var reservations = new ReservationStore(clock);
            var intents = new TransferIntentStore(clock);
            var failures = new TransferFailureStore(clock);
            var bans = new BanStore(clock);
            var whitelist = new WhitelistStore(clock);
            var tokens = new ApiTokenStore(clock);
            return new Embedded
            {
                Client = new InProcRegistryClient(new RegistryStores
                {
                    Backends = backends,
                    Reservations = reservations,
                    Intents = intents,
                    Failures = failures,
                    Bans = bans,
                    Whitelist = whitelist,
                    Tokens = tokens,
                }, proxyCfg, clock),
                Backends = backends,
                Reservations = reservations,
                Intents = intents,
                Failures = failures,
                Bans = bans,
                Whitelist = whitelist,
                Tokens = tokens,
                Clock = clock,
            };
        }

        public void Heartbeat(string serverId, string host = "10.0.0.1", int port = 42420, int players = 0)
            => Backends.Upsert(new BackendHeartbeat
            {
                ServerId = serverId,
                DisplayName = serverId,
                PublicHost = host,
                PublicPort = port,
                Players = players,
                MaxPlayers = 32,
            });
    }

    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task AFailureNotice_IsStoredByTheEmbeddedClient()
    {
        var e = Embedded.Create();

        Assert.True(await e.Client.ReportTransferFailureAsync(new TransferFailed
        {
            SourceServerId = "backend-1",
            ClientTransferId = "transfer-51",
            Reason = "target unavailable",
        }, Ct));

        var failure = Assert.Single(e.Failures.DrainForSource("backend-1"));
        Assert.Equal("transfer-51", failure.ClientTransferId);
        Assert.Equal("target unavailable", failure.Reason);
    }

    // ---- minting: who gets a reservation ----

    [Fact]
    public async Task AMint_LandsInTheStoreTheBackendWillConsumeItFrom()
    {
        var e = Embedded.Create();
        e.Heartbeat("creative");

        var r = await e.Client.MintReservationAsync(Uid, "alice", "creative", "admin swap", Ct,
            realRemoteIp: "203.0.113.7", realRemotePort: 51234, clientTransferId: "transfer-9");

        Assert.NotNull(r);
        Assert.Equal("embedded-proxy", r!.SourceServerId);
        Assert.Equal("creative", r.TargetServerId);
        Assert.Equal("alice", r.PlayerName);
        Assert.Equal("admin swap", r.Reason);
        Assert.Equal("203.0.113.7", r.RealRemoteIp);
        Assert.Equal(51234, r.RealRemotePort);
        Assert.Equal("transfer-9", r.ClientTransferId);

        // The mint is worth nothing unless the backend can find it: this is the same lookup
        // /api/reservations/consume performs when the player lands.
        var consumed = e.Reservations.Consume(r.Id, "creative");
        Assert.NotNull(consumed);
        Assert.Equal(Uid, consumed!.PlayerUid);
        // And only once, or a reservation is a reusable ticket onto a backend.
        Assert.Null(e.Reservations.Consume(r.Id, "creative"));
    }

    [Fact]
    public async Task AMintForABackendNobodyHasHeardFrom_IsRefusedAndStoresNothing()
    {
        var e = Embedded.Create();
        e.Heartbeat("creative");

        Assert.Null(await e.Client.MintReservationAsync(Uid, "alice", "nowhere", null, Ct));

        // Storing it anyway would leave a reservation no backend can ever consume, which the
        // sweeper would carry until it expired.
        Assert.Null(e.Reservations.ConsumeByUid(Uid, "nowhere"));
    }

    [Theory]
    [InlineData("", "creative")]
    [InlineData(Uid, "")]
    public async Task AMintMissingHalfOfItsSubject_IsRefusedBeforeAnythingIsLookedUp(
        string uid, string target)
    {
        var e = Embedded.Create();
        e.Heartbeat("creative");

        Assert.Null(await e.Client.MintReservationAsync(uid, "alice", target, null, Ct));
    }

    // ---- minting: the ban backstop ----

    [Fact]
    public async Task AMintForAPlayerBannedFromThatBackend_IsRefused()
    {
        var e = Embedded.Create();
        e.Heartbeat("creative");
        e.Bans.Add(new NetworkBan { PlayerUid = Uid, ServerId = "creative", Reason = "griefing" });

        // Embedded mode has no HTTP endpoint in front of it, so this check is the only thing
        // standing between a banned player and a transfer onto the backend that banned them.
        Assert.Null(await e.Client.MintReservationAsync(Uid, "alice", "creative", null, Ct));
        Assert.Null(e.Reservations.ConsumeByUid(Uid, "creative"));
    }

    [Fact]
    public async Task AMintForAPlayerBannedNetworkWide_IsRefusedForEveryBackend()
    {
        var e = Embedded.Create();
        e.Heartbeat("hub");
        e.Heartbeat("creative", port: 42421);
        e.Bans.Add(new NetworkBan { PlayerUid = Uid, ServerId = "" });

        Assert.Null(await e.Client.MintReservationAsync(Uid, "alice", "hub", null, Ct));
        Assert.Null(await e.Client.MintReservationAsync(Uid, "alice", "creative", null, Ct));
    }

    [Fact]
    public async Task AMintForAPlayerBannedFromSomewhereElse_GoesThrough()
    {
        var e = Embedded.Create();
        e.Heartbeat("hub");
        e.Heartbeat("creative", port: 42421);
        e.Bans.Add(new NetworkBan { PlayerUid = Uid, ServerId = "creative" });

        // A scoped ban is a scoped ban. Refusing every backend because one of them banned the
        // player would quietly turn every per-server ban into a network ban.
        Assert.NotNull(await e.Client.MintReservationAsync(Uid, "alice", "hub", null, Ct));
        Assert.Null(await e.Client.MintReservationAsync(Uid, "alice", "creative", null, Ct));
    }

    [Fact]
    public async Task AMintForAPlayerWhoseBanHasRunOut_GoesThrough()
    {
        var e = Embedded.Create();
        e.Heartbeat("creative");
        e.Bans.Add(new NetworkBan
        {
            PlayerUid = Uid,
            ServerId = "creative",
            ExpiresAtUnix = e.Clock.NowUnix + 60,
        });

        Assert.Null(await e.Client.MintReservationAsync(Uid, "alice", "creative", null, Ct));

        // Timed bans lapse on read rather than waiting for the sweep, so the player is free the
        // second their hour is up and not whenever the background loop next runs.
        e.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.NotNull(await e.Client.MintReservationAsync(Uid, "alice", "creative", null, Ct));
    }

    [Fact]
    public async Task AWhitelistedPlayerIsNotAPrerequisiteForAMint()
    {
        var e = Embedded.Create();
        e.Heartbeat("creative");

        // Whether coverage is required at all lives in the proxy's [whitelist] section, which
        // the registry never sees. Refusing unlisted players here would enforce a whitelist on
        // operators who never turned one on.
        Assert.Empty(e.Whitelist.Active());
        Assert.NotNull(await e.Client.MintReservationAsync(Uid, "alice", "creative", null, Ct));
    }

    // ---- minting: the TTL ----

    [Fact]
    public async Task ATtlWithinTheCeiling_IsUsedAsConfigured()
    {
        var e = Embedded.Create(cfg =>
        {
            cfg.ReservationTtlSeconds = 45;
            cfg.MaxReservationTtlSeconds = 300;
        });
        e.Heartbeat("creative");

        var r = await e.Client.MintReservationAsync(Uid, "alice", "creative", null, Ct);

        Assert.Equal(e.Clock.NowUnix + 45, r!.ExpiresAtUnix);
    }

    [Fact]
    public async Task ATtlAboveTheCeiling_IsClampedToTheCeiling()
    {
        var e = Embedded.Create(cfg =>
        {
            cfg.ReservationTtlSeconds = 100000;
            cfg.MaxReservationTtlSeconds = 300;
        });
        e.Heartbeat("creative");

        var r = await e.Client.MintReservationAsync(Uid, "alice", "creative", null, Ct);

        // A reservation is a standing invitation onto a backend, so the ceiling is what stops a
        // fat-fingered config from handing out day-long ones.
        Assert.Equal(e.Clock.NowUnix + 300, r!.ExpiresAtUnix);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public async Task ATtlThatIsNotAPositiveNumberOfSeconds_FallsBackToTheBuiltInMinute(int configured)
    {
        var e = Embedded.Create(cfg =>
        {
            cfg.ReservationTtlSeconds = configured;
            cfg.MaxReservationTtlSeconds = 300;
        });
        e.Heartbeat("creative");

        var r = await e.Client.MintReservationAsync(Uid, "alice", "creative", null, Ct);

        // An unset or negative TTL must not mean "expires immediately", which would break every
        // transfer on the network rather than just looking wrong in the config.
        Assert.Equal(e.Clock.NowUnix + 60, r!.ExpiresAtUnix);
    }

    [Fact]
    public async Task TwoMintsForTheSamePlayer_GetDistinctIds()
    {
        var e = Embedded.Create();
        e.Heartbeat("creative");

        var first = await e.Client.MintReservationAsync(Uid, "alice", "creative", null, Ct);
        var second = await e.Client.MintReservationAsync(Uid, "alice", "creative", null, Ct);

        // Ids key the store, so a collision would have the second mint overwrite the first and
        // leave one of the two players holding a ticket that was already consumed.
        Assert.NotEqual(first!.Id, second!.Id);
        Assert.NotNull(e.Reservations.Peek(first.Id));
        Assert.NotNull(e.Reservations.Peek(second.Id));
    }

    // ---- the routing snapshot ----

    [Fact]
    public async Task TheSnapshot_IsTheLiveOneAndNotACachedCopy()
    {
        var e = Embedded.Create();
        e.Heartbeat("hub", players: 4);

        Assert.Single((await e.Client.GetServersAsync(Ct))!.Backends);
        e.Heartbeat("creative", port: 42421, players: 3);

        // In-process means there is nothing to cache: a backend that heartbeats mid-second is
        // routable on the next call, with no forced refresh needed.
        var snapshot = await e.Client.GetServersAsync(Ct);
        Assert.Equal(2, snapshot!.Backends.Count);
        Assert.Equal(7, snapshot.TotalPlayers);
    }

    [Fact]
    public async Task ResolvingByServerId_MatchesWhateverCaseTheOperatorTyped()
    {
        var e = Embedded.Create();
        e.Heartbeat("Creative", port: 42421);

        Assert.Equal(42421, (await e.Client.ResolveByServerIdAsync("creative", Ct))!.PublicPort);
        Assert.Equal(42421, (await e.Client.ResolveByServerIdAsync("CREATIVE", Ct))!.PublicPort);
        Assert.Null(await e.Client.ResolveByServerIdAsync("nowhere", Ct));
        Assert.Null(await e.Client.ResolveByServerIdAsync("", Ct));
    }

    // ---- bans ----

    [Fact]
    public async Task ABan_IsStampedWithTheClockAndVisibleToTheGateImmediately()
    {
        var e = Embedded.Create();

        var ban = await e.Client.AddBanAsync(new BanRequest
        {
            PlayerUid = Uid,
            PlayerName = "griefer",
            Reason = "griefing",
            BannedBy = "admin",
        }, Ct);

        Assert.NotNull(ban);
        Assert.Equal(e.Clock.NowUnix, ban!.CreatedAtUnix);
        // Zero, not "now": a ban with no duration is permanent, and stamping it with an expiry
        // of now would lift it on the next read.
        Assert.Equal(0, ban.ExpiresAtUnix);
        Assert.True(ban.IsNetworkWide);
        Assert.NotNull(e.Bans.FindBlocking(Uid));
    }

    [Fact]
    public async Task ATimedBan_ExpiresTheConfiguredNumberOfSecondsAfterItWasMade()
    {
        var e = Embedded.Create();

        var ban = await e.Client.AddBanAsync(
            new BanRequest { PlayerUid = Uid, DurationSeconds = 3600 }, Ct);

        Assert.Equal(e.Clock.NowUnix + 3600, ban!.ExpiresAtUnix);
        Assert.Single((await e.Client.GetBansAsync(Ct))!);

        e.Clock.Advance(TimeSpan.FromSeconds(3601));
        Assert.Empty((await e.Client.GetBansAsync(Ct))!);
    }

    [Fact]
    public async Task ARebanOfTheSameScope_ReplacesTheEntryRatherThanStackingOne()
    {
        var e = Embedded.Create();
        await e.Client.AddBanAsync(new BanRequest { PlayerUid = Uid, Reason = "first" }, Ct);

        await e.Client.AddBanAsync(new BanRequest { PlayerUid = Uid, Reason = "second" }, Ct);

        // Otherwise one unban would leave the player banned by the entry underneath it, and the
        // operator would have to guess how many times to run it.
        var listed = Assert.Single((await e.Client.GetBansAsync(Ct))!);
        Assert.Equal("second", listed.Reason);
        Assert.True(await e.Client.LiftBanAsync(Uid, null, Ct));
        Assert.Empty((await e.Client.GetBansAsync(Ct))!);
    }

    [Fact]
    public async Task ABanIsLiftedWithTheScopeItWasMadeWith()
    {
        var e = Embedded.Create();
        await e.Client.AddBanAsync(new BanRequest { PlayerUid = Uid, ServerId = "creative" }, Ct);

        Assert.False(await e.Client.LiftBanAsync(Uid, null, Ct));
        Assert.False(await e.Client.LiftBanAsync(Uid, "hub", Ct));
        Assert.True(await e.Client.LiftBanAsync(Uid, "creative", Ct));
        Assert.Empty((await e.Client.GetBansAsync(Ct))!);
    }

    [Fact]
    public async Task ABanRequestWithNoUid_IsRefusedRatherThanStoredAgainstNobody()
    {
        var e = Embedded.Create();

        Assert.Null(await e.Client.AddBanAsync(new BanRequest { PlayerName = "griefer" }, Ct));
        Assert.Null(await e.Client.AddBanAsync(null!, Ct));
        // An entry keyed on the empty uid blocks nobody and cannot be lifted by name.
        Assert.Empty((await e.Client.GetBansAsync(Ct))!);
    }

    // ---- whitelist ----

    [Fact]
    public async Task AWhitelistEntry_IsStampedTheSameWayABanIs()
    {
        var e = Embedded.Create();

        var entry = await e.Client.AddWhitelistAsync(new WhitelistRequest
        {
            PlayerUid = Uid,
            PlayerName = "builder",
            Note = "trusted",
            AddedBy = "admin",
        }, Ct);

        Assert.NotNull(entry);
        Assert.Equal("trusted", entry!.Note);
        Assert.Equal("admin", entry.AddedBy);
        Assert.Equal(e.Clock.NowUnix, entry.CreatedAtUnix);
        Assert.Equal(0, entry.ExpiresAtUnix);
        Assert.True(entry.IsNetworkWide);
        Assert.NotNull(e.Whitelist.FindCovering(Uid));
    }

    [Fact]
    public async Task ATimedWhitelistEntry_StopsCoveringWhenItRunsOut()
    {
        var e = Embedded.Create();

        var entry = await e.Client.AddWhitelistAsync(
            new WhitelistRequest { PlayerUid = Uid, DurationSeconds = 86400 }, Ct);

        Assert.Equal(e.Clock.NowUnix + 86400, entry!.ExpiresAtUnix);
        Assert.NotNull(e.Whitelist.FindCovering(Uid));

        // A day pass has to actually end, or "let them in for today" is a permanent decision.
        e.Clock.Advance(TimeSpan.FromSeconds(86401));
        Assert.Null(e.Whitelist.FindCovering(Uid));
        Assert.Empty((await e.Client.GetWhitelistAsync(Ct))!);
    }

    [Fact]
    public async Task AScopedWhitelistEntry_CoversOnlyItsOwnBackend()
    {
        var e = Embedded.Create();
        await e.Client.AddWhitelistAsync(new WhitelistRequest { PlayerUid = Uid, ServerId = "staff" }, Ct);

        Assert.NotNull(e.Whitelist.FindCovering(Uid, "staff"));
        Assert.Null(e.Whitelist.FindCovering(Uid, "hub"));
        // Empty scope asks about network-wide coverage only, which a staff-only entry is not.
        Assert.Null(e.Whitelist.FindCovering(Uid, ""));
    }

    [Fact]
    public async Task AWhitelistEntryIsRemovedWithTheScopeItWasMadeWith()
    {
        var e = Embedded.Create();
        await e.Client.AddWhitelistAsync(new WhitelistRequest { PlayerUid = Uid }, Ct);
        await e.Client.AddWhitelistAsync(new WhitelistRequest { PlayerUid = Uid, ServerId = "staff" }, Ct);

        // Two scopes, two entries: removing one leaves the other standing, which is what lets an
        // operator take somebody off the staff server without ejecting them from the network.
        Assert.True(await e.Client.RemoveWhitelistAsync(Uid, "staff", Ct));
        Assert.Single((await e.Client.GetWhitelistAsync(Ct))!);
        Assert.NotNull(e.Whitelist.FindCovering(Uid));

        Assert.True(await e.Client.RemoveWhitelistAsync(Uid, null, Ct));
        Assert.Empty((await e.Client.GetWhitelistAsync(Ct))!);
        Assert.False(await e.Client.RemoveWhitelistAsync(Uid, null, Ct));
    }

    [Fact]
    public async Task AWhitelistRequestWithNoUid_IsRefusedRatherThanStoredAgainstNobody()
    {
        var e = Embedded.Create();

        Assert.Null(await e.Client.AddWhitelistAsync(new WhitelistRequest { PlayerName = "builder" }, Ct));
        Assert.Null(await e.Client.AddWhitelistAsync(null!, Ct));
        Assert.Empty((await e.Client.GetWhitelistAsync(Ct))!);
    }

    // ---- transfer intents ----

    [Fact]
    public async Task AQueuedIntent_IsDrainedOnceAndOnlyOnce()
    {
        var e = Embedded.Create();
        e.Intents.Add(new TransferIntentRequest
        {
            PlayerUid = Uid,
            PlayerName = "alice",
            TargetServerId = "creative",
            Mode = "seamless",
            Reason = "event start",
        });

        var intent = Assert.Single(await e.Client.DrainTransferIntentsAsync(Ct));
        Assert.Equal(Uid, intent.PlayerUid);
        Assert.Equal("creative", intent.TargetServerId);
        Assert.Equal("seamless", intent.Mode);
        // Draining is destructive on purpose: the dispatcher polls this in a loop, and an intent
        // that survived its own drain would move the player again on the next tick.
        Assert.Empty(await e.Client.DrainTransferIntentsAsync(Ct));
    }

    // ---- api tokens ----

    [Fact]
    public async Task AMintedToken_LandsInTheStoreTheMiddlewareWillReadItFrom()
    {
        var e = Embedded.Create();

        var created = await e.Client.CreateApiTokenAsync(new ApiTokenCreateRequest
        {
            Name = "discord-bot",
            Scopes = new List<string> { ApiTokenScopes.WhitelistWrite },
            CreatedBy = "admin",
        }, Ct);

        Assert.NotNull(created);
        Assert.StartsWith(ApiTokenSecret.Prefix, created.Token);
        // Found by the hash, which is the only lookup the auth path does.
        var held = e.Tokens.FindByHash(ApiTokenSecret.Hash(created.Token));
        Assert.NotNull(held);
        Assert.Equal("discord-bot", held.Name);
        Assert.Equal(e.Clock.NowUnix + ApiTokenService.DefaultDurationSeconds, held.ExpiresAtUnix);
        // The record handed back is redacted, the stored one is not.
        Assert.Equal("", created.Record!.Hash);
        Assert.NotEqual("", held.Hash);
    }

    [Theory]
    [InlineData("", "whitelist:write")]
    [InlineData("bot\nforged", "whitelist:write")]
    [InlineData("bot", "")]
    [InlineData("bot", "bans:destroy")]
    public async Task ATokenTheRulesRefuse_IsANullHereAndA400OverHttp(string name, string scope)
    {
        var e = Embedded.Create();

        var created = await e.Client.CreateApiTokenAsync(new ApiTokenCreateRequest
        {
            Name = name,
            Scopes = scope.Length == 0 ? new List<string>() : new List<string> { scope },
        }, Ct);

        // Same rules as POST /api/tokens, from the same service, so embedded and remote modes
        // cannot end up issuing differently shaped credentials.
        Assert.Null(created);
        Assert.Equal(0, e.Tokens.Count);
    }

    [Fact]
    public async Task ARevokedToken_StaysListedAndStopsBeingUsable()
    {
        var e = Embedded.Create();
        var created = await e.Client.CreateApiTokenAsync(new ApiTokenCreateRequest
        { Name = "bot", Scopes = new List<string> { ApiTokenScopes.BansRead } }, Ct);

        Assert.True(await e.Client.RevokeApiTokenAsync(created!.Record!.Id, Ct));
        Assert.False(await e.Client.RevokeApiTokenAsync(created.Record.Id, Ct));

        var listed = Assert.Single((await e.Client.GetApiTokensAsync(Ct))!);
        Assert.True(listed.Revoked);
        Assert.False(listed.IsUsableAt(e.Clock.NowUnix));
        Assert.Equal("", listed.Hash);
    }
}
