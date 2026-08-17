using Nimbus.Shared.Models;

namespace Nimbus.Proxy.Tests;

/// <summary>Scripted IRegistryClient for the router, status, cache, admin and dispatcher tests:
/// serves what it was handed, or throws. <see cref="Bans"/> null means "registry error" for the
/// ban list, and <see cref="Whitelist"/> null the same for the whitelist. The write calls answer
/// from the matching <c>...Result</c> field and record what they were asked for, so a test can
/// script a refusal as easily as a success and then read back the exact request a command
/// built.</summary>
internal sealed class FakeRegistryClient : IRegistryClient
{
    public NetworkSnapshot? Snapshot;
    public bool Throw;
    public List<NetworkBan>? Bans;
    public int BanFetches;
    public List<WhitelistEntry>? Whitelist;
    public int WhitelistFetches;

    /// <summary>What AddBanAsync answers. Null is "the registry refused the ban".</summary>
    public NetworkBan? AddBanResult;
    public BanRequest? LastBanRequest;

    /// <summary>What LiftBanAsync answers, and the pair it was last called with.</summary>
    public bool LiftBanResult;
    public (string Uid, string? ServerId)? LastLift;

    /// <summary>What AddWhitelistAsync answers. Null is "the registry refused the entry".</summary>
    public WhitelistEntry? AddWhitelistResult;
    public WhitelistRequest? LastWhitelistRequest;

    /// <summary>What RemoveWhitelistAsync answers, and the pair it was last called with.</summary>
    public bool RemoveWhitelistResult;
    public (string Uid, string? ServerId)? LastWhitelistRemove;

    /// <summary>Intents handed to the next drain, and how many drains have happened.</summary>
    public List<TransferIntent> Intents = new();
    public int Drains;

    public readonly List<TransferFailed> FailureReports = new();
    public bool FailFailureReport { get; set; }
    public bool ThrowFailureReport { get; set; }

    /// <summary>Backends ResolveByServerIdAsync knows about, keyed by serverId.</summary>
    public Dictionary<string, BackendSnapshot> Backends = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times a target has been resolved. Zero after an intent was handled tells
    /// a test the intent was dropped before the dispatcher went looking for a backend.</summary>
    public int Resolves;

    public Task<NetworkSnapshot?> GetServersAsync(CancellationToken ct, bool forceRefresh = false)
        => Throw
            ? throw new InvalidOperationException("registry down")
            : Task.FromResult(Snapshot);

    /// <summary>Every mint this client was asked for, oldest first. A transfer that reached the
    /// registry left a record here, which is how the dispatcher tests tell a session that was
    /// actually moved from one that was only looked at.</summary>
    public readonly List<MintCall> Mints = new();

    internal sealed record MintCall(string PlayerUid, string PlayerName, string TargetServerId,
        string? Reason, string? ClientTransferId);

    /// <summary>True makes every mint come back null, which is how a registry that is up but
    /// refusing (expired secret, unknown target) looks to the transfer paths.</summary>
    public bool FailMint;
    public bool ThrowMint;

    public Task<TransferReservation?> MintReservationAsync(string playerUid, string playerName,
        string targetServerId, string? reason, CancellationToken ct,
        string? realRemoteIp = null, int realRemotePort = 0, string? clientTransferId = null)
    {
        lock (Mints) Mints.Add(new MintCall(playerUid, playerName, targetServerId, reason, clientTransferId));
        if (ThrowMint) throw new InvalidOperationException("mint failed unexpectedly");
        if (FailMint) return Task.FromResult<TransferReservation?>(null);
        return Task.FromResult<TransferReservation?>(new TransferReservation
        {
            Id = "fake-reservation",
            PlayerUid = playerUid,
            PlayerName = playerName,
            TargetServerId = targetServerId,
            ExpiresAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60,
        });
    }

    /// <summary>Snapshot of <see cref="Mints"/>, safe to read while the dispatcher is running.</summary>
    public List<MintCall> MintsSoFar()
    {
        lock (Mints) return new List<MintCall>(Mints);
    }

    public Task<BackendSnapshot?> ResolveByServerIdAsync(string serverId, CancellationToken ct)
    {
        Interlocked.Increment(ref Resolves);
        return Task.FromResult(Backends.TryGetValue(serverId, out var b) ? b : null);
    }

    public Task<List<TransferIntent>> DrainTransferIntentsAsync(CancellationToken ct)
    {
        Drains++;
        if (Throw) throw new InvalidOperationException("registry down");
        var drained = Intents;
        Intents = new List<TransferIntent>();
        return Task.FromResult(drained);
    }

    public Task<bool> ReportTransferFailureAsync(TransferFailed failure, CancellationToken ct)
    {
        lock (FailureReports) FailureReports.Add(failure);
        if (ThrowFailureReport) throw new InvalidOperationException("failure report failed unexpectedly");
        return Task.FromResult(!FailFailureReport);
    }

    public List<TransferFailed> FailureReportsSoFar()
    {
        lock (FailureReports) return new List<TransferFailed>(FailureReports);
    }

    public Task<List<NetworkBan>?> GetBansAsync(CancellationToken ct)
    {
        BanFetches++;
        return Throw
            ? throw new InvalidOperationException("registry down")
            : Task.FromResult(Bans);
    }

    public Task<NetworkBan?> AddBanAsync(BanRequest request, CancellationToken ct)
    {
        LastBanRequest = request;
        return Task.FromResult(AddBanResult);
    }

    public Task<bool> LiftBanAsync(string playerUid, string? serverId, CancellationToken ct)
    {
        LastLift = (playerUid, serverId);
        return Task.FromResult(LiftBanResult);
    }

    public Task<List<WhitelistEntry>?> GetWhitelistAsync(CancellationToken ct)
    {
        WhitelistFetches++;
        return Throw
            ? throw new InvalidOperationException("registry down")
            : Task.FromResult(Whitelist);
    }

    public Task<WhitelistEntry?> AddWhitelistAsync(WhitelistRequest request, CancellationToken ct)
    {
        LastWhitelistRequest = request;
        return Task.FromResult(AddWhitelistResult);
    }

    public Task<bool> RemoveWhitelistAsync(string playerUid, string? serverId, CancellationToken ct)
    {
        LastWhitelistRemove = (playerUid, serverId);
        return Task.FromResult(RemoveWhitelistResult);
    }

    /// <summary>What CreateApiTokenAsync answers. Null is "the registry refused the token".</summary>
    public ApiTokenCreateResponse? CreateApiTokenResult;
    public ApiTokenCreateRequest? LastApiTokenRequest;

    /// <summary>Records handed to the next listing. Null is "registry error", as everywhere else
    /// on this double.</summary>
    public List<ApiToken>? ApiTokens;

    /// <summary>What RevokeApiTokenAsync answers, and the id it was last called with.</summary>
    public bool RevokeApiTokenResult;
    public string? LastApiTokenRevoke;

    public Task<ApiTokenCreateResponse?> CreateApiTokenAsync(ApiTokenCreateRequest request, CancellationToken ct)
    {
        LastApiTokenRequest = request;
        return Task.FromResult(CreateApiTokenResult);
    }

    public Task<List<ApiToken>?> GetApiTokensAsync(CancellationToken ct)
        => Throw
            ? throw new InvalidOperationException("registry down")
            : Task.FromResult(ApiTokens);

    public Task<bool> RevokeApiTokenAsync(string id, CancellationToken ct)
    {
        LastApiTokenRevoke = id;
        return Task.FromResult(RevokeApiTokenResult);
    }
}
