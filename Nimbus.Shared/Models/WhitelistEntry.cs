namespace Nimbus.Shared.Models;

// A whitelist entry held by the registry, the inverse gate of NetworkBan: a ban says who may
// not come in, an entry here says who may. Same shape on purpose, keyed on PlayerUid because
// names change and uids do not.
//
// The list on its own decides nothing. Enforcement is a proxy-side toggle, the [whitelist]
// section of nimbus.proxy.toml, so an entry sitting in the registry while enforcement is off
// costs nobody anything.
public sealed class WhitelistEntry : IScopedEntry
{
    public string PlayerUid { get; set; } = "";

    // Last known name, for readable listings. Never used for matching.
    public string PlayerName { get; set; } = "";

    // Empty means network-wide: the entry covers every backend. Otherwise it covers that one
    // backend and no other.
    public string ServerId { get; set; } = "";

    public string Note { get; set; } = "";
    public string AddedBy { get; set; } = "";
    public long CreatedAtUnix { get; set; }

    // 0 means permanent.
    public long ExpiresAtUnix { get; set; }

    public bool IsNetworkWide => string.IsNullOrEmpty(ServerId);

    // A pending entry carries a name to be matched by and no uid yet: it is the row an operator
    // adds for a player who has never connected, so there is no PlayerUid to key on. The gate
    // matches it by the authenticated name at the door and binds it to that account's uid on the
    // first join, after which it is an ordinary uid-keyed entry and this reads false. The empty
    // uid is the whole of the state: nothing else needs to be stored to know an entry is pending,
    // and a bool alongside it could only ever disagree with the uid it is meant to describe.
    public bool IsPending => string.IsNullOrEmpty(PlayerUid) && !string.IsNullOrEmpty(PlayerName);

    public bool IsActiveAt(long nowUnix)
        => ExpiresAtUnix <= 0 || nowUnix < ExpiresAtUnix;

    // True when this entry covers the given backend. A network-wide entry covers every backend,
    // a scoped one only its own. An empty serverId asks whether the network itself is covered,
    // which is all a backend with no ServerId can be asked about.
    public bool Covers(string? serverId)
        => IsNetworkWide || string.Equals(ServerId, serverId, StringComparison.OrdinalIgnoreCase);

    // The IScopedEntry reading of the same test: an entry matches the scope it covers.
    public bool Matches(string? serverId) => Covers(serverId);
}

public sealed class WhitelistRequest
{
    public string PlayerUid { get; set; } = "";
    public string PlayerName { get; set; } = "";

    // Empty for a network-wide entry.
    public string ServerId { get; set; } = "";
    public string Note { get; set; } = "";
    public string AddedBy { get; set; } = "";

    // 0 or less means permanent.
    public int DurationSeconds { get; set; }
}

// Body of POST /api/whitelist/remove. The arguments live here rather than in the query because
// the HMAC covers method, path, body, timestamp and nonce, and never the query string.
public sealed class WhitelistRemoveRequest
{
    public string PlayerUid { get; set; } = "";

    // Empty removes the network-wide entry. A scoped entry must be removed with the serverId it
    // was created with.
    public string ServerId { get; set; } = "";
}

// Body of POST /api/whitelist/bind. The proxy sends this once its gate has matched a pending
// entry against a joining player: the name that was listed, the authenticated uid that name
// turned out to carry, and the scope the pending entry was stored under. The registry rewrites
// the pending row to carry the uid, so the next join for that player matches by uid directly.
public sealed class WhitelistBindRequest
{
    public string PlayerName { get; set; } = "";
    public string PlayerUid { get; set; } = "";

    // Empty binds the network-wide pending entry; a scoped one must be bound with the serverId it
    // was stored under.
    public string ServerId { get; set; } = "";
}

public sealed class WhitelistResponse
{
    public bool Ok { get; set; }
    public WhitelistEntry? Entry { get; set; }
    public string? Error { get; set; }
}

public sealed class WhitelistListResponse
{
    public bool Ok { get; set; }
    public List<WhitelistEntry> Entries { get; set; } = new();
}
