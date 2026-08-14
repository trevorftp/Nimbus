using Nimbus.Registry.Services;
using Nimbus.Shared.Models;

namespace Nimbus.Proxy;

// Direct in-process bridge to the embedded registry services. Bypasses HTTP + HMAC because
// both sides are the same OS process. The decisions it makes are not written here: reservation
// minting goes through the same ReservationService the HTTP endpoint uses and the moderation
// entries are shaped by the same RegistryStamps, so embedded and remote deployments cannot
// drift apart on a rule that was only added to one of them (#65).
internal sealed class InProcRegistryClient : IRegistryClient
{
    private readonly BackendRegistry backends;
    private readonly ReservationService reservations;
    private readonly TransferIntentStore intents;
    private readonly TransferFailureStore failures;
    private readonly BanStore bans;
    private readonly WhitelistStore whitelist;
    private readonly ApiTokenService tokens;
    private readonly RegistryConfig cfg;
    private readonly TimeProvider clock;

    public InProcRegistryClient(RegistryStores stores, RegistryConfig cfg, TimeProvider? clock = null)
    {
        this.backends = stores.Backends;
        this.intents = stores.Intents;
        this.failures = stores.Failures;
        this.bans = stores.Bans;
        this.whitelist = stores.Whitelist;
        this.cfg = cfg;
        this.clock = clock ?? TimeProvider.System;
        // Built here rather than resolved: the embedded registry can run with no HTTP listener
        // and therefore no container to resolve from. Both services hold no state of their own,
        // only references to the stores above, so these are the same rules over the same data and
        // not a second source of truth.
        this.reservations = new ReservationService(stores.Backends, stores.Reservations, stores.Bans, this.clock);
        this.tokens = new ApiTokenService(stores.Tokens, this.clock);
    }

    public Task<TransferReservation?> MintReservationAsync(
        string playerUid, string playerName, string targetServerId, string? reason, CancellationToken ct,
        string? realRemoteIp = null, int realRemotePort = 0, string? clientTransferId = null)
    {
        // The TTL is the one difference in the inputs: over HTTP the caller names it in the
        // request body, here it is proxy config, since the caller is the proxy itself.
        var result = reservations.Mint(new ReservationMintRequest
        {
            PlayerUid = playerUid,
            PlayerName = playerName ?? "",
            SourceServerId = cfg.ProxyId,
            TargetServerId = targetServerId,
            TtlSeconds = cfg.ReservationTtlSeconds,
            MaxTtlSeconds = cfg.MaxReservationTtlSeconds,
            Reason = reason,
            RealRemoteIp = realRemoteIp ?? "",
            RealRemotePort = realRemotePort,
            ClientTransferId = clientTransferId ?? "",
        });

        // A refusal is a null here where the endpoint would answer 400/404/403. There is no
        // caller to report to in-process, so the reason goes to the log instead.
        switch (result.Status)
        {
            case ReservationMintStatus.UnknownTarget:
                Log.Warn($"in-proc registry: unknown target server '{targetServerId}'");
                break;
            case ReservationMintStatus.Banned:
                Log.Warn($"in-proc registry: refusing reservation, uid={playerUid} is banned from '{targetServerId}'");
                break;
        }

        return Task.FromResult(result.Reservation);
    }

    public Task<NetworkSnapshot?> GetServersAsync(CancellationToken ct, bool forceRefresh = false)
        => Task.FromResult<NetworkSnapshot?>(backends.Snapshot());

    public Task<BackendSnapshot?> ResolveByServerIdAsync(string serverId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(serverId)) return Task.FromResult<BackendSnapshot?>(null);
        var snap = backends.Snapshot();
        return Task.FromResult<BackendSnapshot?>(
            snap.Backends.FirstOrDefault(b => string.Equals(b.ServerId, serverId, StringComparison.OrdinalIgnoreCase)));
    }

    public Task<List<TransferIntent>> DrainTransferIntentsAsync(CancellationToken ct)
        => Task.FromResult(intents.Drain());

    public Task<bool> ReportTransferFailureAsync(TransferFailed failure, CancellationToken ct)
        => Task.FromResult(failures.Add(failure));

    public Task<List<NetworkBan>?> GetBansAsync(CancellationToken ct)
        => Task.FromResult<List<NetworkBan>?>(bans.Active());

    // Same stamping as POST /api/bans, from the same helper, so embedded and remote modes
    // cannot end up with differently shaped bans.
    public Task<NetworkBan?> AddBanAsync(BanRequest request, CancellationToken ct)
    {
        var ban = RegistryStamps.NewBan(request, clock.GetUtcNow().ToUnixTimeSeconds());
        return Task.FromResult(ban is null ? null : bans.Add(ban));
    }

    public Task<bool> LiftBanAsync(string playerUid, string? serverId, CancellationToken ct)
        => Task.FromResult(bans.Lift(playerUid, serverId));

    public Task<List<WhitelistEntry>?> GetWhitelistAsync(CancellationToken ct)
        => Task.FromResult<List<WhitelistEntry>?>(whitelist.Active());

    // Same stamping as POST /api/whitelist, from the same helper.
    public Task<WhitelistEntry?> AddWhitelistAsync(WhitelistRequest request, CancellationToken ct)
    {
        var entry = RegistryStamps.NewWhitelistEntry(request, clock.GetUtcNow().ToUnixTimeSeconds());
        return Task.FromResult(entry is null ? null : whitelist.Add(entry));
    }

    // Lifts and removals are the store method and nothing else: there was never a second copy
    // of them to extract, the endpoints delegate the same way.
    public Task<bool> RemoveWhitelistAsync(string playerUid, string? serverId, CancellationToken ct)
        => Task.FromResult(whitelist.Remove(playerUid, serverId));

    // Same ApiTokenService the HTTP endpoints call, so the default-90-days rule, the scope
    // vocabulary and the refusal reasons are the same object in embedded and remote mode. A
    // refusal is a null here where the endpoint answers 400, with the reason in the log: there is
    // no HTTP caller to hand a status code to.
    public Task<ApiTokenCreateResponse?> CreateApiTokenAsync(ApiTokenCreateRequest request, CancellationToken ct)
    {
        var result = tokens.Create(request);
        if (result.Status != ApiTokenCreateStatus.Ok)
        {
            Log.Warn($"in-proc registry: refusing to mint an api token, {Explain(result)}");
            return Task.FromResult<ApiTokenCreateResponse?>(null);
        }

        return Task.FromResult<ApiTokenCreateResponse?>(new ApiTokenCreateResponse
        {
            Ok = true,
            Token = result.Plaintext,
            Record = result.Token!.Redacted(),
        });
    }

    private static string Explain(ApiTokenCreateResult result) => result.Status switch
    {
        ApiTokenCreateStatus.MissingName => "no name given",
        ApiTokenCreateStatus.InvalidName => "the name is too long or carries control characters",
        ApiTokenCreateStatus.NoScopes => "no scopes given",
        _ => $"unknown scope '{result.UnknownScope}'",
    };

    public Task<List<ApiToken>?> GetApiTokensAsync(CancellationToken ct)
        => Task.FromResult<List<ApiToken>?>(tokens.List());

    public Task<bool> RevokeApiTokenAsync(string id, CancellationToken ct)
        => Task.FromResult(tokens.Revoke(id));
}
