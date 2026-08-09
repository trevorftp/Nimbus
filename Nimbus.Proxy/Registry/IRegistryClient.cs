using Nimbus.Shared.Models;

namespace Nimbus.Proxy;

// Abstraction over the Nimbus registry. Two implementations:
//   * HttpRegistryClient: signed HTTP calls to a standalone Nimbus.Registry process.
//   * InProcRegistryClient: direct calls into the embedded registry services hosted inside
//     this proxy process. No HTTP round-trip, no HMAC.
//
// Callers depend on this interface so the same swap / dispatch / admin paths work in both
// embedded and remote registry modes.
internal interface IRegistryClient
{
    // Eight genuinely distinct values, left as explicit parameters (S107 suppressed). The player uid
    // and name, the target server, the reason, the cancellation token, the real remote ip and port,
    // and the client transfer id are not one concept: they are the separate facts the registry needs
    // to mint a reservation, each meaningful on its own at the call site. A "reservation request"
    // record would bundle them by name only, turning eight visible arguments into eight fields hidden
    // behind one, trading the readability the rule is meant to protect for a lower count. It stays a
    // flat signature on purpose.
    Task<TransferReservation?> MintReservationAsync( // NOSONAR
        string playerUid, string playerName, string targetServerId, string? reason, CancellationToken ct,
        string? realRemoteIp = null, int realRemotePort = 0, string? clientTransferId = null);

    Task<NetworkSnapshot?> GetServersAsync(CancellationToken ct, bool forceRefresh = false);

    Task<BackendSnapshot?> ResolveByServerIdAsync(string serverId, CancellationToken ct);

    Task<List<TransferIntent>> DrainTransferIntentsAsync(CancellationToken ct);

    // Network bans. The proxy keeps a local cache of these (BanCache) because the connection
    // gate cannot afford a registry round-trip per join; these calls are the refresh and the
    // write path behind the admin commands.
    Task<List<NetworkBan>?> GetBansAsync(CancellationToken ct);

    Task<NetworkBan?> AddBanAsync(BanRequest request, CancellationToken ct);

    Task<bool> LiftBanAsync(string playerUid, string? serverId, CancellationToken ct);

    // Network whitelist. Cached proxy-side (WhitelistCache) for the same reason bans are: the
    // connection gate runs on the byte pump and cannot wait on the registry.
    Task<List<WhitelistEntry>?> GetWhitelistAsync(CancellationToken ct);

    Task<WhitelistEntry?> AddWhitelistAsync(WhitelistRequest request, CancellationToken ct);

    Task<bool> RemoveWhitelistAsync(string playerUid, string? serverId, CancellationToken ct);

    // Scoped API tokens (#54). These are the management path only, reached from the admin socket:
    // the tokens themselves are presented to the registry by whoever holds them and never travel
    // back through here. Creating one is the single moment its plaintext exists, which is why the
    // create call answers with a response type rather than with the stored record.
    Task<ApiTokenCreateResponse?> CreateApiTokenAsync(ApiTokenCreateRequest request, CancellationToken ct);

    Task<List<ApiToken>?> GetApiTokensAsync(CancellationToken ct);

    Task<bool> RevokeApiTokenAsync(string id, CancellationToken ct);
}
