using System.Net.Sockets;
using Nimbus.Shared.Models;

namespace Nimbus.Proxy;

// A single proxied player session. Owns the client socket and the current upstream socket.
//
// Lifecycle:
//   - Client connects, ProxySession is created.
//   - RunAsync opens the initial upstream and runs c->s and s->c byte pumps until either
//     side closes or one of the transfer requests fires.
//   - On redirect, the proxy forges a Packet_ServerRedirect and closes. The reconnect is
//     routed by the staged sticky, matched on player UID or on client address, before the
//     new upstream is opened.
//   - On seamless, the normal path uses the same safe redirect underneath while Nimbus.Client
//     hides the VS loading UI. Raw upstream splice lives behind an unsafe config flag.
internal sealed partial class ProxySession : IPlayer
{
    public long Id { get; }

    private readonly ProxyConfig cfg;
    private readonly TcpClient client;
    private readonly NetworkStream clientStream;
    private readonly CancellationToken sessionStopToken;
    private readonly DateTimeOffset sessionStart = DateTimeOffset.UtcNow;
    private readonly string clientRemote; // captured at construction — safe after socket close
    // The key this session's UDP override is filed under, captured for the same reason: the
    // socket is closed by the time the teardown wants to take the override back down, and
    // TcpClient.Close() nulls Client. Kept in the raw, un-normalised form the relay matches
    // incoming datagrams against.
    private readonly System.Net.IPAddress? clientAddress;
    private readonly SessionState? state;
    private readonly FrameSniffer? sniffC2S;
    private readonly FrameSniffer? sniffS2C;
    private readonly StickyRouteTable? stickies;
    private readonly IRegistryClient? registry;
    private readonly UdpRouteOverrides? udpOverrides;
    private readonly EventBus? events;

    private TcpClient? upstream;
    private BackendEndpoint? currentBackend;
    private CancellationTokenSource? pumpCts;
    private Task? pumpC2S;
    private Task? pumpS2C;

    private byte[]? capturedIdentification;
    private string? capturedPlayerUid;
    private string? capturedPlayerName;

    // Set when ClientSessionRunner routed this session off a sticky it matched on the client
    // address rather than on a player UID. Reconciled against the real UID once Identification
    // arrives.
    private StickyRoute? ipRoutedSticky;

    // The backend the captured Identification bytes have been handed to. The mp token inside
    // them is single use: the first backend to check it with the auth server consumes it, and a
    // second backend asking about the same token is told 'missingaccount' and kicks the player
    // (#57). Recorded once and never moved, because the first backend is the one that owns the
    // token for the rest of this session.
    private BackendEndpoint? identificationSentTo;
    private volatile bool warnedIdentificationReplay;
    private volatile bool seamlessCapable;
    private readonly object swapLock = new();
    private volatile bool swapping;
    private volatile bool closed;

    private long c2sBytes;
    private long s2cBytes;
    private volatile bool kickedByBackend;

    // Initial-join reservation state for the currently connected backend:
    //   0 = pending, 1 = done (or not needed), 2 = failed terminal.
    private int initialReservationState;

    private readonly BanCache? bans;
    private readonly WhitelistCache? whitelist;

    // Set once the ban or whitelist gate has told the client why it is being dropped. Stops the
    // connect path from dialing a backend anyway and from forging a second, misleading disconnect.
    private volatile bool rejectedAtGate;
    private volatile string? gateRejectionReason;

    // The in-flight forged disconnect the gate started. Awaited before the sockets are torn down,
    // because the write races the teardown otherwise and the player sees a dropped connection
    // instead of the reason they were sent away.
    private volatile Task? gateDisconnect;

    // The four transport essentials the session cannot exist without, plus the ambient services the
    // surrounding proxy lends it (see SessionServices). Callers that supplied no services pass an
    // all-null bundle, which lands the members exactly where omitted arguments used to.
    public ProxySession(long id, ProxyConfig cfg, TcpClient client, CancellationToken stopToken,
        SessionServices? services = null)
    {
        Id = id;
        this.cfg = cfg;
        this.client = client;
        this.client.NoDelay = true;
        this.clientStream = client.GetStream();
        this.sessionStopToken = stopToken;
        var clientEp = client.Client?.RemoteEndPoint as System.Net.IPEndPoint;
        this.clientAddress = clientEp?.Address;
        this.clientRemote = DescribeClient(this.clientAddress);
        var svc = services ?? new SessionServices();
        this.stickies = svc.Stickies;
        this.registry = svc.Registry;
        this.udpOverrides = svc.UdpOverrides;
        this.events = svc.Events;
        this.bans = svc.Bans;
        this.whitelist = svc.Whitelist;

        // Sniffers always run on the client stream so registry-backed joins and transfers have
        // the player UID even when SniffFrames is disabled.
        this.state = new SessionState(id);
        this.sniffC2S = new FrameSniffer(id, "c->s", state) { Verbose = cfg.Logging.SniffFrames };
        this.sniffS2C = new FrameSniffer(id, "s->c", state) { Verbose = cfg.Logging.SniffFrames };
        this.sniffC2S.OnRawFrame = OnClientFrame;
    }

    // The client address as everything that reads a log line or a sticky route expects it: an
    // IPv4 one unwrapped from ::ffff:1.2.3.4 under dual-stack, and "?" when the socket had no
    // endpoint to offer.
    private static string DescribeClient(System.Net.IPAddress? address)
    {
        if (address == null) return "?";
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
    }

    public SessionState.Phase Phase => state?.Current ?? SessionState.Phase.TcpOpen;

    public string? PlayerUid => capturedPlayerUid;
    public string? PlayerName => capturedPlayerName;

    // The backend this session is pumped to right now, or null before the first upstream is up.
    // Read by the evacuate command to find the sessions sitting on the backend being emptied.
    public BackendEndpoint? CurrentBackend => currentBackend;

    public bool HasIdentification => capturedIdentification != null;
    public bool SupportsSeamlessTransfers => seamlessCapable;
    public string ClientRemote => clientRemote;

    // IPlayer surface (aliases over the existing internal fields so handlers get a stable API).
    string? IPlayer.Uid => capturedPlayerUid;
    string? IPlayer.Name => capturedPlayerName;
    IServerInfo? IPlayer.CurrentServer => currentBackend == null ? null : currentBackend.ToServerInfo();
    bool IPlayer.SupportsSeamlessTransfers => SupportsSeamlessTransfers;

    Task<string?> IPlayer.TransferAsync(IServerInfo target, string? reason)
        => ((IPlayer)this).TransferAsync(target, cfg.Transfers.DefaultMode, reason);

    async Task<string?> IPlayer.TransferAsync(IServerInfo target, string mode, string? reason)
        => (await RequestTransferAsync(target.ToEndpoint(), mode, registry, reason, cfg.Registry.FailOnError).ConfigureAwait(false)).failReason;

    // The mode name every branch below reports back, and the one ProxyConfigValidator accepts in
    // transfers.default_mode. It is the tuple's first element on six paths that each return a
    // different failure, so it is written once rather than six times.
    private const string SeamlessMode = "seamless";

    internal async Task<(string modeUsed, string? failReason)> RequestTransferAsync(BackendEndpoint target, string mode,
        IRegistryClient? registry = null, string? reason = null, bool failOnRegistryError = true,
        string? clientTransferId = null)
    {
        string normalized = string.Equals(mode, "splice", StringComparison.OrdinalIgnoreCase) ? SeamlessMode : mode;
        if (string.Equals(normalized, SeamlessMode, StringComparison.OrdinalIgnoreCase))
        {
            if (!cfg.Transfers.AllowSeamless)
                return (SeamlessMode, "seamless transfers disabled in config");

            if (Phase != SessionState.Phase.Ready)
                return (SeamlessMode, $"seamless requires a fully joined session (phase=Ready). current phase={Phase}");

            if (cfg.Transfers.RequireSeamlessCapability && !SupportsSeamlessTransfers)
            {
                if (!cfg.Transfers.FallbackToRedirectWhenSeamlessUnavailable)
                    return (SeamlessMode, "client has not advertised Nimbus seamless capability");

                Log.Warn($"[s{Id}] seamless requested but client has no Nimbus capability; falling back to redirect");
                var redirectFail = await RequestRedirectAsync(target, registry,
                    reason ?? "seamless unavailable, redirect fallback", failOnRegistryError, clientTransferId).ConfigureAwait(false);
                return ("redirect", redirectFail);
            }

            if (!cfg.Transfers.EnableUnsafeSeamlessSplice)
            {
                var redirectFail = await RequestRedirectAsync(target, registry,
                    reason ?? "seamless visual redirect", failOnRegistryError, clientTransferId).ConfigureAwait(false);
                return (SeamlessMode, redirectFail);
            }

            return (SeamlessMode, await RequestSeamlessAsync(target, registry, reason, failOnRegistryError, clientTransferId).ConfigureAwait(false));
        }
        if (string.Equals(normalized, "redirect", StringComparison.OrdinalIgnoreCase))
            return ("redirect", await RequestRedirectAsync(target, registry, reason, failOnRegistryError, clientTransferId).ConfigureAwait(false));
        return (normalized, $"unknown transfer mode '{mode}'");
    }

    internal void MarkSeamlessCapable()
    {
        seamlessCapable = true;
    }

    void IPlayer.Disconnect(string? reason)
    {
        if (!string.IsNullOrEmpty(reason)) Log.Info($"[s{Id}] disconnect requested by handler: {reason}");
        Close();
    }

    // Real client endpoint as seen by this proxy. Forwarded to backends via reservation.
    private (string ip, int port) ClientEndpoint
    {
        get
        {
            try
            {
                if (client.Client?.RemoteEndPoint is System.Net.IPEndPoint ep)
                {
                    var addr = ep.Address;
                    // Unwrap ::ffff:1.2.3.4 so backends see a clean IPv4 string under dual-stack.
                    if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
                    return (addr.ToString(), ep.Port);
                }
            }
            catch { /* the socket can be closed under us between the null check and the read */ }
            return ("", 0);
        }
    }

    // Force-close. Pumps and sockets tear down on the next loop iteration.
    public void Close()
    {
        closed = true;
        // Every step here is best-effort: Close races the pumps and the teardown in RunAsync's
        // finally, so any of the three can already be disposed. Whoever gets there first wins and
        // the loser has nothing left to do.
        try { pumpCts?.Cancel(); } catch { /* already disposed by an earlier teardown */ }
        try { upstream?.Close(); } catch { /* backend socket may be gone already */ }
        try { client.Close(); } catch { /* the client may have dropped first */ }
    }

    private void OnClientFrame(string name, ReadOnlyMemory<byte> raw)
    {
        if (name == "Identification")
        {
            CaptureIdentification(raw.Span, source: "sniffer");
            return;
        }

        if (name == "Chatline")
            SurfaceChatLine(raw.Span);
    }

    // Read-only PlayerChatEvent for plugins. Parsing is skipped entirely when nothing subscribed,
    // so a proxy with no chat-aware plugin pays nothing on this path beyond the name compare.
    private void SurfaceChatLine(ReadOnlySpan<byte> raw)
    {
        if (events == null || !events.HasSubscribers<PlayerChatEvent>()) return;
        if (!ChatlineParser.TryExtract(raw, out string message, out int groupId)) return;

        var server = currentBackend?.ToServerInfo();
        var evt = new PlayerChatEvent(this, server, message, groupId);
        // Off the pump: a slow handler must not stall the player's own traffic. Chat is observed,
        // never gated, so nothing downstream waits on this. The token keeps a proxy that is
        // already stopping from waking handlers for a line nobody can act on any more.
        _ = Task.Run(async () =>
        {
            try { await events.FireAsync(evt).ConfigureAwait(false); }
            catch { /* a plugin that throws on a chat line does not get to end the session */ }
        }, sessionStopToken);
    }

    // `landingOn` is the backend this session is about to be connected to, passed by the connect
    // path because `currentBackend` is only assigned once the socket is up. On the pump path the
    // backend is already current and the argument is left null.
    private bool CaptureIdentification(ReadOnlySpan<byte> raw, string source, BackendEndpoint? landingOn = null)
    {
        if (capturedIdentification != null) return true;

        var frame = raw.ToArray();
        if (!IdentificationParser.TryExtract(frame, out var uid, out var pname))
        {
            Log.Warn($"[s{Id}] Identification frame from {source} ({frame.Length} bytes) did not parse a PlayerUID; will retry on the next Identification frame from this client");
            return false;
        }

        capturedIdentification = frame;
        capturedPlayerUid = uid;
        capturedPlayerName = pname;

        // The sniffer runs ahead of the forward on the c->s pump, so these bytes are on their
        // way to the backend we are already connected to. That backend now owns the token.
        if (currentBackend != null) NoteIdentificationDelivered(currentBackend);

        // Ban gate. Synchronous against the warm cache: this runs on the byte pump, so it must
        // not wait on the registry. A network-wide ban ends the session here, before any backend
        // sees the player, and so does a ban scoped to the backend this session is landing on.
        // The scope cannot be checked any earlier: the backend is picked before the client has
        // sent an identity (#57). A backend configured as host:port has no ServerId, so no scoped
        // ban can match it.
        var landing = landingOn ?? currentBackend;
        var ban = bans?.FindBlocking(uid, landing?.ServerId);
        if (ban != null)
        {
            rejectedAtGate = true;
            gateRejectionReason = "player is banned";
            RejectBannedPlayer(ban);
            return false;
        }

        // Whitelist gate, checked after the ban gate because a ban wins over an entry. Same warm
        // cache, same landing backend: a backend with no ServerId can only be gated network-wide,
        // and then only a network-wide entry covers it.
        var missing = CheckLandingWhitelist(landing);
        if (missing != null)
        {
            rejectedAtGate = true;
            gateRejectionReason = "player is not whitelisted";
            RejectUnwhitelistedPlayer(missing.Value);
            return false;
        }

        ReconcileIpRoutedSticky(uid);
        TryConsumeStickyRoute(uid);
        return true;
    }

    // Called by ClientSessionRunner when the reconnect was matched on the client address.
    public void NoteRoutedByStickyIp(StickyRoute route) => ipRoutedSticky = route;

    // Several players behind one NAT can transfer inside the same window, and an address-matched
    // route hands out the oldest of them, so the player who shows up may not be the one the route
    // was staged for. That is survivable: whoever took it runs a fresh token exchange with that
    // backend and plays there perfectly well, connected rather than kicked. The route itself goes
    // back under its owner's UID so their own reconnect still finds it, or so the late fallback
    // below moves them once they identify. It is deliberately not re-indexed by address: putting
    // it back there is how the two of them would swap places a second time.
    //
    // The interloper is left where they landed. They are on a working session and can move
    // themselves, and chasing them would mean a third redirect for a problem that has already
    // stopped costing anyone their connection.
    private void ReconcileIpRoutedSticky(string uid)
    {
        var route = ipRoutedSticky;
        if (route == null) return;
        ipRoutedSticky = null;
        if (string.Equals(route.Uid, uid, StringComparison.OrdinalIgnoreCase)) return;

        Log.Warn($"[s{Id}] sticky route staged for uid {route.Uid} was matched on client ip {route.ClientIp} " +
                 $"but this session identified as uid {uid}; re-staging the route under its own uid only");
        stickies?.Stage(route.Uid, clientIp: null, route.Target, StickyRouteTable.UidTtl, route.Reason, route.Attempts);
    }

    // Tells the player which ban closed the door, then tears the session down.
    private void RejectBannedPlayer(NetworkBan ban)
    {
        string until = ban.ExpiresAtUnix > 0
            ? $" (until {DateTimeOffset.FromUnixTimeSeconds(ban.ExpiresAtUnix):u})"
            : "";
        // A scoped ban leaves the rest of the network reachable, so the player must not be told
        // the whole network is closed to them.
        string scope = ban.IsNetworkWide ? "this network" : "this server";
        string reason = string.IsNullOrWhiteSpace(ban.Reason)
            ? $"You are banned from {scope}{until}."
            : $"You are banned from {scope}{until}: {ban.Reason}";

        Log.Info($"[s{Id}] {capturedPlayerName ?? "?"} rejected: {(ban.IsNetworkWide ? "network ban" : $"ban scoped to {ban.ServerId}")}" +
                 (string.IsNullOrWhiteSpace(ban.Reason) ? "" : $" ({ban.Reason})"));
        ProxyMetrics.BannedJoinRejected();
        ForgeDisconnectAndClose(reason);
    }

    // Which whitelist switch shut the door on a player. Only used to pick the wording: telling
    // someone the network is closed when a single backend is would send them away for good.
    private enum WhitelistScope { Network, Server }

    // The scope refusing this landing, or null when the player may come in. Bans are checked
    // before this on purpose: a ban wins over an entry, so a listed player who is also banned
    // never reaches here.
    private WhitelistScope? CheckLandingWhitelist(BackendEndpoint? landing)
    {
        string? serverId = landing?.ServerId;
        if (!cfg.Whitelist.RequiresCoverage(serverId)) return null;

        // The network switch closes the whole door, so it owns the wording even when the landing
        // backend is also named in whitelist.servers.
        var scope = cfg.Whitelist.Network ? WhitelistScope.Network : WhitelistScope.Server;

        // Cold start. Nothing has ever been read from the registry, so an empty list here is not
        // an answer and must not be read as one. Closed is the default: the alternative leaves a
        // private network open to everyone for as long as the registry stays unreachable.
        if (whitelist == null || !whitelist.HasSynced)
        {
            WarnColdStartOnce(failOpen: cfg.Whitelist.FailOpenUntilFirstSync);
            return cfg.Whitelist.FailOpenUntilFirstSync ? null : scope;
        }

        return whitelist.FindCovering(capturedPlayerUid, serverId) != null ? null : scope;
    }

    // Once per process, not once per connection: a network locked out by an unreachable registry
    // would otherwise write a line per join attempt, which is the moment logs are least readable.
    private static int coldStartWarned;

    private static void WarnColdStartOnce(bool failOpen)
    {
        if (Interlocked.Exchange(ref coldStartWarned, 1) != 0) return;
        if (failOpen)
            Log.Warn("whitelist enforcement is on but the list has never been fetched; " +
                     "fail_open_until_first_sync = true, so players are being let in unchecked until the registry answers");
        else
            Log.Warn("whitelist enforcement is on but the list has never been fetched; refusing every join until the " +
                     "registry answers once. Set whitelist.fail_open_until_first_sync = true to let players in instead");
    }

    // Same shape as RejectBannedPlayer, for the gate the other way round.
    private void RejectUnwhitelistedPlayer(WhitelistScope scope)
    {
        string reason = scope == WhitelistScope.Network
            ? "This network is whitelisted."
            : "This server is whitelisted.";

        Log.Info($"[s{Id}] {capturedPlayerName ?? "?"} rejected: no whitelist entry covering " +
                 $"{(scope == WhitelistScope.Network ? "this network" : currentBackend?.ServerId ?? "this server")}");
        ProxyMetrics.UnwhitelistedJoinRejected();
        ForgeDisconnectAndClose(reason);
    }

    // Forges a vanilla disconnect so the player sees a reason instead of a dropped socket, then
    // tears the session down. Fire-and-forget because both gates call it from the pump.
    private void ForgeDisconnectAndClose(string reason)
    {
        gateDisconnect = Task.Run(async () =>
        {
            try
            {
                var frame = DisconnectBuilder.BuildDisconnectFrame(reason);
                // Two seconds to get the reason across, and never longer than the proxy itself
                // lives: a shutdown must not sit out the full courtesy write for a player whose
                // socket is going away regardless. RunAsync's finally closes what this skips.
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(sessionStopToken);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                await clientStream.WriteAsync(frame, cts.Token).ConfigureAwait(false);
                await clientStream.FlushAsync(cts.Token).ConfigureAwait(false);
                // Breathing room for the client to render the reason before the socket goes.
                // Cut short by the 2s budget or by session stop, and either way Close follows.
                try { await Task.Delay(150, cts.Token).ConfigureAwait(false); } catch { /* cut short, close now */ }
            }
            catch { /* the client may have left before the reason reached it; nothing to salvage */ }
            finally { Close(); }
        }, sessionStopToken);
    }

    // The reason a transfer to `target` is refused because of a ban, or null when it is allowed.
    // Both the UID and the destination are needed, so this is checked on the transfer methods
    // rather than at route selection, which runs before the client has sent any identity (#57).
    // A target with no ServerId carries no scope a ban could be matched against.
    private string? CheckTargetBan(BackendEndpoint target)
    {
        if (bans == null || string.IsNullOrEmpty(capturedPlayerUid) || string.IsNullOrEmpty(target.ServerId))
            return null;

        var ban = bans.FindBlocking(capturedPlayerUid, target.ServerId);
        if (ban == null) return null;
        return ban.IsNetworkWide
            ? "player is banned from the network"
            : $"player is banned from {target.ServerId}";
    }

    // The reason a transfer to `target` is refused for want of a whitelist entry, or null when it
    // is allowed. Always checked after CheckTargetBan: a ban wins over an entry.
    private string? CheckTargetWhitelist(BackendEndpoint target)
    {
        if (!cfg.Whitelist.RequiresCoverage(target.ServerId)) return null;

        // Cold start, same reading as the connection gate: an unread list is not an empty one.
        if (whitelist == null || !whitelist.HasSynced)
        {
            WarnColdStartOnce(failOpen: cfg.Whitelist.FailOpenUntilFirstSync);
            if (cfg.Whitelist.FailOpenUntilFirstSync) return null;
            return $"whitelist for {DescribeTarget(target)} has never been fetched from the registry";
        }

        if (whitelist.FindCovering(capturedPlayerUid, target.ServerId) != null) return null;
        return $"player is not whitelisted on {DescribeTarget(target)}";
    }

    // A backend configured as host:port has no id to name in a refusal, and can only have been
    // gated by whitelist.network in the first place.
    private static string DescribeTarget(BackendEndpoint target)
        => string.IsNullOrEmpty(target.ServerId) ? "the network" : target.ServerId;

    // The two gates every transfer runs against its destination, in this order because a ban wins
    // over a missing whitelist entry. Returns the refusal to hand back to the caller, or null when
    // the player may go. `mode` is only the word the log line uses to name the path asking.
    private string? CheckTransferGates(BackendEndpoint target, string mode)
    {
        var banFail = CheckTargetBan(target);
        if (banFail != null)
        {
            Log.Warn($"[s{Id}] {mode} rejected: {banFail}");
            return banFail;
        }

        var whitelistFail = CheckTargetWhitelist(target);
        if (whitelistFail != null)
        {
            Log.Warn($"[s{Id}] {mode} rejected: {whitelistFail}");
            return whitelistFail;
        }

        return null;
    }

    // How many redirects one staged route may fire before we give up and leave the player where
    // they landed. With address-matched routing in place the first redirect normally lands, and
    // the NAT mix-up above needs at most one more. Anything past that is a loop, not a retry.
    private const int MaxStickyRedirects = 3;

    // Late fallback: the route was still staged when this session identified, so the routing
    // decision at connect time missed it. Everything here has to assume the token is already
    // spent, because it is: this session identified to the backend it landed on.
    private void TryConsumeStickyRoute(string uid)
    {
        if (stickies == null || string.IsNullOrEmpty(uid)) return;
        if (!stickies.TryConsume(uid, out var route)) return;

        // Already where the route wanted us. Nothing to do, and replaying Identification to the
        // same backend would trip its duplicate-login path.
        if (currentBackend is BackendEndpoint cur &&
            string.Equals(cur.Host, route.Target.Host, StringComparison.OrdinalIgnoreCase) &&
            cur.Port == route.Target.Port)
            return;

        // A ban placed while the route sat staged. The redirect below would refuse it anyway;
        // dropping the route here leaves the player on the working session they already have.
        var banFail = CheckTargetBan(route.Target);
        if (banFail != null)
        {
            Log.Warn($"[s{Id}] sticky route to {route.Target} dropped: {banFail}; " +
                     $"leaving {capturedPlayerName ?? uid} on {currentBackend?.ToString() ?? "?"}");
            return;
        }

        // Same for a target that has since started requiring a whitelist entry this player does
        // not hold. Checked after the ban for the same reason the gate does.
        var whitelistFail = CheckTargetWhitelist(route.Target);
        if (whitelistFail != null)
        {
            Log.Warn($"[s{Id}] sticky route to {route.Target} dropped: {whitelistFail}; " +
                     $"leaving {capturedPlayerName ?? uid} on {currentBackend?.ToString() ?? "?"}");
            return;
        }

        if (route.Attempts >= MaxStickyRedirects)
        {
            Log.Warn($"[s{Id}] sticky route to {route.Target} dropped after {route.Attempts} redirects; " +
                     $"leaving {capturedPlayerName ?? uid} on {currentBackend?.ToString() ?? "?"}");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Redirect, never splice. This session has already identified to the backend it
                // landed on, which spent the mp token doing it, so replaying those bytes at the
                // target would have the target ask the auth server about a token that no longer
                // exists and kick the player. A redirect makes the client reconnect and run a
                // fresh token exchange with the target, which is the only auth-safe way to move
                // a session that identified somewhere else.
                var fail = await RequestRedirectAsync(route.Target, registry,
                    $"sticky reconnect: {route.Reason}", failOnRegistryError: false,
                    stickyAttempt: route.Attempts + 1).ConfigureAwait(false);
                if (fail != null)
                    Log.Warn($"[s{Id}] sticky reconnect redirect failed: {fail} (session stays on {currentBackend?.ToString() ?? "?"})");
            }
            catch (Exception ex) { Log.Warn($"[s{Id}] sticky reconnect redirect crashed: {ex.Message}"); }
        }, sessionStopToken);
    }

    // Records which backend received the captured Identification bytes. First writer wins: the
    // backend that validated the mp token keeps owning it for the rest of the session.
    private void NoteIdentificationDelivered(BackendEndpoint backend) => identificationSentTo ??= backend;

    // Tripwire for the one thing that is never safe: writing the captured Identification to a
    // backend other than the one that already has it. The mp token in those bytes is single use,
    // the auth server answers 'missingaccount' the second time it is asked about it, and the
    // player is kicked with "Bad game session, try relogging" (#57).
    //
    // The explicit unsafe splice flag is warned about rather than overridden. An operator who
    // turned it on may be running backends with auth verification off, where the replay does
    // work, and silently changing what an explicitly unsafe flag does would be worse than the
    // warning. Every other caller is refused, because after the late fallback became a redirect
    // nothing else in the proxy should be trying this at all.
    private string? CheckIdentificationReplay(BackendEndpoint target, bool operatorOptedIn)
    {
        var sentTo = identificationSentTo;
        if (sentTo == null) return null;
        if (string.Equals(sentTo.Host, target.Host, StringComparison.OrdinalIgnoreCase) && sentTo.Port == target.Port)
            return null;

        if (!operatorOptedIn)
        {
            Log.Warn($"[s{Id}] refusing to replay Identification to {target}: it was already delivered to {sentTo} " +
                     $"and its mp token is single use");
            return $"Identification was already delivered to {sentTo}; replaying it at {target} would spend an already-consumed mp token";
        }

        if (!warnedIdentificationReplay)
        {
            warnedIdentificationReplay = true;
            Log.Warn($"[s{Id}] unsafe splice replays Identification from {sentTo} to {target}; the mp token is single use " +
                     $"and {target} will reject the player unless that backend skips auth verification");
        }
        return null;
    }

    public async Task RunAsync(IReadOnlyList<BackendEndpoint> tryOrder, ReadOnlyMemory<byte> firstClientFrame = default)
    {
        try
        {
            var (connected, lastFailReason) = await ConnectAnyAsync(tryOrder, firstClientFrame).ConfigureAwait(false);
            if (!connected)
            {
                // The gate already sent the real reason; do not paper over it.
                if (rejectedAtGate) return;

                await SendNoBackendDisconnectAsync(lastFailReason).ConfigureAwait(false);
                return;
            }

            await PumpUntilClosedAsync().ConfigureAwait(false);
        }
        finally
        {
            await TearDownSessionAsync().ConfigureAwait(false);
        }
    }

    // Single-target convenience. Kept for callers that already have one endpoint in hand.
    // Sits here, ahead of the phases RunAsync is built from, so the two overloads stay adjacent.
    public Task RunAsync(BackendEndpoint initial) => RunAsync(new[] { initial });

    // Try each candidate until one connects. A handler cancelling stops the chain; a connect
    // failure moves on to the next backend. Returns whether the session got an upstream, and the
    // last reason it did not, which is what the player is eventually told.
    private async Task<(bool connected, string? lastFailReason)> ConnectAnyAsync(
        IReadOnlyList<BackendEndpoint> tryOrder, ReadOnlyMemory<byte> firstClientFrame)
    {
        string? lastFailReason = null;
        for (int i = 0; i < tryOrder.Count; i++)
        {
            var (ok, cancelled, reason) = await ConnectUpstreamAsync(tryOrder[i], firstClientFrame).ConfigureAwait(false);
            if (ok) return (true, null);
            lastFailReason = reason;
            if (cancelled) break;
            if (i + 1 < tryOrder.Count)
                Log.Info($"[s{Id}] failover: trying next candidate after '{reason}'");
        }
        return (false, lastFailReason);
    }

    // Nothing on the candidate list answered. Forge a disconnect so the player is told that,
    // rather than watching the connection drop for no stated reason. Best effort throughout: the
    // teardown runs next whether or not any of this reached the client.
    private async Task SendNoBackendDisconnectAsync(string? lastFailReason)
    {
        Log.Warn($"[s{Id}] no candidate connected: {lastFailReason ?? "unknown"}; sending forged disconnect");
        try
        {
            var frame = DisconnectBuilder.BuildDisconnectFrame($"No backend reachable right now ({lastFailReason ?? "all candidates failed"}). Please try again shortly.");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await clientStream.WriteAsync(frame, cts.Token).ConfigureAwait(false);
            await clientStream.FlushAsync(cts.Token).ConfigureAwait(false);
            try { await Task.Delay(150, cts.Token).ConfigureAwait(false); } catch { /* cut short, tear down now */ }
        }
        catch { /* no backend and now no client either; the teardown handles the rest */ }
    }

    // Sit on the pumps until they stop for good. They also stop for a swap, which installs a new
    // upstream and a new pair underneath us, so the exit has to tell the two cases apart.
    private async Task PumpUntilClosedAsync()
    {
        while (!sessionStopToken.IsCancellationRequested && !closed)
        {
            await Task.WhenAll(SafeAwait(pumpC2S!), SafeAwait(pumpS2C!)).ConfigureAwait(false);
            if (!swapping) break;  // pumps ended because of client/upstream close, not a swap

            // The swap routine installs the new pumps before it clears this flag.
            while (swapping && !sessionStopToken.IsCancellationRequested && !closed)
                await Task.Delay(10, sessionStopToken).ConfigureAwait(false);
        }
    }

    // Close everything this session owns, in the one order that does not cut a message short.
    // Runs from RunAsync's finally, so it runs however the session ended.
    private async Task TearDownSessionAsync()
    {
        closed = true;
        // A gate rejection forges its disconnect off the pump, so it can still be in flight
        // here. Closing the client socket underneath it would replace the reason the player
        // was given with a dropped connection. The write carries its own 2s cap.
        var pendingDisconnect = gateDisconnect;
        if (pendingDisconnect != null) await SafeAwait(pendingDisconnect).ConfigureAwait(false);
        // The pumps exited because one of these two ended, so the other is usually the only
        // one left to close and the dead one throws. This is the last owner either way.
        try { upstream?.Close(); } catch { /* backend already dropped us */ }
        try { client.Close(); } catch { /* client already dropped us */ }
        // Drop any UDP retargeting for this client IP so the next player (or NAT reuse) starts
        // fresh. Off the address captured at construction, not off the socket: by here the
        // client socket has been closed either by this teardown or by Close(), and
        // TcpClient.Close() nulls Client, so reading the endpoint back gives nothing to clear
        // under and the override outlives the session.
        if (udpOverrides != null && clientAddress != null)
            udpOverrides.Clear(clientAddress);

        await AnnounceDisconnectAsync().ConfigureAwait(false);

        var elapsed = DateTimeOffset.UtcNow - sessionStart;
        Log.Info($"[s{Id}] {capturedPlayerName ?? clientRemote} disconnected ({FormatDuration(elapsed)} | ↑{FormatBytes(c2sBytes)} ↓{FormatBytes(s2cBytes)})");
    }

    // Disconnect notifications are the last thing this session does. A handler that throws here
    // must not skip the ones after it or lose the summary line the teardown writes afterwards.
    private async Task AnnounceDisconnectAsync()
    {
        if (events == null) return;

        if (kickedByBackend && currentBackend != null)
        {
            try { await events.FireAsync(new ServerKickedEvent(this, currentBackend.ToServerInfo())).ConfigureAwait(false); }
            catch { /* a failed kick notification must not swallow the disconnect one */ }
        }
        try { await events.FireAsync(new PlayerDisconnectEvent(this, c2sBytes, s2cBytes)).ConfigureAwait(false); }
        catch { /* the session is over; a throwing handler changes nothing about that */ }
    }

    private async Task<(bool ok, bool cancelled, string? reason)> ConnectUpstreamAsync(BackendEndpoint target, ReadOnlyMemory<byte> firstClientFrame)
    {
        var (settled, cancelled, cancelReason) = await FirePreConnectAsync(target, reason: "initial connect", label: "initial upstream").ConfigureAwait(false);
        if (cancelled) return (false, true, cancelReason ?? "cancelled");
        target = settled;

        // Anything the opening frame settles stops the whole candidate chain rather than failing
        // over, because a different backend would not change the answer.
        var frameFail = await PrepareFirstClientFrameAsync(target, firstClientFrame).ConfigureAwait(false);
        if (frameFail != null) return (false, true, frameFail);

        var previous = currentBackend == null ? null : currentBackend.ToServerInfo();

        var (up, openFail) = await OpenUpstreamAsync(target, firstClientFrame).ConfigureAwait(false);
        if (openFail != null) return (false, false, openFail);

        upstream = up;
        currentBackend = target;
        // The first frame has just been written to this backend. If it was the Identification,
        // this backend is the one that gets to spend the mp token.
        if (capturedIdentification != null) NoteIdentificationDelivered(target);
        UpdateUdpOverride(target);
        StartPumps();
        Log.Info($"[s{Id}] {capturedPlayerName ?? "?"} ({clientRemote}) → {target.ServerId ?? target.ToString()}");
        if (events != null)
        {
            try { await events.FireAsync(new ServerPostConnectEvent(this, target.ToServerInfo(), previous)).ConfigureAwait(false); }
            catch { /* the player is connected and playing; a throwing handler cannot undo that */ }
        }
        return (true, false, null);
    }

    // ServerPreConnect: handlers can swap the target or cancel before a socket is opened. Returns
    // the destination the handlers settled on, and whether one of them refused. The raw
    // CancelReason is handed back rather than a formatted message, because the two transfer paths
    // word a cancellation differently. `label` only names the path in the log line.
    private async Task<(BackendEndpoint target, bool cancelled, string? cancelReason)> FirePreConnectAsync(
        BackendEndpoint target, string? reason, string label)
    {
        if (events == null) return (target, false, null);

        var pre = new ServerPreConnectEvent(this, target.ToServerInfo(), reason);
        await events.FireAsync(pre).ConfigureAwait(false);
        if (pre.IsCancelled)
        {
            Log.Warn($"[s{Id}] {label} cancelled by handler: {pre.CancelReason}");
            return (target, true, pre.CancelReason);
        }
        return (pre.Target.ToEndpoint(), false, null);
    }

    // Read the frame the client opened with, before any of it is replayed upstream: capture the
    // identity if it is there, let the gates answer, and prime the reservation. Returns the reason
    // this session must not reach a backend at all, or null to carry on connecting. A session with
    // no opening frame has nothing to settle here and passes straight through.
    private async Task<string?> PrepareFirstClientFrameAsync(BackendEndpoint target, ReadOnlyMemory<byte> firstClientFrame)
    {
        if (firstClientFrame.IsEmpty) return null;

        // Classify before capturing. A stock client opens with LoginTokenQuery and only sends
        // Identification once the backend has answered, so the first frame usually holds no
        // identity at all and handing it to the capture path made every single session log a
        // parse failure for a frame that never had a UID in it (#57).
        string firstName = PacketDispatch.DescribeFrame(clientToServer: true, firstClientFrame.Span);
        if (firstName == "Identification")
            CaptureIdentification(firstClientFrame.Span, source: "first frame", landingOn: target);
        else
            Log.Trace($"[s{Id}] first frame is {firstName}; identity arrives on a later frame");

        // A player the gate refused must not cause an upstream connection at all. The gate has
        // already told the client why it is going.
        if (rejectedAtGate)
            return gateRejectionReason ?? "player refused at the gate";

        // If the first frame already contained Identification, prime the reservation
        // before replaying bytes upstream. Missing UID here is non-fatal; the c->s pump
        // retries as soon as it captures Identification from later frames.
        var mintFail = await EnsureInitialReservationAsync(target, "initial connect").ConfigureAwait(false);
        if (mintFail != null)
        {
            Log.Warn($"[s{Id}] initial reservation mint failed for {target}: {mintFail}");
            return mintFail;
        }

        return null;
    }

    // Open the socket this session will be pumped over: dial, announce the real client with a
    // PROXY v2 header, and replay whatever the client already said. Returns the connected socket,
    // or the reason this candidate failed. Every failure drops only the socket it opened, so the
    // caller is free to try the next backend on the list.
    private async Task<(TcpClient? up, string? fail)> OpenUpstreamAsync(BackendEndpoint target, ReadOnlyMemory<byte> firstClientFrame)
    {
        var up = new TcpClient { NoDelay = true };
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(sessionStopToken);
        connectCts.CancelAfter(cfg.Advanced.ConnectTimeoutMs);
        ProxyMetrics.BackendConnectAttempt();
        try
        {
            await up.ConnectAsync(target.Host, target.Port, connectCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ProxyMetrics.BackendConnectFailure();
            Log.Warn($"[s{Id}] could not reach backend {target}: {ex.Message}");
            // The reason is already logged above; closing a socket that never connected is
            // housekeeping and its own failure adds nothing to the failover decision.
            try { up.Close(); } catch { /* never connected, nothing to report */ }
            return (null, $"{target}: {ex.Message}");
        }
        if (!await TryWriteProxyProtocolAsync(up, target).ConfigureAwait(false))
        {
            ProxyMetrics.BackendConnectFailure();
            try { up.Close(); } catch { /* the header write already failed; the socket is done */ }
            return (null, $"{target}: PROXY v2 header write failed");
        }
        if (!await TryWriteFirstClientFrameAsync(up, firstClientFrame).ConfigureAwait(false))
        {
            ProxyMetrics.BackendConnectFailure();
            try { up.Close(); } catch { /* the frame replay already failed; the socket is done */ }
            return (null, $"{target}: first client frame write failed");
        }
        ProxyMetrics.BackendConnectSuccess();
        return (up, null);
    }

    // Pin UDP for this client to the same backend our TCP session uses. No-op without overrides.
    private void UpdateUdpOverride(BackendEndpoint target)
    {
        // Same captured address the teardown clears under, so the two always agree on the key.
        // No usable client address means no override to pin, and UDP falls back to the default.
        if (udpOverrides == null || clientAddress == null) return;
        udpOverrides.Set(clientAddress, target);
    }

    private async Task<bool> TryWriteFirstClientFrameAsync(TcpClient up, ReadOnlyMemory<byte> frame)
    {
        if (frame.IsEmpty) return true;
        try
        {
            await up.GetStream().WriteAsync(frame, sessionStopToken).ConfigureAwait(false);
            sniffC2S?.OnBytes(frame.Span);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"[s{Id}] failed to replay first client frame: {ex.Message}");
            return false;
        }
    }

    // PROXY v2 has to be the first upstream bytes.
    private async Task<bool> TryWriteProxyProtocolAsync(TcpClient up, BackendEndpoint target)
    {
        if (!target.ProxyProtocol) return true;
        if (client.Client?.RemoteEndPoint is not System.Net.IPEndPoint clientEp ||
            up.Client?.LocalEndPoint is not System.Net.IPEndPoint upstreamEp)
        {
            Log.Warn($"[s{Id}] proxy-protocol header skipped for {target}: endpoint info unavailable");
            return true;
        }
        try
        {
            var header = ProxyProtocolV2.BuildHeader(clientEp, upstreamEp);
            await up.GetStream().WriteAsync(header, sessionStopToken).ConfigureAwait(false);
            Log.Trace($"[s{Id}] wrote PROXY v2 header ({header.Length}B) {clientEp} -> {upstreamEp} for {target}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"[s{Id}] PROXY v2 header write failed for {target}: {ex.Message}");
            return false;
        }
    }

    private void StartPumps()
    {
        pumpCts = CancellationTokenSource.CreateLinkedTokenSource(sessionStopToken);
        pumpC2S = PumpAsync("c->s", clientStream, upstream!.GetStream(), sniffC2S, isC2S: true, pumpCts.Token);
        pumpS2C = PumpAsync("s->c", upstream.GetStream(), clientStream, sniffS2C, isC2S: false, pumpCts.Token);
    }

    // The three exceptions a dying stream throws at a pump. Cancellation, a broken socket and a
    // socket disposed under us are three ways of being told the same thing, and the pump does the
    // same thing about all of them, so they are matched in one place rather than as repeated
    // catch triples on both the read and the write.
    private static bool IsStreamGone(Exception ex)
        => ex is OperationCanceledException or IOException or ObjectDisposedException;

    // The byte pump, one instance per direction. The direction flag stays: c->s and s->c differ
    // only in when the sniffer runs and what the exit is recorded against, and splitting them
    // would duplicate the whole read-forward-cancel skeleton in the busiest code in the proxy.
    //
    // Both awaits below are on the stream calls directly rather than behind helpers. Wrapping
    // either one would add an async state machine per chunk on a path that runs for every packet
    // of every player, which is the same reasoning that keeps BanStore.FindBlocking a loop.
    private async Task PumpAsync(string label, NetworkStream from, NetworkStream to, FrameSniffer? sniffer, bool isC2S, CancellationToken token)
    {
        var buf = new byte[cfg.Advanced.BufferSize];
        long total = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                int read;
                try { read = await from.ReadAsync(buf.AsMemory(0, buf.Length), token).ConfigureAwait(false); }
                catch (Exception ex) when (IsStreamGone(ex)) { break; }
                if (read <= 0) break;

                total += read;
                var chunk = buf.AsMemory(0, read);

                // c->s is inspected before the bytes are forwarded, so the gates and the initial
                // reservation get to act on the frame before any backend sees it.
                if (isC2S && !await InspectClientChunkAsync(sniffer, chunk).ConfigureAwait(false))
                    break;

                try { await to.WriteAsync(chunk, token).ConfigureAwait(false); }
                catch (Exception ex) when (IsStreamGone(ex)) { break; }

                // s->c is inspected after the forward instead: nothing here gates it, so the
                // player's frame is not made to wait on the parse.
                if (!isC2S)
                    sniffer?.OnBytes(chunk.Span);
            }
        }
        finally
        {
            RecordPumpExit(label, isC2S, total);
        }
    }

    // Everything the proxy does to a client chunk before it is allowed upstream. Returns false to
    // stop the pump, which is how both refusals here end the session.
    //
    // ValueTask, and not Task, on purpose: this runs for every c->s chunk, and the ordinary case
    // is the one with no await in it at all, where an async ValueTask method allocates nothing.
    // The state machine is only paid for on the first chunk that has to mint a reservation.
    private async ValueTask<bool> InspectClientChunkAsync(FrameSniffer? sniffer, ReadOnlyMemory<byte> chunk)
    {
        sniffer?.OnBytes(chunk.Span);

        // The gates normally fire before we dial a backend, but a client that stays quiet past
        // the first-frame read window (status.query_timeout_ms) gets here with the pumps already
        // running and its Identification in this very buffer. Forwarding it would let a refused
        // player's login reach the backend in the ~150ms before the forged disconnect closes us
        // down, so the pump has to drop the chunk itself rather than trust the pre-connect check.
        if (rejectedAtGate)
        {
            Log.Trace($"[s{Id}] dropping c->s chunk after gate rejection");
            return false;
        }

        if (initialReservationState == 0)
        {
            var mintFail = await EnsureInitialReservationAsync(currentBackend, "initial connect (stream)").ConfigureAwait(false);
            if (mintFail != null)
            {
                Log.Warn($"[s{Id}] closing session after reservation prime failed: {mintFail}");
                return false;
            }
        }

        return true;
    }

    // Close out one pump: bill the bytes to its direction and work out whether its exit means the
    // backend dropped a live player. Runs from PumpAsync's finally, so it runs on every exit path.
    private void RecordPumpExit(string label, bool isC2S, long total)
    {
        if (isC2S)
        {
            Interlocked.Add(ref c2sBytes, total);
            ProxyMetrics.AddBytes(total, 0);
        }
        else
        {
            Interlocked.Add(ref s2cBytes, total);
            ProxyMetrics.AddBytes(0, total);
        }
        Log.Trace($"[s{Id}] {label} pump exited ({total} bytes this segment)");

        // s->c pump exiting without our own Close() or a swap in flight means the backend
        // dropped the connection while the player was live.
        if (!isC2S && !closed && !swapping)
        {
            var ph = Phase;
            if (ph == SessionState.Phase.Ready || ph == SessionState.Phase.Disconnecting)
                kickedByBackend = true;
        }
    }

    private async Task<string?> EnsureInitialReservationAsync(BackendEndpoint? target, string reason)
    {
        if (initialReservationState != 0) return null;
        if (target == null)
            return null;
        if (registry == null || string.IsNullOrEmpty(target.ServerId))
        {
            initialReservationState = 1;
            return null;
        }
        if (string.IsNullOrEmpty(capturedPlayerUid))
            return null; // wait until Identification is captured

        var mintFail = await MintReservationIfPossibleAsync(target, registry, reason, cfg.Registry.FailOnError).ConfigureAwait(false);
        if (mintFail != null)
        {
            initialReservationState = 2;
            return mintFail;
        }

        initialReservationState = 1;
        return null;
    }

    // Await a task purely to know it has finished. Callers use this on pumps and on the forged
    // disconnect, both of which already log and handle their own failures on the way out.
    private static async Task SafeAwait(Task t) { try { await t.ConfigureAwait(false); } catch { /* the task reported for itself */ } }

    private static string FormatDuration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h{t.Minutes:D2}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m{t.Seconds:D2}s";
        return $"{(int)t.TotalSeconds}s";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1}MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:F1}KB";
        return $"{bytes}B";
    }
}
