using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nimbus.Registry.Services;
using Nimbus.Shared;
using Nimbus.Shared.Models;

namespace Nimbus.Registry;

public static class Endpoints
{
    // The two refusals this file repeats, held once because they are wire contract rather than
    // prose: nine handlers answer an unparseable body with the first and four answer a missing
    // subject with the second, and a caller that matches on the text needs every one of them to
    // read the same.
    private const string MalformedBody = "malformed body";
    private const string PlayerUidRequired = "PlayerUid required";

    public static void Map(WebApplication app)
    {
        // Health (unauthenticated, outside /api).
        app.MapGet("/", () => Results.Text(
            $"Nimbus registry. protocol={NimbusProtocol.ProtocolVersion} version={NimbusProtocol.NimbusVersion}",
            "text/plain"));

        app.MapGet("/health", (TimeProvider clock) => Results.Ok(new { ok = true, ts = clock.GetUtcNow().ToUnixTimeSeconds() }));

        // Heartbeat.
        app.MapPost("/api/heartbeat", async (HttpContext ctx, BackendRegistry reg, RegistryConfig cfg, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("Heartbeat");
            BackendHeartbeat? hb;
            try { hb = await ctx.Request.ReadFromJsonAsync<BackendHeartbeat>(); }
            catch { return Results.BadRequest(new { error = MalformedBody }); }
            if (hb is null || string.IsNullOrEmpty(hb.ServerId))
                return Results.BadRequest(new { error = "missing ServerId" });

            reg.Upsert(hb);
            // Two gates rather than one: LogHeartbeats is the operator's opt-in, and the level
            // check is what stops four counters being boxed per heartbeat per backend when the
            // opt-in is on but the sink is running at Warning, which is where the registry sits
            // by default.
            if (cfg.LogHeartbeats && log.IsEnabled(LogLevel.Information))
                log.LogInformation("heartbeat {Id} players={P}/{M} tps={Tps:F1} maint={M2}",
                    hb.ServerId, hb.Players, hb.MaxPlayers, hb.Tps, hb.Maintenance);

            return Results.Ok(new BackendHeartbeatResponse { Ok = true, NextHeartbeatSeconds = 5 });
        });

        // Network snapshot.
        app.MapGet("/api/servers", (BackendRegistry reg) => Results.Ok(reg.Snapshot()));

        // Reservations. The mint rules live in ReservationService because the proxy's embedded
        // mode reaches them without passing through here; this handler is the HTTP dress on
        // them (parse, map the refusal to a status code).
        app.MapPost("/api/reservations", async (HttpContext ctx, ReservationService reservations, RegistryConfig cfg) =>
        {
            ReservationRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<ReservationRequest>(); }
            catch { return Results.BadRequest(new { error = MalformedBody }); }

            if (req is null)
                return Results.BadRequest(new { error = "PlayerUid + TargetServerId required" });

            var result = reservations.Mint(new ReservationMintRequest
            {
                PlayerUid = req.PlayerUid,
                PlayerName = req.PlayerName,
                SourceServerId = req.SourceServerId,
                TargetServerId = req.TargetServerId,
                TtlSeconds = req.TtlSeconds,
                MaxTtlSeconds = cfg.MaxReservationTtlSeconds,
                Reason = req.Reason,
                RealRemoteIp = req.RealRemoteIp,
                RealRemotePort = req.RealRemotePort,
                ClientTransferId = req.ClientTransferId,
            });

            return result.Status switch
            {
                ReservationMintStatus.MissingSubject
                    => Results.BadRequest(new { error = "PlayerUid + TargetServerId required" }),
                ReservationMintStatus.UnknownTarget
                    => Results.NotFound(new ReservationResponse { Ok = false, Error = "target server not registered" }),
                ReservationMintStatus.Banned
                    => Results.Json(new ReservationResponse { Ok = false, Error = "player is banned from the target server" },
                        statusCode: StatusCodes.Status403Forbidden),
                _ => Results.Ok(new ReservationResponse { Ok = true, Reservation = result.Reservation }),
            };
        });

        // Backend consumes a reservation during identification. Single-use.
        app.MapPost("/api/reservations/{id}/consume", (string id, HttpContext ctx, ReservationStore store) =>
        {
            string target = ctx.Request.Query["target"].ToString();
            if (string.IsNullOrEmpty(target))
                return Results.BadRequest(new { error = "?target=<serverId> required" });

            var r = store.Consume(id, target);
            if (r is null)
                return Results.NotFound(new ReservationResponse { Ok = false, Error = "reservation invalid, expired, or target mismatch" });
            return Results.Ok(new ReservationResponse { Ok = true, Reservation = r });
        });

        // Target backend consumes by (uid, target) at identification time. Vanilla clients
        // can't carry a reservation id through the redirect, so we look up by uid.
        app.MapPost("/api/reservations/consume-by-uid", (HttpContext ctx, ReservationStore store) =>
        {
            string uid = ctx.Request.Query["uid"].ToString();
            string target = ctx.Request.Query["target"].ToString();
            if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(target))
                return Results.BadRequest(new { error = "?uid=&target= required" });

            var r = store.ConsumeByUid(uid, target);
            if (r is null)
                return Results.NotFound(new ReservationResponse { Ok = false, Error = "no matching reservation" });
            return Results.Ok(new ReservationResponse { Ok = true, Reservation = r });
        });

        // Network bans. Held here so one ban covers every backend instead of being repeated
        // per savegame. Scoped bans (ServerId set) block a single backend.
        app.MapPost("/api/bans", async (HttpContext ctx, BanStore bans, TimeProvider clock) =>
        {
            BanRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<BanRequest>(); }
            catch { return Results.BadRequest(new { error = MalformedBody }); }

            Attribute(ctx, req);
            var stamped = RegistryStamps.NewBan(req, clock.GetUtcNow().ToUnixTimeSeconds());
            if (stamped is null)
                return Results.BadRequest(new BanResponse { Ok = false, Error = PlayerUidRequired });

            return Results.Ok(new BanResponse { Ok = true, Ban = bans.Add(stamped) });
        });

        // Lift a ban. Omit ServerId to lift the network-wide one; a scoped ban must be lifted
        // with the same serverId it was created with.
        app.MapPost("/api/bans/lift", async (HttpContext ctx, BanStore bans) =>
        {
            BanLiftRequest? req;
            try { req = await ReadOptionalBodyAsync<BanLiftRequest>(ctx); }
            catch { return Results.BadRequest(new { error = MalformedBody }); }

            // The signature covers the body and not the query, so a body naming a player settles
            // both arguments and the query is never consulted. Deprecated: ?uid=/?server= answer
            // only when the body names nobody, which keeps a proxy older than this endpoint
            // working. Drop the fallback when NimbusProtocol.ProtocolVersion moves past 1, since
            // that is the point at which a mismatched proxy is refused by HmacAuthMiddleware
            // anyway.
            bool fromBody = !string.IsNullOrEmpty(req?.PlayerUid);
            string uid = fromBody ? req!.PlayerUid : ctx.Request.Query["uid"].ToString();
            if (string.IsNullOrEmpty(uid))
                return Results.BadRequest(new { error = PlayerUidRequired });

            string serverId = fromBody ? req!.ServerId : ctx.Request.Query["server"].ToString();
            bool lifted = bans.Lift(uid, serverId);
            if (!lifted)
                return Results.NotFound(new BanResponse { Ok = false, Error = "no matching ban" });
            return Results.Ok(new BanResponse { Ok = true });
        });

        app.MapGet("/api/bans", (BanStore bans) => Results.Ok(new BanListResponse { Ok = true, Bans = bans.Active() }));

        // Network whitelist. Same storage shape as the bans above, read the other way round:
        // an entry says a player may come in. Whether that is required at all is a proxy-side
        // toggle, the [whitelist] section of nimbus.proxy.toml, so nothing here refuses anything.
        app.MapPost("/api/whitelist", async (HttpContext ctx, WhitelistStore whitelist, TimeProvider clock) =>
        {
            WhitelistRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<WhitelistRequest>(); }
            catch { return Results.BadRequest(new { error = MalformedBody }); }

            Attribute(ctx, req);
            // A uid entry is stored as before. A name-only entry is stored pending (#104): it
            // carries no uid to key on, so Add routes it to the name-keyed list, where the gate
            // matches it by the authenticated name and binds it on first join. Only a request that
            // names nobody at all is refused.
            var stamped = RegistryStamps.NewWhitelistEntry(req, clock.GetUtcNow().ToUnixTimeSeconds());
            if (stamped is null)
                return Results.BadRequest(new WhitelistResponse { Ok = false, Error = "PlayerUid or PlayerName required" });

            return Results.Ok(new WhitelistResponse { Ok = true, Entry = whitelist.Add(stamped) });
        });

        // Bind a pending entry to the uid it turned out to carry. The proxy posts this once its
        // gate has admitted a joining player on a pending name match: the entry is rewritten to key
        // on the uid, so the player's next join matches by uid with no pending path at all.
        // Idempotent by design (#104): binding a name that is already bound, or was never pending,
        // is a no-op that still answers ok, because the proxy fires this best-effort off the join
        // and must not be handed an error for a race it cannot do anything about.
        app.MapPost("/api/whitelist/bind", async (HttpContext ctx, WhitelistStore whitelist) =>
        {
            WhitelistBindRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<WhitelistBindRequest>(); }
            catch { return Results.BadRequest(new { error = MalformedBody }); }

            if (req is null || string.IsNullOrEmpty(req.PlayerName) || string.IsNullOrEmpty(req.PlayerUid))
                return Results.BadRequest(new WhitelistResponse { Ok = false, Error = "PlayerName and PlayerUid required" });

            whitelist.Bind(req.PlayerName, req.PlayerUid, req.ServerId ?? "");
            return Results.Ok(new WhitelistResponse { Ok = true });
        });

        // Drop an entry. Omit ServerId to drop the network-wide one; a scoped entry must be
        // removed with the same serverId it was created with. Same body-over-query rule as
        // /api/bans/lift, for the same reason.
        app.MapPost("/api/whitelist/remove", async (HttpContext ctx, WhitelistStore whitelist) =>
        {
            WhitelistRemoveRequest? req;
            try { req = await ReadOptionalBodyAsync<WhitelistRemoveRequest>(ctx); }
            catch { return Results.BadRequest(new { error = MalformedBody }); }

            bool fromBody = !string.IsNullOrEmpty(req?.PlayerUid);
            string uid = fromBody ? req!.PlayerUid : ctx.Request.Query["uid"].ToString();
            if (string.IsNullOrEmpty(uid))
                return Results.BadRequest(new { error = PlayerUidRequired });

            string serverId = fromBody ? req!.ServerId : ctx.Request.Query["server"].ToString();
            if (!whitelist.Remove(uid, serverId))
                return Results.NotFound(new WhitelistResponse { Ok = false, Error = "no matching entry" });
            return Results.Ok(new WhitelistResponse { Ok = true });
        });

        app.MapGet("/api/whitelist", (WhitelistStore whitelist)
            => Results.Ok(new WhitelistListResponse { Ok = true, Entries = whitelist.Active() }));

        // Backends post here when someone asks the proxy to move a player.
        // The proxy drains the queue and runs its normal swap path.
        app.MapPost("/api/transfer-intents", async (HttpContext ctx, TransferIntentStore store, BackendRegistry reg) =>
        {
            TransferIntentRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<TransferIntentRequest>(); }
            catch { return Results.BadRequest(new { error = MalformedBody }); }

            if (req is null || string.IsNullOrEmpty(req.PlayerUid) || string.IsNullOrEmpty(req.TargetServerId))
                return Results.BadRequest(new TransferIntentResponse { Ok = false, Error = "PlayerUid + TargetServerId required" });

            var target = reg.Get(req.TargetServerId);
            if (target is null)
                return Results.NotFound(new TransferIntentResponse { Ok = false, Error = "target server not registered" });

            var intent = store.Add(req);
            return Results.Ok(new TransferIntentResponse { Ok = true, Intent = intent });
        });

        // Proxy polls this. Destructive drain (each intent delivered at most once).
        app.MapPost("/api/transfer-intents/drain", (TransferIntentStore store) =>
        {
            var taken = store.Drain();
            return Results.Ok(new TransferIntentDrainResponse { Ok = true, Intents = taken });
        });

        // Token management. HMAC only, like every other internal endpoint: no scope reaches these
        // three, so a leaked bot token cannot mint itself a better one. ApiTokenScopes maps them
        // to no scope at all, which is what TokenAuthMiddleware turns into a 403.
        app.MapPost("/api/tokens", async (HttpContext ctx, ApiTokenService tokens) =>
        {
            ApiTokenCreateRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<ApiTokenCreateRequest>(); }
            catch { return Results.BadRequest(new { error = MalformedBody }); }

            var result = tokens.Create(req);
            return result.Status switch
            {
                ApiTokenCreateStatus.MissingName
                    => Results.BadRequest(new ApiTokenCreateResponse { Ok = false, Error = "name required" }),
                ApiTokenCreateStatus.InvalidName
                    => Results.BadRequest(new ApiTokenCreateResponse
                    {
                        Ok = false,
                        Error = $"name must be at most {ApiTokenService.MaxNameLength} characters and carry no control characters",
                    }),
                ApiTokenCreateStatus.NoScopes
                    => Results.BadRequest(new ApiTokenCreateResponse { Ok = false, Error = "at least one scope required" }),
                ApiTokenCreateStatus.UnknownScope
                    => Results.BadRequest(new ApiTokenCreateResponse
                    {
                        Ok = false,
                        Error = $"unknown scope '{result.UnknownScope}'; known scopes are {string.Join(", ", ApiTokenScopes.All)}",
                    }),
                // The only response that ever carries the plaintext. The record next to it is
                // redacted like every other listing: the caller gets the secret once and the
                // metadata for as long as the token exists.
                _ => Results.Ok(new ApiTokenCreateResponse
                {
                    Ok = true,
                    Token = result.Plaintext,
                    Record = result.Token!.Redacted(),
                }),
            };
        });

        app.MapPost("/api/tokens/revoke", async (HttpContext ctx, ApiTokenService tokens) =>
        {
            ApiTokenRevokeRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<ApiTokenRevokeRequest>(); }
            catch { return Results.BadRequest(new { error = MalformedBody }); }

            if (req is null || string.IsNullOrWhiteSpace(req.Id))
                return Results.BadRequest(new ApiTokenResponse { Ok = false, Error = "Id required" });

            if (!tokens.Revoke(req.Id))
                return Results.NotFound(new ApiTokenResponse { Ok = false, Error = "no matching token, or already revoked" });
            return Results.Ok(new ApiTokenResponse { Ok = true });
        });

        // Records only. The store holds a hash and never the secret, and even the hash is left
        // out of the answer.
        app.MapGet("/api/tokens", (ApiTokenService tokens)
            => Results.Ok(new ApiTokenListResponse { Ok = true, Tokens = tokens.List() }));
    }

    // A write made with a scoped token is attributed to that token, not to whoever the body says.
    // Overwriting rather than defaulting is the point: the credential names its holder, and a
    // caller must not be able to sign somebody else's name to a ban by filling in the field.
    // HMAC-authenticated callers are untouched and keep attributing themselves.
    private static void Attribute(HttpContext ctx, BanRequest? req)
    {
        var attribution = ApiTokenIdentity.Attribution(ctx);
        if (attribution is not null && req is not null) req.BannedBy = attribution;
    }

    private static void Attribute(HttpContext ctx, WhitelistRequest? req)
    {
        var attribution = ApiTokenIdentity.Attribution(ctx);
        if (attribution is not null && req is not null) req.AddedBy = attribution;
    }

    // Reads a body that a caller is allowed to leave out entirely. An absent body is not an
    // error here, unlike the endpoints that require one: it means the caller is old enough to
    // still be passing its arguments in the query.
    private static async Task<T?> ReadOptionalBodyAsync<T>(HttpContext ctx) where T : class
    {
        if (!DeclaresABody(ctx.Request)) return null;
        // RequestAborted, not None: finishing the read for a caller that has already hung up
        // buys nothing, and the handler above turns the cancellation into the same 400 a
        // truncated body gets.
        return await ctx.Request.ReadFromJsonAsync<T>(ctx.RequestAborted);
    }

    // A caller announces a body in one of two ways: it measures it (Content-Length) or it
    // chunks it (Transfer-Encoding). Reading only the first sent every chunked request down
    // the deprecated query path with an empty request object, so its arguments vanished and
    // the answer was usually a 400 (#91). Neither header means there is genuinely nothing to
    // read, which is still the old proxy asking to be answered from the query.
    private static bool DeclaresABody(HttpRequest request)
    {
        if (request.ContentLength is { } length) return length > 0;
        // Joined rather than walked: "gzip, chunked" and a repeated header read the same way,
        // and a header that is not there at all reads as an empty string rather than null.
        return request.Headers.TransferEncoding.ToString()
            .Contains("chunked", StringComparison.OrdinalIgnoreCase);
    }
}
