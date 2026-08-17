using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// The background loop that turns a queued transfer intent into a moved player. It runs against
/// a real ProxyListener session table holding real sessions, with a scripted registry feeding it
/// intents, so what a test asserts is the outcome an operator sees: this player moved, that one
/// did not.
///
/// Two things make this loop worth pinning. Every proxy on a network drains the same queue, so
/// an intent for a uid this proxy does not hold has to be dropped silently rather than acted on
/// or retried. And the loop is the only thing standing between a registry that has gone quiet
/// and a proxy that stops dispatching transfers for the rest of its uptime, so a failed poll has
/// to be counted and survived rather than thrown out of the loop.
///
/// A dispatch is observed through the reservation it mints. Reaching the mint means the intent
/// matched a session, resolved a live target and got as far as asking the registry to let the
/// player in, which is the whole of what the dispatcher is responsible for.
/// </summary>
public class TransferIntentDispatcherTests
{
    private const string Uid = "uid-1";

    /// <summary>An admin harness with a dispatcher running behind it. The poll period is floored
    /// at 250ms inside the dispatcher, so the tests wait rather than tick a clock.</summary>
    private sealed class Dispatching : IAsyncDisposable
    {
        public required AdminHarness Harness { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task Loop { get; init; }
        public FakeRegistryClient Registry => Harness.Registry;

        public static Task<Dispatching> StartAsync(params string[] serverIds)
            => StartAsync((Action<ProxyConfig>?)null, serverIds);

        public static async Task<Dispatching> StartAsync(Action<ProxyConfig>? configure, params string[] serverIds)
        {
            var harness = await AdminHarness.StartAsync(
                cfg =>
                {
                    cfg.Registry.TransferIntentPollMs = 1;
                    configure?.Invoke(cfg);
                },
                serverIds: serverIds.Length == 0 ? new[] { "hub" } : serverIds);
            var cts = new CancellationTokenSource();
            var dispatcher = new TransferIntentDispatcher(harness.Cfg, harness.Registry,
                harness.Proxy.Sessions, cts.Token);
            return new Dispatching { Harness = harness, Cts = cts, Loop = Task.Run(dispatcher.RunAsync) };
        }

        /// <summary>Registers a target the dispatcher can resolve. Live and out of maintenance
        /// unless the test says otherwise.</summary>
        public void Target(string serverId, bool stale = false, bool maintenance = false)
            => Registry.Backends[serverId] = new BackendSnapshot
            {
                ServerId = serverId,
                DisplayName = serverId,
                PublicHost = "10.0.0.9",
                PublicPort = 42421,
                Stale = stale,
                Maintenance = maintenance,
            };

        /// <summary>Mints made before the intents were queued. Routing a player to their first
        /// backend mints a reservation of its own, so the joins in a test's setup leave marks in
        /// the same list the dispatch does.</summary>
        private int baseline;

        /// <summary>Queues intents, after drawing a line under the setup's own mints.</summary>
        public void Queue(params TransferIntent[] intents)
        {
            baseline = Registry.MintsSoFar().Count;
            Registry.Intents = intents.ToList();
        }

        /// <summary>Only the mints the dispatcher is responsible for.</summary>
        public List<FakeRegistryClient.MintCall> Dispatched()
            => Registry.MintsSoFar().Skip(baseline).ToList();

        /// <summary>Waits for the loop to have drained at least <paramref name="count"/> more
        /// times, which is the point after which "nothing happened" means the dispatcher decided
        /// so rather than that the test looked too early.</summary>
        public Task SettleAsync(int drains = 4)
        {
            int target = Registry.Drains + drains;
            return AdminHarness.WaitFor(() => Registry.Drains >= target,
                $"the dispatcher never reached {target} drains (got {Registry.Drains})");
        }

        public Task DispatchesAtLeast(int count)
            => AdminHarness.WaitFor(() => Dispatched().Count >= count,
                $"the dispatcher never minted {count} reservations (got {Dispatched().Count})");

        public Task FailureReportsAtLeast(int count)
            => AdminHarness.WaitFor(() => Registry.FailureReportsSoFar().Count >= count,
                $"the dispatcher never reported {count} failures (got {Registry.FailureReportsSoFar().Count})");

        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            try { await Loop.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* shutting down */ }
            Cts.Dispose();
            await Harness.DisposeAsync();
        }
    }

    private static TransferIntent Intent(string uid = Uid, string target = "creative",
        string mode = "redirect", string? reason = null, string clientTransferId = "")
        => new()
        {
            Id = "intent-1",
            PlayerUid = uid,
            PlayerName = "alice",
            SourceServerId = "source",
            TargetServerId = target,
            Mode = mode,
            Reason = reason,
            ClientTransferId = clientTransferId,
        };

    // ---- the happy path ----

    [Fact]
    public async Task AnIntentForALiveSession_MovesThatPlayerToTheNamedTarget()
    {
        await using var d = await Dispatching.StartAsync();
        await d.Harness.JoinAsync(Uid, "alice");
        d.Target("creative");
        d.Queue(Intent(reason: "event start", clientTransferId: "transfer-9"));

        await d.DispatchesAtLeast(1);

        var mint = Assert.Single(d.Dispatched());
        Assert.Equal(Uid, mint.PlayerUid);
        Assert.Equal("creative", mint.TargetServerId);
        Assert.Equal("event start", mint.Reason);
        // The far side commits the seamless handoff on this id, so an intent that dropped it
        // would arrive as an unrelated join.
        Assert.Equal("transfer-9", mint.ClientTransferId);
    }

    [Fact]
    public async Task AnIntentPicksTheOneSessionItNames_AndLeavesTheOthersWhereTheyAre()
    {
        await using var d = await Dispatching.StartAsync();
        await d.Harness.JoinAsync(Uid, "alice");
        await d.Harness.JoinAsync("uid-2", "bob");
        await d.Harness.JoinAsync("uid-3", "carol");
        d.Target("creative");
        d.Queue(Intent());

        await d.DispatchesAtLeast(1);
        await d.SettleAsync(3);

        // Three players on the proxy, one named in the intent. Moving the wrong one, or all of
        // them, is the failure this guards.
        var mint = Assert.Single(d.Dispatched());
        Assert.Equal(Uid, mint.PlayerUid);
    }

    [Fact]
    public async Task AUidThatDiffersOnlyInCase_StillMatchesTheSession()
    {
        await using var d = await Dispatching.StartAsync();
        await d.Harness.JoinAsync("UID-MixedCase", "alice");
        d.Target("creative");
        d.Queue(Intent(uid: "uid-mixedcase"));

        await d.DispatchesAtLeast(1);

        // Uids arrive from different sources with different casing, and a case-sensitive compare
        // here would drop the intent as "no live session" and leave the player standing. The mint
        // carries the session's spelling, not the intent's, because that is the one the backend
        // will match the arriving player against.
        Assert.Equal("UID-MixedCase", Assert.Single(d.Dispatched()).PlayerUid);
    }

    [Fact]
    public async Task SeveralIntentsInOneDrain_AreEachDispatched()
    {
        await using var d = await Dispatching.StartAsync();
        await d.Harness.JoinAsync(Uid, "alice");
        await d.Harness.JoinAsync("uid-2", "bob");
        d.Target("creative");
        d.Queue(Intent(), Intent(uid: "uid-2", target: "creative"));

        await d.DispatchesAtLeast(2);

        // A drain hands back a batch, and an operator sending a whole event's worth of players
        // at once must not have all but the first quietly dropped.
        var moved = d.Dispatched().Select(m => m.PlayerUid).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "uid-1", "uid-2" }, moved);
    }

    // ---- intents this proxy must not act on ----

    [Fact]
    public async Task AnIntentForAUidThisProxyDoesNotHold_IsDroppedWithoutTouchingTheRegistry()
    {
        await using var d = await Dispatching.StartAsync();
        await d.Harness.JoinAsync(Uid, "alice");
        d.Target("creative");
        d.Queue(Intent(uid: "uid-on-another-proxy"));

        await d.SettleAsync(4);

        // Every proxy on the network drains the same queue, so an intent for somebody else's
        // player has to be dropped rather than resolved, minted for, or retried. Resolving it
        // would put every proxy on the network on the registry for every transfer anyone makes.
        Assert.Empty(d.Dispatched());
        Assert.Equal(0, d.Registry.Resolves);
    }

    [Fact]
    public async Task ASeamlessIntentForAUidThisProxyDoesNotHold_ReportsOneFailure()
    {
        await using var d = await Dispatching.StartAsync();
        await d.Harness.JoinAsync(Uid, "alice");
        d.Queue(Intent(uid: "uid-on-another-proxy", mode: "seamless", clientTransferId: "transfer-1"));

        await d.FailureReportsAtLeast(1);

        var failure = Assert.Single(d.Registry.FailureReportsSoFar());
        Assert.Equal("transfer-1", failure.ClientTransferId);
        Assert.Contains("not connected", failure.Reason);
    }

    [Theory]
    [InlineData("", "creative")]
    [InlineData(Uid, "")]
    public async Task AnIntentMissingHalfOfItsSubject_IsDroppedBeforeAnythingIsResolved(
        string uid, string target)
    {
        await using var d = await Dispatching.StartAsync();
        await d.Harness.JoinAsync(Uid, "alice");
        d.Target("creative");
        d.Queue(Intent(uid: uid, target: target));

        await d.SettleAsync(4);

        // A half-filled intent is turned away up front, before the dispatcher goes looking for a
        // backend. The uid-less case matters most: matching on an empty uid against a session
        // table is one OrdinalIgnoreCase slip away from moving whoever happens to be first.
        Assert.Empty(d.Dispatched());
        Assert.Equal(0, d.Registry.Resolves);
    }

    [Fact]
    public async Task AnIntentForABackendTheRegistryCannotResolve_MovesNobody()
    {
        await using var d = await Dispatching.StartAsync();
        await d.Harness.JoinAsync(Uid, "alice");
        // No Target() call: the registry has never heard of "creative".
        d.Queue(Intent());

        await d.SettleAsync(4);

        // Sending the player at an address the registry cannot name would disconnect them into
        // nothing, which is worse than leaving them where they are.
        Assert.True(d.Registry.Resolves > 0);
        Assert.Empty(d.Dispatched());
    }

    [Fact]
    public async Task ASeamlessIntentForAnUnknownBackend_ReportsOneFailure()
    {
        await using var d = await Dispatching.StartAsync();
        await d.Harness.JoinAsync(Uid, "alice");
        d.Queue(Intent(mode: "seamless", clientTransferId: "transfer-unknown"));

        await d.FailureReportsAtLeast(1);

        var failure = Assert.Single(d.Registry.FailureReportsSoFar());
        Assert.Equal("transfer-unknown", failure.ClientTransferId);
        Assert.Contains("unavailable", failure.Reason);
    }

    [Fact]
    public async Task AnIntentForABackendThatStoppedHeartbeating_MovesNobody()
    {
        await using var d = await Dispatching.StartAsync();
        await d.Harness.JoinAsync(Uid, "alice");
        d.Target("creative", stale: true);
        d.Queue(Intent());

        await d.SettleAsync(4);

        // Stale means the backend has missed its heartbeats. Its last known address is probably
        // still routable, which is exactly what makes moving somebody onto it a bad bet. The
        // session matched and the target resolved, so this is the staleness check refusing and
        // not an earlier guard.
        Assert.True(d.Registry.Resolves > 0);
        Assert.Empty(d.Dispatched());
    }

    [Fact]
    public async Task AnIntentForABackendInMaintenance_MovesNobody()
    {
        await using var d = await Dispatching.StartAsync();
        await d.Harness.JoinAsync(Uid, "alice");
        d.Target("creative", maintenance: true);
        d.Queue(Intent());

        await d.SettleAsync(4);

        // Maintenance is an operator saying "send me nobody", and an intent queued before the
        // flag went up must not walk past it.
        Assert.True(d.Registry.Resolves > 0);
        Assert.Empty(d.Dispatched());
    }

    [Theory]
    [InlineData(true, false, "stale")]
    [InlineData(false, true, "maintenance")]
    public async Task ASeamlessIntentForAnUnavailableBackend_ReportsOneFailure(
        bool stale, bool maintenance, string expectedReason)
    {
        await using var d = await Dispatching.StartAsync();
        await d.Harness.JoinAsync(Uid, "alice");
        d.Target("creative", stale, maintenance);
        d.Queue(Intent(mode: "seamless", clientTransferId: "transfer-unavailable"));

        await d.FailureReportsAtLeast(1);

        var failure = Assert.Single(d.Registry.FailureReportsSoFar());
        Assert.Equal("transfer-unavailable", failure.ClientTransferId);
        Assert.Contains(expectedReason, failure.Reason);
    }

    [Fact]
    public async Task ASeamlessDispatchThatCannotMintAReservation_ReportsOneFailure()
    {
        await using var d = await Dispatching.StartAsync();
        d.Harness.Cfg.Transfers.AllowSeamless = true;
        d.Harness.Cfg.Transfers.RequireSeamlessCapability = false;
        var player = await d.Harness.JoinAsync(Uid, "alice");
        await AdminHarness.ReachReadyAsync(player);
        d.Target("creative");
        d.Registry.FailMint = true;
        d.Queue(Intent(mode: "seamless", clientTransferId: "transfer-mint-failed"));

        await d.FailureReportsAtLeast(1);

        var failure = Assert.Single(d.Registry.FailureReportsSoFar());
        Assert.Equal("transfer-mint-failed", failure.ClientTransferId);
        Assert.Equal("registry mint failed", failure.Reason);
    }

    [Fact]
    public async Task ASeamlessDispatchThatNeverReachesReady_ReportsOneFailure()
    {
        await using var d = await Dispatching.StartAsync(
            cfg => cfg.Registry.SeamlessReadyWaitTimeoutSeconds = 1);
        await d.Harness.JoinAsync(Uid, "alice");
        d.Target("creative");
        d.Queue(Intent(mode: "seamless", clientTransferId: "transfer-timeout"));

        await d.FailureReportsAtLeast(1);

        var failure = Assert.Single(d.Registry.FailureReportsSoFar());
        Assert.Equal("transfer-timeout", failure.ClientTransferId);
        Assert.Contains("timed out", failure.Reason);
    }

    [Fact]
    public async Task ADispatchException_ReportsOneFailure()
    {
        await using var d = await Dispatching.StartAsync();
        d.Harness.Cfg.Transfers.AllowSeamless = true;
        d.Harness.Cfg.Transfers.RequireSeamlessCapability = false;
        var player = await d.Harness.JoinAsync(Uid, "alice");
        await AdminHarness.ReachReadyAsync(player);
        d.Target("creative");
        d.Registry.ThrowMint = true;
        d.Queue(Intent(mode: "seamless", clientTransferId: "transfer-exception"));

        await d.FailureReportsAtLeast(1);

        var failure = Assert.Single(d.Registry.FailureReportsSoFar());
        Assert.Equal("transfer-exception", failure.ClientTransferId);
        Assert.Contains("failed to dispatch", failure.Reason);
    }

    [Fact]
    public async Task ARejectedFailureNotice_IsLoggedAndRetainedForInspection()
    {
        await using var d = await Dispatching.StartAsync();
        await d.Harness.JoinAsync(Uid, "alice");
        d.Registry.FailFailureReport = true;
        d.Queue(Intent(uid: "uid-on-another-proxy", mode: "seamless", clientTransferId: "transfer-rejected"));

        await d.FailureReportsAtLeast(1);

        Assert.Equal("transfer-rejected", Assert.Single(d.Registry.FailureReportsSoFar()).ClientTransferId);
    }

    [Fact]
    public async Task AFailureNoticeException_IsSwallowedByTheDispatcher()
    {
        await using var d = await Dispatching.StartAsync();
        await d.Harness.JoinAsync(Uid, "alice");
        d.Registry.ThrowFailureReport = true;
        d.Queue(Intent(uid: "uid-on-another-proxy", mode: "seamless", clientTransferId: "transfer-report-error"));

        await d.FailureReportsAtLeast(1);

        Assert.Equal("transfer-report-error", Assert.Single(d.Registry.FailureReportsSoFar()).ClientTransferId);
    }

    [Fact]
    public async Task ARedirectIntentDoesNotCreateASeamlessFailureNotice()
    {
        await using var d = await Dispatching.StartAsync();
        await d.Harness.JoinAsync(Uid, "alice");
        d.Queue(Intent(clientTransferId: "redirect-id"));

        await d.SettleAsync(4);

        Assert.Empty(d.Registry.FailureReportsSoFar());
    }

    // ---- a registry that goes quiet ----

    [Fact]
    public async Task APollThatFails_IsCountedAndTheLoopKeepsRunning()
    {
        await using var d = await Dispatching.StartAsync();
        await d.Harness.JoinAsync(Uid, "alice");
        d.Target("creative");

        d.Registry.Throw = true;
        await d.SettleAsync(3);

        // A registry restart or a network blip must not take the dispatcher down for the rest of
        // the proxy's uptime: the loop is the only thing that will ever pick the queue back up.
        d.Registry.Throw = false;
        d.Queue(Intent());
        await d.DispatchesAtLeast(1);
        Assert.Equal(Uid, Assert.Single(d.Dispatched()).PlayerUid);
    }

    [Fact]
    public async Task APollThatFails_ShowsUpInTheMetricsAnOperatorWatches()
    {
        await using var d = await Dispatching.StartAsync();
        long before = PollFailures();

        d.Registry.Throw = true;
        await d.SettleAsync(2);
        await AdminHarness.WaitFor(() => PollFailures() > before,
            "the failed polls never reached nimbus_proxy_registry_intent_poll_failures_total");

        // Swallowing the exception is right, but swallowing it silently would leave a registry
        // that has been unreachable for an hour looking exactly like a quiet network.
        Assert.True(PollFailures() > before);
    }

    /// <summary>Reads the poll-failure counter out of the Prometheus text. The counter is process
    /// wide, so the assertions above compare against a baseline rather than an absolute.</summary>
    private static long PollFailures()
    {
        foreach (string line in ProxyMetrics.RenderPrometheus().Split('\n'))
        {
            if (!line.StartsWith("nimbus_proxy_registry_intent_poll_failures_total ", StringComparison.Ordinal))
                continue;
            return long.Parse(line.Split(' ')[1].Trim());
        }
        Assert.Fail("nimbus_proxy_registry_intent_poll_failures_total is not in the metrics output");
        return 0;
    }
}
