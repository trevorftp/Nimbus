using Nimbus.Shared.Models;

namespace Nimbus.Registry.Services;

// Network whitelist. One entry per (PlayerUid, ServerId) pair, so a player can hold a
// network-wide entry and per-backend entries at the same time. Timed entries expire on read
// and are dropped by the background sweep. Shaped like BanStore because the gate is the same
// lookup read the other way round.
//
// The registry does not know whether any proxy is enforcing this list: the [whitelist] switches
// live in proxy config. Storing an entry is therefore never an enforcement decision.
// Given a state file, the list survives a restart the same way the ban list does: read once
// here, written back whole on every change. A whitelist that emptied itself on restart would
// lock every player out of a closed network instead of letting a griefer back in, so the two
// stores get the same treatment for opposite reasons.
public sealed class WhitelistStore
{
    private readonly RegistryEntryStore<WhitelistEntry> _store;
    private readonly PendingWhitelistStore _pending;

    public WhitelistStore(TimeProvider? clock = null, RegistryStateFile<WhitelistEntry>? state = null,
        RegistryStateFile<WhitelistEntry>? pendingState = null)
    {
        _store = new RegistryEntryStore<WhitelistEntry>(clock, state);
        _pending = new PendingWhitelistStore(clock, pendingState);
    }

    // Adds or replaces an entry, routing by shape: a name-only entry (no uid) is a pending entry
    // and goes to the name-keyed list; a uid entry takes the ordinary path, byte for byte the same
    // as before. Re-adding an already-listed player updates the note and duration rather than
    // stacking, on whichever list owns it.
    public WhitelistEntry Add(WhitelistEntry entry)
        => entry.IsPending ? _pending.Add(entry) : _store.Add(entry);

    public bool Remove(string playerUid, string? serverId) => _store.Remove(playerUid, serverId);

    // The entry covering this player on `serverId`, or null. Pass an empty serverId to ask only
    // about network-wide coverage. Expired entries are skipped. Uid-keyed only: a pending entry
    // has no uid and is found through FindPending instead.
    public WhitelistEntry? FindCovering(string playerUid, string? serverId = null)
        => _store.Find(playerUid, serverId);

    // The pending entry listed for this name on `serverId`, or null. The gate calls this on a
    // uid-coverage miss, using the authenticated name, and admits the player if it answers.
    public WhitelistEntry? FindPending(string playerName, string? serverId = null)
        => _pending.Find(playerName, serverId);

    // Rewrites the pending entry for (name, scope) to carry `playerUid`, moving it into the
    // uid-keyed list so it persists as, and matches as, an ordinary entry from now on. The bound
    // row is added to the uid list before the pending row is dropped, so a join landing in the
    // gap between the two is covered by one or the other and never by neither. Idempotent: an
    // already-bound or absent name has nothing to move and is reported as false, which the callers
    // read as a no-op success.
    public bool Bind(string playerName, string playerUid, string? serverId)
    {
        if (string.IsNullOrEmpty(playerName) || string.IsNullOrEmpty(playerUid)) return false;

        var pending = _pending.Find(playerName, serverId);
        if (pending == null) return false;

        _store.Add(new WhitelistEntry
        {
            PlayerUid = playerUid,
            PlayerName = pending.PlayerName,
            ServerId = pending.ServerId,
            Note = pending.Note,
            AddedBy = pending.AddedBy,
            CreatedAtUnix = pending.CreatedAtUnix,
            ExpiresAtUnix = pending.ExpiresAtUnix,
        });
        _pending.Remove(pending.PlayerName, pending.ServerId);
        return true;
    }

    // Both lists, so an operator listing and the proxy cache see uid entries and pending entries
    // alike. Pending entries carry an empty uid, so FindCovering never matches them and the gate's
    // uid path is unaffected by their presence.
    public List<WhitelistEntry> Active()
    {
        var active = _store.Active();
        active.AddRange(_pending.Active());
        return active;
    }

    public int Prune() => _store.Prune() + _pending.Prune();
}
