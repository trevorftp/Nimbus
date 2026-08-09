using Nimbus.Shared.Models;

namespace Nimbus.Proxy;

// Proxy-side snapshot of the registry's whitelist, the twin of BanCache.
//
// Same reason for existing: the connection gate runs while parsing Identification, on the byte
// pump, so the lookup has to be synchronous. A background refresh keeps this list warm, and an
// entry added through the admin socket is applied immediately so it takes effect on the next
// join rather than after the next poll.
//
// A registry outage leaves the last known list in place. That is safe for bans and dangerous
// here: with enforcement on and nothing ever fetched, an empty list means nobody gets in at
// all. HasSynced exists for exactly that case, so the gate can tell "the list really is empty"
// apart from "we have never managed to read it".
internal sealed class WhitelistCache
{
    private readonly RegistryEntryCache<WhitelistEntry> cache;

    public WhitelistCache(IRegistryClient? registry, CancellationToken stopToken,
        TimeSpan? refreshPeriod = null, TimeProvider? clock = null)
        => cache = new RegistryEntryCache<WhitelistEntry>(registry,
            static (r, ct) => r.GetWhitelistAsync(ct), "whitelist", stopToken, refreshPeriod, clock);

    public int Count => cache.Count;

    // True once the registry has answered at least once since boot. False means the list below
    // is a guess, not an answer: nobody has ever been listed as far as this proxy knows.
    public bool HasSynced => cache.HasSynced;

    // The entry covering this player on `serverId`, or null. A network-wide entry matches
    // whatever is asked; a scoped one only its own backend. Pass no serverId to ask about the
    // network alone, which is all a backend configured as host:port can be asked about.
    public WhitelistEntry? FindCovering(string? playerUid, string? serverId = null)
        => cache.Find(playerUid, serverId);

    // The pending entry listed for this name on `serverId`, or null. The gate calls this on a
    // uid-coverage miss, using the authenticated name from the Identification frame: a pending
    // entry has no uid to match on, so FindCovering above can never answer it and this is the only
    // way it reaches the door. A network-wide pending entry covers whatever is asked; a scoped one
    // only its own backend.
    public WhitelistEntry? FindPending(string? playerName, string? serverId = null)
    {
        if (string.IsNullOrEmpty(playerName)) return null;
        return cache.FindFirst(e => e.IsPending
            && string.Equals(e.PlayerName, playerName, StringComparison.OrdinalIgnoreCase)
            && e.Matches(serverId));
    }

    // True when the registry answered and this list is now its answer. False means the list below
    // is whatever it was before, which matters to callers that just changed the registry and are
    // about to act on the result: see WhitelistRemoveCommand.
    public Task<bool> RefreshAsync() => cache.RefreshAsync();

    public Task RunAsync() => cache.RunAsync();
}
