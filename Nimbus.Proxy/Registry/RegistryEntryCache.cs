using Nimbus.Shared.Models;

namespace Nimbus.Proxy;

// Proxy-side snapshot of one of the registry's per-scope lists, the mechanism behind BanCache
// and WhitelistCache.
//
// The connection gate runs while parsing Identification, on the byte pump, so it has to be a
// synchronous lookup: a registry round-trip per join would put the control plane on the hot path
// of every login. A background refresh keeps the list warm instead, and a locally issued change
// is applied immediately so an operator's action takes effect on the next join rather than after
// the next poll.
//
// A registry outage leaves the last known list in place. HasSynced tells "the list really is
// empty" apart from "we have never managed to read it", the cold-start distinction the whitelist
// gate depends on and the ban gate simply never asks about.
internal sealed class RegistryEntryCache<TEntry> where TEntry : class, IScopedEntry
{
    private readonly IRegistryClient? registry;
    private readonly Func<IRegistryClient, CancellationToken, Task<List<TEntry>?>> fetch;
    private readonly string label;
    private readonly CancellationToken stopToken;
    private readonly TimeProvider clock;
    private readonly TimeSpan refreshPeriod;

    private volatile TEntry[] entries = Array.Empty<TEntry>();
    private volatile bool synced;

    public RegistryEntryCache(IRegistryClient? registry,
        Func<IRegistryClient, CancellationToken, Task<List<TEntry>?>> fetch, string label,
        CancellationToken stopToken, TimeSpan? refreshPeriod, TimeProvider? clock)
    {
        this.registry = registry;
        this.fetch = fetch;
        this.label = label;
        this.stopToken = stopToken;
        this.clock = clock ?? TimeProvider.System;
        this.refreshPeriod = refreshPeriod ?? TimeSpan.FromSeconds(15);
    }

    public int Count => entries.Length;

    // True once the registry has answered at least once since boot. False means the list below
    // is a guess, not an answer: nobody has ever been listed as far as this proxy knows.
    public bool HasSynced => synced;

    // The entry that currently applies to this player on `serverId`, or null. A network-wide
    // entry matches whatever is asked; a scoped one only its own backend. Pass no serverId to ask
    // about the network alone, which is all a backend configured as host:port can be asked about.
    public TEntry? Find(string? playerUid, string? serverId = null)
    {
        if (string.IsNullOrEmpty(playerUid)) return null;
        var snapshot = entries;
        if (snapshot.Length == 0) return null;

        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        foreach (var entry in snapshot)
        {
            if (!string.Equals(entry.PlayerUid, playerUid, StringComparison.OrdinalIgnoreCase)) continue;
            // Expiry is checked here too: a timed entry must stop applying even if the next
            // refresh has not landed yet.
            if (!entry.IsActiveAt(now)) continue;
            if (entry.Matches(serverId)) return entry;
        }
        return null;
    }

    // The first active entry the predicate accepts, or null. The uid path above is a closed lookup
    // that every list shares; this is the seam a specific list uses for a match its own entry type
    // knows how to make and IScopedEntry does not, which today is only the whitelist's pending
    // lookup by name. Only called on a uid-coverage miss, never on the settled path, so the closure
    // it takes is off the hot lane.
    public TEntry? FindFirst(Func<TEntry, bool> predicate)
    {
        var snapshot = entries;
        if (snapshot.Length == 0) return null;

        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        foreach (var entry in snapshot)
        {
            if (!entry.IsActiveAt(now)) continue;
            if (predicate(entry)) return entry;
        }
        return null;
    }

    // True when the registry answered and this list is now its answer. False means the list below
    // is whatever it was before, which matters to callers that just changed the registry and are
    // about to act on the result: see WhitelistRemoveCommand.
    public async Task<bool> RefreshAsync()
    {
        if (registry == null) return false;
        var fetched = await fetch(registry, stopToken).ConfigureAwait(false);
        if (fetched == null) return false;  // registry error: keep the previous list
        entries = fetched.ToArray();
        synced = true;
        return true;
    }

    public async Task RunAsync()
    {
        if (registry == null) return;

        while (!stopToken.IsCancellationRequested)
        {
            try { await RefreshAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Warn($"{label} refresh failed: {ex.GetType().Name}: {ex.Message}"); }

            try { await Task.Delay(refreshPeriod, stopToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
