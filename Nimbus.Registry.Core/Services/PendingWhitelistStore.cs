using System.Collections.Concurrent;
using Nimbus.Shared.Models;

namespace Nimbus.Registry.Services;

// The whitelist's pending rows, kept apart from the uid-keyed list because they are keyed by a
// different thing. A pending entry has no uid to key on: an operator listed a name for a player
// who has never connected, so it is stored under (PlayerName, ServerId) instead, which lets a
// pending Bob and a uid-keyed Bob coexist and lets a second pending Bob for the same scope replace
// the first. This is whitelist-only on purpose (#104): the ban path shares RegistryEntryStore and
// must never gain a name-keyed concept, so pending lives here rather than on the generic.
//
// Given a state file, the list survives a restart the same way the uid list does, and for the same
// reason: an event import stored as pending must not evaporate because the registry rebooted before
// the players arrived. A row read back that is not pending any more (it carries a uid) or has run
// out is dropped on load, mirroring how the uid store retires entries the clock has already passed.
internal sealed class PendingWhitelistStore
{
    private readonly ConcurrentDictionary<string, WhitelistEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly RegistryStateFile<WhitelistEntry>? _state;

    public PendingWhitelistStore(TimeProvider? clock, RegistryStateFile<WhitelistEntry>? state)
    {
        _clock = clock ?? TimeProvider.System;
        _state = state;
        if (_state is null) return;

        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        bool dropped = false;
        foreach (var entry in _state.Load())
        {
            // A row that has since been bound (it carries a uid) belongs to the uid list, not here,
            // and one that has expired is over. Either way it is not a live pending entry, so it is
            // left out and the file is rewritten without it.
            if (!entry.IsPending || !entry.IsActiveAt(now)) { dropped = true; continue; }
            _entries[Key(entry.PlayerName, entry.ServerId)] = entry;
        }
        if (dropped) Persist(now);
    }

    private static string Key(string playerName, string? serverId)
        => (playerName ?? "").ToLowerInvariant() + "|" + (serverId ?? "").ToLowerInvariant();

    // Adds or replaces the pending entry for this (name, scope). A second pending entry for the
    // same name and scope updates in place rather than stacking.
    public WhitelistEntry Add(WhitelistEntry entry)
    {
        _entries[Key(entry.PlayerName, entry.ServerId)] = entry;
        Persist();
        return entry;
    }

    public bool Remove(string playerName, string? serverId)
    {
        if (!_entries.TryRemove(Key(playerName, serverId), out _)) return false;
        Persist();
        return true;
    }

    // The pending entry covering this name on `serverId`, or null. Pass an empty serverId to ask
    // only about network-wide pending entries. Expired entries are skipped. A walk rather than a
    // keyed lookup for the same reason the uid store walks: a network-wide pending entry has an
    // empty ServerId in its key but must answer a scoped query, so the match is on Matches, not on
    // the key.
    public WhitelistEntry? Find(string playerName, string? serverId = null)
    {
        if (string.IsNullOrEmpty(playerName)) return null;
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        foreach (var kv in _entries) // NOSONAR
        {
            var entry = kv.Value;
            if (!string.Equals(entry.PlayerName, playerName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!entry.IsActiveAt(now)) continue;
            if (entry.Matches(serverId)) return entry;
        }
        return null;
    }

    public List<WhitelistEntry> Active()
    {
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        return _entries.Values.Where(entry => entry.IsActiveAt(now)).ToList();
    }

    public int Prune()
    {
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        int dropped = 0;
        foreach (var kv in _entries)
        {
            if (!kv.Value.IsActiveAt(now) && _entries.TryRemove(kv.Key, out _))
                dropped++;
        }
        if (dropped > 0) Persist(now);
        return dropped;
    }

    private void Persist() => Persist(_clock.GetUtcNow().ToUnixTimeSeconds());

    private void Persist(long nowUnix) => _state?.Save(_entries.Values, nowUnix);
}
