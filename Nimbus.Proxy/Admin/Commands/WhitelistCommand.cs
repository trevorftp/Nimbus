using Nimbus.Shared.Models;

namespace Nimbus.Proxy;

// Network whitelist, held by the registry so one entry covers every proxy. Shaped like the ban
// commands: `player` resolves against live sessions because operators know names, `uid` lists
// someone who is not currently connected.
//
// Adding an entry never turns enforcement on. That switch is [whitelist] in nimbus.proxy.toml,
// and it has to be, because the registry cannot know which backends a given proxy is gating.
internal sealed class WhitelistAddCommand : IAdminCommand
{
    public string Name => "whitelist-add";
    public string Permission => "nimbus.command.whitelist.add";
    public string Summary => "whitelist a player across the network, or on one backend";
    public string Usage => "whitelist-add (--uid <uid> | --player <name>) [--server <serverId>] [--duration <seconds>] [--note <text>]";

    public async Task<object> ExecuteAsync(AdminContext ctx)
    {
        if (ctx.Proxy.Registry == null)
            return new { ok = false, reason = "whitelists need a registry (registry.mode is 'disabled')" };

        var req = ctx.Request;
        string uid = req.OptionalString("uid") ?? "";
        string name = req.OptionalString("player") ?? "";
        ProxySession? online = null;

        if (string.IsNullOrEmpty(uid))
        {
            if (string.IsNullOrEmpty(name))
                return AdminCommandError.Usage(this, "need either uid or player");

            // A live player resolves to their uid immediately, exactly as before. An offline one
            // no longer fails: the entry is stored pending on the name alone (#104), and the gate
            // binds it to their uid the first time they connect. The uid stays empty here, which is
            // what routes the entry to the pending list.
            online = WhitelistLookup.ByName(ctx, name);
            if (online?.PlayerUid != null)
                uid = online.PlayerUid;
        }
        else
        {
            online = WhitelistLookup.ByUid(ctx, uid);
        }

        string serverId = req.OptionalString("serverId") ?? "";
        string note = req.OptionalString("note") ?? "";
        req.TryInt32("duration", out int duration);

        var request = new WhitelistRequest
        {
            PlayerUid = uid,
            PlayerName = !string.IsNullOrEmpty(name) ? name : online?.PlayerName ?? "",
            ServerId = serverId,
            Note = note,
            AddedBy = "admin",
            DurationSeconds = duration,
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.StopToken);
        cts.CancelAfter(TimeSpan.FromSeconds(ctx.Proxy.RegistryCfg.HttpTimeoutSeconds + 1));
        var entry = await ctx.Proxy.Registry.AddWhitelistAsync(request, cts.Token).ConfigureAwait(false);
        if (entry == null)
            return new { ok = false, reason = "registry refused the whitelist entry" };

        // Apply immediately rather than waiting for the next refresh, so the player's next join
        // attempt sees the entry.
        try { await ctx.Proxy.Whitelist.RefreshAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            // The entry is in the registry either way, so this only delays it reaching the gate
            // on this proxy. Worth saying out loud: the operator who just ran the command is
            // probably watching someone try to join.
            Log.Warn($"whitelist cache refresh failed after listing {uid}: {ex.GetType().Name}: {ex.Message}; the gate picks it up on the next poll");
        }

        return new
        {
            ok = true,
            uid = entry.PlayerUid,
            player = entry.PlayerName,
            scope = entry.IsNetworkWide ? "network" : entry.ServerId,
            // Which of the two things happened. A pending entry was stored on the name because the
            // player is not connected; it binds to their uid on first join. A bound entry is the
            // ordinary uid-keyed one, either because a uid was given or because the player is live.
            pending = entry.IsPending,
            status = entry.IsPending ? "pending" : "bound",
            expiresAtUnix = entry.ExpiresAtUnix,
            enforcing = ctx.Cfg.Whitelist.Enabled,
        };
    }
}

internal sealed class WhitelistRemoveCommand : IAdminCommand
{
    public string Name => "whitelist-remove";
    public string Permission => "nimbus.command.whitelist.remove";
    public string Summary => "drop a whitelist entry and disconnect whoever loses access";
    public string Usage => "whitelist-remove --uid <uid> [--server <serverId>]";

    public async Task<object> ExecuteAsync(AdminContext ctx)
    {
        if (ctx.Proxy.Registry == null)
            return new { ok = false, reason = "whitelists need a registry (registry.mode is 'disabled')" };

        if (!ctx.Request.TryString("uid", out var uid))
            return AdminCommandError.Missing(this, "uid");

        // Scoped entries are removed with the serverId they were created with; omitting it drops
        // the network-wide one.
        string serverId = ctx.Request.OptionalString("serverId") ?? "";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.StopToken);
        cts.CancelAfter(TimeSpan.FromSeconds(ctx.Proxy.RegistryCfg.HttpTimeoutSeconds + 1));
        bool removed = await ctx.Proxy.Registry.RemoveWhitelistAsync(uid, serverId, cts.Token).ConfigureAwait(false);
        if (!removed)
            return new { ok = false, uid, scope = string.IsNullOrEmpty(serverId) ? "network" : serverId };

        // The sweep below asks the cache who is still covered, so the removal has to be in the
        // cache before it runs. Whether it got there decides how the sweep reads a "still
        // covered" answer, so the outcome is carried rather than assumed.
        bool cacheHasRemoval;
        try { cacheHasRemoval = await ctx.Proxy.Whitelist.RefreshAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            cacheHasRemoval = false;
            Log.Warn($"whitelist cache refresh failed after removing {uid}: {ex.GetType().Name}: {ex.Message}");
        }

        int kicked = KickSessionsLosingCoverage(ctx, uid, cacheHasRemoval);

        return new
        {
            ok = true,
            uid,
            scope = string.IsNullOrEmpty(serverId) ? "network" : serverId,
            kicked,
            // False means the sweep above ran against a list this removal had not reached, so it
            // erred towards kicking. The registry still holds the removal.
            cacheRefreshed = cacheHasRemoval,
        };
    }

    // Removing an entry can close a door the player is already standing behind. Which of their
    // sessions that is depends on the backend each one sits on and on what coverage is left, so
    // this walks the session table rather than reasoning from the removed entry. Kept apart from
    // the command body because deciding who loses access is its own concern, distinct from talking
    // to the registry and the cache above it. cacheHasRemoval carries whether the sweep may trust
    // a "still covered" answer: on a stale list the entry that just went away is still sitting
    // there and would spare every session it used to cover, which is how this command came to
    // report a kick count of zero while the player stayed connected. Kicking someone who turns out
    // to hold other coverage costs them a reconnect; leaving them on costs the removal its point.
    private static int KickSessionsLosingCoverage(AdminContext ctx, string uid, bool cacheHasRemoval)
    {
        int kicked = 0;
        foreach (var session in ctx.Proxy.Sessions.Values)
        {
            if (!string.Equals(session.PlayerUid, uid, StringComparison.OrdinalIgnoreCase)) continue;

            // A session with no backend yet reports a null serverId, which only whitelist.network
            // gates.
            string? current = ((IPlayer)session).CurrentServer?.ServerId;
            if (!ctx.Cfg.Whitelist.RequiresCoverage(current)) continue;
            if (cacheHasRemoval && ctx.Proxy.Whitelist.FindCovering(uid, current) != null) continue;

            ((IPlayer)session).Disconnect(ctx.Cfg.Whitelist.Network
                ? "This network is whitelisted."
                : "This server is whitelisted.");
            kicked++;
        }
        return kicked;
    }
}

internal sealed class WhitelistListCommand : IAdminCommand
{
    public string Name => "whitelist-list";
    public IReadOnlyList<string> Aliases => new[] { "whitelist" };
    public string Permission => "nimbus.command.whitelist.list";
    public string Summary => "list active whitelist entries and where they are enforced";
    public string Usage => "whitelist-list";

    public async Task<object> ExecuteAsync(AdminContext ctx)
    {
        if (ctx.Proxy.Registry == null)
            return new { ok = false, reason = "whitelists need a registry (registry.mode is 'disabled')" };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.StopToken);
        cts.CancelAfter(TimeSpan.FromSeconds(ctx.Proxy.RegistryCfg.HttpTimeoutSeconds + 1));
        var entries = await ctx.Proxy.Registry.GetWhitelistAsync(cts.Token).ConfigureAwait(false);
        if (entries == null)
            return new { ok = false, reason = "registry unreachable" };

        return new
        {
            ok = true,
            count = entries.Count,
            // The list means nothing without the switches: an empty one with enforcement on is a
            // closed network, not an open one.
            network = ctx.Cfg.Whitelist.Network,
            servers = ctx.Cfg.Whitelist.Servers,
            synced = ctx.Proxy.Whitelist.HasSynced,
            entries = entries.ConvertAll(e => new
            {
                uid = e.PlayerUid,
                player = e.PlayerName,
                scope = e.IsNetworkWide ? "network" : e.ServerId,
                note = e.Note,
                addedBy = e.AddedBy,
                createdAtUnix = e.CreatedAtUnix,
                expiresAtUnix = e.ExpiresAtUnix,
            }),
        };
    }
}

internal static class WhitelistLookup
{
    public static ProxySession? ByName(AdminContext ctx, string name)
        => ctx.Proxy.Sessions.Values.FirstOrDefault(s => string.Equals(s.PlayerName, name, StringComparison.OrdinalIgnoreCase));

    public static ProxySession? ByUid(AdminContext ctx, string uid)
        => ctx.Proxy.Sessions.Values.FirstOrDefault(s => string.Equals(s.PlayerUid, uid, StringComparison.OrdinalIgnoreCase));
}
