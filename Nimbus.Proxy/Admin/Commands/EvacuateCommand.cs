namespace Nimbus.Proxy;

// Empties a backend of the players already on it, one swap at a time.
//
// Drain and evacuate are two halves of the same operation and neither implies the other, which
// is the kubernetes cordon/drain split under different names: drain is the cordon (stop sending
// new arrivals), evacuate is the eviction (move the ones already there). The pair is typed in
// that order, `nimctl drain hub` then `nimctl evacuate hub`, and a sweep run against a source
// that was never cordoned says so in its answer and in the log, because new joins can land on
// the source while the sweep is still walking it.
//
// Every session goes through the same RequestTransferAsync the swap command uses, so bans,
// whitelists, stale/maintenance refusals and the redirect-versus-seamless choice per client are
// decided per player exactly as they would be for a swap typed by hand. A player whose transfer
// is refused stays where they are and is reported as failed: evacuate never disconnects anyone,
// so running it again after fixing the cause is safe.
internal sealed class EvacuateCommand : IAdminCommand
{
    private const int DefaultPaceMs = 250;
    private const int MaxPaceMs = 5000;

    // The command answers when the sweep is done, so the sweep has to finish inside the time an
    // admin client will wait for a line. Sessions the budget does not reach are reported as
    // skipped rather than silently dropped, and a second evacuate picks them up.
    private static readonly TimeSpan SweepBudget = TimeSpan.FromSeconds(90);

    public string Name => "evacuate";
    public IReadOnlyList<string> Aliases => new[] { "evac" };
    public string Permission => "nimbus.command.evacuate";
    public string Summary => "move every session off a backend (drain cordons it, evacuate empties it)";
    public string Usage => "evacuate <serverId> [--to <serverId>] [--pace-ms <n>] [--reason \"...\"] " +
                           "(drain the source first: evacuate moves players, it does not stop new ones arriving)";

    public async Task<object> ExecuteAsync(AdminContext ctx)
    {
        var req = ctx.Request;
        if (!req.TryString("serverId", out var source))
            return AdminCommandError.Missing(this, "serverId");

        int paceMs = DefaultPaceMs;
        if (req.Has("paceMs") &&
            (!req.TryInt32("paceMs", out paceMs) || paceMs < 0 || paceMs > MaxPaceMs))
            return AdminCommandError.Usage(this, $"paceMs must be a whole number of milliseconds between 0 and {MaxPaceMs}");

        string mode = string.Equals(ctx.Cfg.Transfers.DefaultMode, "splice", StringComparison.OrdinalIgnoreCase)
            ? "seamless"
            : ctx.Cfg.Transfers.DefaultMode;
        if (!string.Equals(mode, "seamless", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "redirect", StringComparison.OrdinalIgnoreCase))
            return AdminCommandError.Usage(this, $"unknown transfer mode '{ctx.Cfg.Transfers.DefaultMode}' in transfers.default_mode");

        // Snapshot the table before the first transfer. Chasing sessions that arrive mid-sweep
        // would turn an undrained source into a loop the command never leaves; the warning below
        // is the honest answer instead.
        var onSource = ctx.Proxy.Sessions.Values
            .Where(s => string.Equals(s.CurrentBackend?.ServerId, source, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Id)
            .ToList();

        if (onSource.Count == 0 && ctx.Cfg.FindBackend(source) == null)
            return new
            {
                ok = false,
                reason = $"unknown serverId '{source}': not in the backend pool and no live session is on it",
                usage = Usage,
            };

        string? to = req.OptionalString("to") ?? req.OptionalString("targetServerId");
        var probes = new ProbeCache();
        var (fixedTarget, targetRefusal) = await ResolveFixedTargetAsync(ctx, to, source, probes).ConfigureAwait(false);
        if (targetRefusal != null) return targetRefusal;

        bool drained = ctx.Proxy.Router.IsDrained(source);
        string? warning = drained
            ? null
            : $"{source} is not drained, so new sessions can still be routed onto it while this sweep runs; " +
              $"run `nimctl drain {source}` first and then evacuate";
        if (warning != null) Log.Warn($"evacuate: {warning}");
        Log.Info($"evacuate: {onSource.Count} session(s) on {source} -> " +
                 $"{fixedTarget?.ServerId ?? "the router's choice"} at {paceMs}ms apart");

        var plan = new EvacuationPlan(source, fixedTarget, mode, req.OptionalString("reason"));
        var sweep = await SweepAsync(ctx, onSource, plan, probes, paceMs).ConfigureAwait(false);

        Log.Info($"evacuate: {source} done, {sweep.Moved} moved, {sweep.Failed} failed, {sweep.Skipped} skipped");

        return new
        {
            ok = true,
            source,
            target = fixedTarget?.ServerId,
            mode,
            paceMs,
            drained,
            warning,
            completed = sweep.Completed,
            counts = new
            {
                matched = onSource.Count,
                moved = sweep.Moved,
                failed = sweep.Failed,
                skipped = sweep.Skipped,
            },
            sessions = sweep.Entries,
        };
    }

    // With --to, the operator named where everyone goes. Resolve it once, up front, and refuse the
    // whole sweep before anyone is moved rather than discover a bad destination per player: it must
    // not be the source, it has to resolve the way swap resolves a name, and it has to answer a tcp
    // probe. No --to leaves fixedTarget null and the sweep asks the router per session.
    private async Task<(BackendEndpoint? target, object? refusal)> ResolveFixedTargetAsync(
        AdminContext ctx, string? to, string source, ProbeCache probes)
    {
        if (to == null) return (null, null);

        if (string.Equals(to, source, StringComparison.OrdinalIgnoreCase))
            return (null, AdminCommandError.Usage(this, $"target '{to}' is the source; evacuate needs somewhere else to put them"));

        var (resolved, refusal) = await ResolveTargetAsync(ctx, to).ConfigureAwait(false);
        if (resolved == null) return (null, new { ok = false, reason = refusal });
        if (!await probes.ReachableAsync(resolved).ConfigureAwait(false))
            return (null, new { ok = false, reason = $"target {resolved.Host}:{resolved.Port} unreachable (tcp probe)" });

        return (resolved, null);
    }

    private sealed record Sweep(int Moved, int Failed, int Skipped, bool Completed, List<object> Entries);

    // What the operator asked this evacuate to do: empty Source, send everyone to Target (or let the
    // router choose per session when it is null), using Mode, recording Reason. The four are fixed
    // for the whole run and were threaded verbatim through both the sweep and the per-session move,
    // so they travel as one value the way `nimctl evacuate <source> --to ... --reason ...` reads as
    // one instruction. Pace and the probe cache stay out: pacing is the sweep's own knob and the
    // cache is shared mutable state, neither belongs to the operator's plan.
    private sealed record EvacuationPlan(string Source, BackendEndpoint? Target, string Mode, string? Reason);

    private static async Task<Sweep> SweepAsync(AdminContext ctx, List<ProxySession> onSource,
        EvacuationPlan plan, ProbeCache probes, int paceMs)
    {
        var entries = new List<object>(onSource.Count);
        int moved = 0, failed = 0, skipped = 0;
        bool completed = true;
        bool anyAttempted = false;
        var deadline = DateTime.UtcNow + SweepBudget;

        foreach (var session in onSource)
        {
            string? halted = HaltReason(ctx, deadline);
            if (halted != null)
            {
                completed = false;
                skipped++;
                entries.Add(Entry(session, "skipped", null, null, $"not reached: {halted}, run evacuate again to continue"));
                continue;
            }

            // Nothing to move: the transfer paths need the captured Identification to mint a
            // reservation and to know who is being sent where.
            if (!session.HasIdentification)
            {
                skipped++;
                entries.Add(Entry(session, "skipped", null, null, $"no Identification captured yet (phase={session.Phase})"));
                continue;
            }

            // Spacing is between transfer starts, so the first one goes immediately and the
            // target's join pipeline sees arrivals rather than a herd.
            if (anyAttempted && paceMs > 0)
            {
                try { await Task.Delay(paceMs, ctx.StopToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* the halt check at the top of the next pass reports it */ }
            }
            anyAttempted = true;

            var move = await MoveSessionAsync(ctx, session, plan, probes).ConfigureAwait(false);
            entries.Add(move.Entry);
            if (move.Outcome == MoveOutcome.Moved) moved++;
            else failed++;
        }

        return new Sweep(moved, failed, skipped, completed, entries);
    }

    // Whether this pass of the sweep must stop before touching the session, and why, phrased for
    // the "skipped" line the caller writes. Cancellation wins over the budget because a shutdown is
    // the sharper reason to give. Null means carry on.
    private static string? HaltReason(AdminContext ctx, DateTime deadline)
        => ctx.StopToken.IsCancellationRequested
            ? "the proxy is shutting down"
            : DateTime.UtcNow >= deadline
                ? $"the sweep reached its {SweepBudget.TotalSeconds:0}s budget"
                : null;

    private enum MoveOutcome { Moved, Failed }

    private sealed record MoveResult(MoveOutcome Outcome, object Entry);

    // Move one session, running the target checks in the same order swap runs them: pick the target
    // (the fixed one, or ask the router afresh so capacity filling up mid-sweep sends later players
    // elsewhere), probe it, then hand it to the same RequestTransferAsync a hand-typed swap uses. A
    // refused transfer leaves the player where they are and is reported as failed; nobody is
    // disconnected.
    private static async Task<MoveResult> MoveSessionAsync(AdminContext ctx, ProxySession session,
        EvacuationPlan plan, ProbeCache probes)
    {
        var (target, targetRefusal) = plan.Target != null
            ? (plan.Target, null)
            : await ChooseTargetAsync(ctx, plan.Source).ConfigureAwait(false);
        if (target == null)
            return new MoveResult(MoveOutcome.Failed, Entry(session, "failed", null, null, targetRefusal ?? "no backend to move to"));

        if (!await probes.ReachableAsync(target).ConfigureAwait(false))
            return new MoveResult(MoveOutcome.Failed, Entry(session, "failed", target, null, $"target {target.Host}:{target.Port} unreachable (tcp probe)"));

        var (modeUsed, failReason) = await session.RequestTransferAsync(target, plan.Mode, ctx.Proxy.Registry,
            plan.Reason ?? $"evacuating {plan.Source}", ctx.Proxy.RegistryCfg.FailOnError).ConfigureAwait(false);
        return failReason == null
            ? new MoveResult(MoveOutcome.Moved, Entry(session, "moved", target, modeUsed, null))
            : new MoveResult(MoveOutcome.Failed, Entry(session, "failed", target, modeUsed, failReason));
    }

    // The same target checks swap makes, in the same order, so a name that swap would refuse is
    // refused here before anyone is moved rather than once per player.
    private static async Task<(BackendEndpoint? target, string? refusal)> ResolveTargetAsync(AdminContext ctx, string serverId)
    {
        if (ctx.Proxy.Registry != null)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.StopToken);
            cts.CancelAfter(TimeSpan.FromSeconds(ctx.Proxy.RegistryCfg.HttpTimeoutSeconds + 1));
            var b = await ctx.Proxy.Registry.ResolveByServerIdAsync(serverId, cts.Token).ConfigureAwait(false);
            if (b == null) return (null, $"unknown serverId '{serverId}' in registry");
            if (b.Stale) return (null, $"target '{serverId}' is stale (no recent heartbeat)");
            if (b.Maintenance) return (null, $"target '{serverId}' is in maintenance");
            return (new BackendEndpoint { Host = b.PublicHost, Port = b.PublicPort, ServerId = serverId }, null);
        }

        // With the registry off, the pool in [servers] is the only place a name resolves from.
        var configured = ctx.Cfg.FindBackend(serverId);
        return configured == null
            ? (null, $"unknown serverId '{serverId}': not in the backend pool and the registry is disabled")
            : (configured, null);
    }

    // No --to: each session goes wherever the router would send a fresh join, minus the backend
    // being emptied. Asked per session rather than once, so capacity the registry reports filling
    // up during the sweep moves later players along to the next candidate.
    private static async Task<(BackendEndpoint? target, string? refusal)> ChooseTargetAsync(AdminContext ctx, string source)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.StopToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var (ordered, noneReason) = await ctx.Proxy.Router.SelectOrderedAsync(cts.Token).ConfigureAwait(false);

        var candidate = ordered.FirstOrDefault(c => !string.Equals(c.ServerId, source, StringComparison.OrdinalIgnoreCase));
        if (candidate != null) return (candidate, null);

        return (null, ordered.Count == 0
            ? $"the router has no healthy backend ({noneReason})"
            : $"the only healthy backend the router offers is {source} itself");
    }

    private static object Entry(ProxySession session, string outcome, BackendEndpoint? target, string? mode, string? reason)
        => new
        {
            id = session.Id,
            player = session.PlayerName,
            uid = session.PlayerUid,
            outcome,
            target = target == null ? null : (string.IsNullOrEmpty(target.ServerId) ? target.ToString() : target.ServerId),
            mode,
            reason,
        };

    // One probe per distinct address for the whole sweep. Without it a hundred players moving to
    // the same backend would knock on its door a hundred times before the first of them arrives.
    private sealed class ProbeCache
    {
        private readonly Dictionary<string, bool> seen = new(StringComparer.Ordinal);

        public async Task<bool> ReachableAsync(BackendEndpoint target)
        {
            string key = $"{target.Host}:{target.Port}";
            if (seen.TryGetValue(key, out bool known)) return known;
            bool reachable = await BackendProbe.ReachableAsync(target.Host, target.Port, BackendProbe.DefaultTimeout)
                .ConfigureAwait(false);
            seen[key] = reachable;
            return reachable;
        }
    }
}
