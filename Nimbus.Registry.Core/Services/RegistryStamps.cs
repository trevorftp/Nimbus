using Nimbus.Shared.Models;

namespace Nimbus.Registry.Services;

// Turns a moderation request into the entry a store keeps. Small, but written twice before
// (#65): POST /api/bans and the proxy's in-process client both had their own copy of the same
// null-coalescing and the same "0 means permanent" arithmetic, and a divergence there is
// invisible until somebody compares two deployments entry by entry.
//
// Only the shaping lives here. Whether the caller answers a refusal with a 400 or with a null
// stays with the caller, and so does the clock: both stores judge expiry on an injected
// TimeProvider, so the caller passes the reading it already took rather than letting this
// class reach for a second one.
public static class RegistryStamps
{
    // Null when the request names nobody. An entry keyed on the empty uid blocks nobody, covers
    // nobody, and cannot be lifted by name.
    public static NetworkBan? NewBan(BanRequest? req, long nowUnix)
    {
        if (req is null || string.IsNullOrEmpty(req.PlayerUid)) return null;
        return new NetworkBan
        {
            PlayerUid = req.PlayerUid,
            PlayerName = req.PlayerName ?? "",
            ServerId = req.ServerId ?? "",
            Reason = req.Reason ?? "",
            BannedBy = req.BannedBy ?? "",
            CreatedAtUnix = nowUnix,
            ExpiresAtUnix = Expiry(req.DurationSeconds, nowUnix),
        };
    }

    // Same contract as NewBan, read the other way round, with one addition the ban side has no use
    // for: a request that names a player by name alone, with no uid, is a pending entry (#104). It
    // is stamped exactly like a normal one but keeps the empty uid, which is what WhitelistEntry
    // reads as pending and what routes it to the name-keyed list. Only a request that names nobody
    // at all, neither uid nor name, is refused.
    public static WhitelistEntry? NewWhitelistEntry(WhitelistRequest? req, long nowUnix)
    {
        if (req is null) return null;
        bool hasUid = !string.IsNullOrEmpty(req.PlayerUid);
        bool hasName = !string.IsNullOrEmpty(req.PlayerName);
        if (!hasUid && !hasName) return null;
        return new WhitelistEntry
        {
            PlayerUid = req.PlayerUid ?? "",
            PlayerName = req.PlayerName ?? "",
            ServerId = req.ServerId ?? "",
            Note = req.Note ?? "",
            AddedBy = req.AddedBy ?? "",
            CreatedAtUnix = nowUnix,
            ExpiresAtUnix = Expiry(req.DurationSeconds, nowUnix),
        };
    }

    // Zero, not "now": no duration means permanent, and stamping an expiry of now would lift
    // the entry on the very next read.
    private static long Expiry(int durationSeconds, long nowUnix)
        => durationSeconds > 0 ? nowUnix + durationSeconds : 0;
}
