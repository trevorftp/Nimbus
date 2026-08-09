using System.Net.Sockets;

namespace Nimbus.Proxy;

internal sealed class ClientSessionRunner
{
    private readonly BackendRouter router;
    private readonly EventBus events;
    private readonly ServerStatusResponder statusResponder;
    private readonly StickyRouteTable stickies;
    private readonly ProxyConfig cfg;
    private readonly CancellationToken stopToken;
    private readonly BanCache? bans;
    private readonly WhitelistCache? whitelist;

    // Eight distinct collaborators, deliberately left as explicit parameters (S107 suppressed). They
    // are not one concept the way ProxySession's SessionServices are: the router, the event bus, the
    // status responder, the sticky table, the config and the stop token are unrelated subsystems, and
    // the only pair with anything in common (the ban and whitelist caches) is two arguments, too few
    // to earn a wrapper. SessionServices does not fit either: it carries a registry and UDP overrides
    // this runner never takes, and lacks the router and status responder it needs. Wrapping any
    // subset here would hide honest wiring behind a container invented only to lower a count.
    public ClientSessionRunner(BackendRouter router, EventBus events, ServerStatusResponder statusResponder, // NOSONAR
        StickyRouteTable stickies, ProxyConfig cfg, CancellationToken stopToken, BanCache? bans = null,
        WhitelistCache? whitelist = null)
    {
        this.router = router;
        this.events = events;
        this.statusResponder = statusResponder;
        this.stickies = stickies;
        this.cfg = cfg;
        this.stopToken = stopToken;
        this.bans = bans;
        this.whitelist = whitelist;
    }

    public async Task RunAsync(ProxySession session, TcpClient client)
    {
        try
        {
            var firstFrame = await TryReadFirstFrameAsync(client).ConfigureAwait(false);
            if (firstFrame.Length > 0 && await statusResponder.TryHandleAsync(client, firstFrame).ConfigureAwait(false))
                return;

            var connectEvt = new PlayerConnectEvent(session);
            await events.FireAsync(connectEvt).ConfigureAwait(false);
            if (connectEvt.IsDenied)
            {
                Log.Info($"[s{session.Id}] connection denied by handler: {connectEvt.DenyReason}");
                await RefuseAsync(client, connectEvt.DenyReason ?? "connection refused").ConfigureAwait(false);
                return;
            }

            IReadOnlyList<BackendEndpoint> ordered = Array.Empty<BackendEndpoint>();
            string? selectReason = null;
            if (TryConsumeStickyRoute(session, firstFrame, out var stickyTarget))
            {
                ordered = new[] { stickyTarget };
            }
            else
            {
                using var selectCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
                selectCts.CancelAfter(TimeSpan.FromSeconds(5));
                (ordered, selectReason) = await router.SelectOrderedAsync(selectCts.Token).ConfigureAwait(false);
            }

            var firstChoice = ordered.Count == 0 ? null : ordered[0].ToServerInfo();
            var chooseEvt = new PlayerChooseInitialServerEvent(session, firstChoice);
            await events.FireAsync(chooseEvt).ConfigureAwait(false);
            if (chooseEvt.IsCancelled)
            {
                Log.Info($"[s{session.Id}] initial connect cancelled by handler: {chooseEvt.CancelReason}");
                await RefuseAsync(client, chooseEvt.CancelReason ?? "connection cancelled").ConfigureAwait(false);
                return;
            }

            if (chooseEvt.Target is ServerInfo overrideSi &&
                (firstChoice == null
                 || !string.Equals(overrideSi.Host, firstChoice.Host, StringComparison.OrdinalIgnoreCase)
                 || overrideSi.Port != firstChoice.Port
                 || !string.Equals(overrideSi.ServerId, firstChoice.ServerId, StringComparison.OrdinalIgnoreCase)))
            {
                ordered = new[] { overrideSi.ToEndpoint() };
            }

            if (ordered.Count == 0)
            {
                Log.Warn($"[s{session.Id}] no healthy backend: {selectReason}; sending forged disconnect");
                await RefuseAsync(client, $"No backend available right now ({selectReason}). Please try again shortly.").ConfigureAwait(false);
                return;
            }

            await session.RunAsync(ordered, firstFrame).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"[s{session.Id}] session crashed: {ex.GetType().Name}: {ex.Message}");
            // The crash is logged above and this listener has to keep accepting. A close that
            // throws on top of it means the socket is already gone, which is the goal anyway.
            try { client.Close(); } catch { /* already gone */ }
        }
    }

    // Resolve the backend a redirect transfer staged for this reconnect, before any upstream is
    // opened. Routing has to be decided here: the mp token the client is about to present is
    // single use, so the backend that answers its token query is the only one that can validate
    // it, and moving the session afterwards costs the player a kick.
    //
    // A client that sends Identification first is matched on its UID. A stock client is not: its
    // first frame is LoginTokenQuery, which carries no identity, and a client slow enough to say
    // nothing inside the first-frame window gives us no frame at all. Both fall back to the
    // client address the redirect path staged alongside the UID.
    private bool TryConsumeStickyRoute(ProxySession session, ReadOnlySpan<byte> firstFrame, out BackendEndpoint target)
    {
        target = default!;

        if (firstFrame.Length > 0 && IdentificationParser.TryExtract(firstFrame, out var uid, out _))
        {
            if (!stickies.TryConsume(uid, out var known)) return false;
            if (IsBannedFrom(uid, known.Target))
            {
                Log.Warn($"[s{session.Id}] sticky route to {known.Target} dropped: uid {uid} is banned from it; routing normally instead");
                return false;
            }
            if (LacksCoverageFor(uid, known.Target))
            {
                Log.Warn($"[s{session.Id}] sticky route to {known.Target} dropped: uid {uid} is not whitelisted on it; routing normally instead");
                return false;
            }
            target = known.Target;
            Log.Trace($"[s{session.Id}] sticky route by uid {uid} -> {known.Target} ({known.Reason})");
            return true;
        }

        string ip = session.ClientRemote;
        if (!stickies.TryConsumeByClientIp(ip, out var route)) return false;
        // Matched on the address, so the UID here is the one the route was staged for rather than
        // a confirmed identity. It is still the right one to check: the route exists to carry
        // that player, and a backend they are banned from is not where it may take anyone.
        if (IsBannedFrom(route.Uid, route.Target))
        {
            Log.Warn($"[s{session.Id}] sticky route to {route.Target} dropped: uid {route.Uid} is banned from it; routing normally instead");
            return false;
        }
        if (LacksCoverageFor(route.Uid, route.Target))
        {
            Log.Warn($"[s{session.Id}] sticky route to {route.Target} dropped: uid {route.Uid} is not whitelisted on it; routing normally instead");
            return false;
        }
        target = route.Target;
        // The player behind this route is a guess until Identification turns up. The session
        // checks it then and puts the route back if we handed it to the wrong person.
        session.NoteRoutedByStickyIp(route);
        Log.Info($"[s{session.Id}] sticky route by client ip {ip} -> {route.Target} ({route.Reason}), expecting uid {route.Uid}");
        return true;
    }

    // A route staged before the ban landed must not carry the player onto a backend that is now
    // closed to them. The route is discarded rather than re-staged, and default routing takes
    // over; the connection gate is the backstop if that lands them somewhere banned too. A target
    // with no ServerId carries no scope a ban could be matched against.
    private bool IsBannedFrom(string uid, BackendEndpoint target)
        => bans != null && !string.IsNullOrEmpty(target.ServerId) && bans.FindBlocking(uid, target.ServerId) != null;

    // Same discard for a target that requires a whitelist entry this uid does not hold. The
    // player falls back to normal routing rather than being refused outright, which is what the
    // per-server scope is for. A cold cache counts as no coverage unless the operator asked for
    // the open door, matching the connection gate.
    private bool LacksCoverageFor(string uid, BackendEndpoint target)
    {
        if (!cfg.Whitelist.RequiresCoverage(target.ServerId)) return false;
        if (whitelist == null || !whitelist.HasSynced) return !cfg.Whitelist.FailOpenUntilFirstSync;
        return whitelist.FindCovering(uid, target.ServerId) == null;
    }

    // Tell the client why it is not getting in, then drop it. Every refusal in RunAsync ends
    // this way, so the close lives here rather than being repeated at each call site.
    private static async Task RefuseAsync(TcpClient client, string message)
    {
        try
        {
            var frame = DisconnectBuilder.BuildDisconnectFrame(message);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.GetStream().WriteAsync(frame, cts.Token).ConfigureAwait(false);
            await client.GetStream().FlushAsync(cts.Token).ConfigureAwait(false);
            // Breathing room for the client to render the reason before the socket goes.
            try { await Task.Delay(150, cts.Token).ConfigureAwait(false); } catch { /* 2s budget spent, close now */ }
        }
        catch { /* the reason is a courtesy; a client that already left still gets refused */ }

        try { client.Close(); } catch { /* the write path above may have closed it already */ }
    }

    private async Task<byte[]> TryReadFirstFrameAsync(TcpClient client)
    {
        var stream = client.GetStream();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(100, cfg.Status.QueryTimeoutMs)));

        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, cts.Token).ConfigureAwait(false))
            return Array.Empty<byte>();

        int rawLen = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
        int len = rawLen & 0x7FFFFFFF;
        if (len == 0)
            return header;
        if (len > 256 * 1024 * 1024)
            throw new InvalidDataException($"client frame too large: {len} bytes");

        var frame = new byte[4 + len];
        Buffer.BlockCopy(header, 0, frame, 0, 4);
        if (!await ReadExactAsync(stream, frame.AsMemory(4, len), cts.Token).ConfigureAwait(false))
            return Array.Empty<byte>();

        return frame;
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read;
            try { read = await stream.ReadAsync(buffer.Slice(offset), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
            catch (IOException) { return false; }
            catch (ObjectDisposedException) { return false; }
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }
}
