using Nimbus.Registry.Services;
using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Registry.Core.Tests;

/// <summary>
/// The pending half of the whitelist (#104): entries listed by name for a player who has never
/// connected, kept apart from the uid-keyed list because they are keyed on the name instead, and
/// bound to a uid the first time the gate matches one. These pin the store seam the gate leans on,
/// the coexistence and replacement rules the keying has to give, and that a bind both settles the
/// entry and survives the process that made it.
/// </summary>
public sealed class WhitelistPendingStoreTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "nimbus-pending-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClock clock = new();

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* never created */ }
    }

    private WhitelistStore MemoryStore() => new(clock);

    private WhitelistStore FileStore() => new(clock,
        RegistryStateFiles.Whitelist(dir), RegistryStateFiles.WhitelistPending(dir));

    private static WhitelistEntry Pending(string name, string serverId = "")
        => new() { PlayerName = name, ServerId = serverId, AddedBy = "admin" };

    // ---- shape ----

    [Fact]
    public void ANameOnlyEntryIsPendingAndAUidEntryIsNot()
    {
        Assert.True(new WhitelistEntry { PlayerName = "builder" }.IsPending);
        Assert.False(new WhitelistEntry { PlayerUid = "uid-1", PlayerName = "builder" }.IsPending);
        // A row naming nobody at all is not pending either: there is no name to match it by.
        Assert.False(new WhitelistEntry().IsPending);
    }

    // ---- storage and lookup ----

    [Fact]
    public void APendingEntryIsFoundByNameAndScopeButNeverByUid()
    {
        var store = MemoryStore();
        store.Add(Pending("builder", "creative"));

        Assert.NotNull(store.FindPending("builder", "creative"));
        // Names are matched without regard to case, the same way the uid gate matches its uids.
        Assert.NotNull(store.FindPending("BUILDER", "creative"));
        // It never leaks into the uid path: the empty uid cannot cover a real player.
        Assert.Null(store.FindCovering("", "creative"));
    }

    [Fact]
    public void APendingBobAndAUidBobCoexist()
    {
        var store = MemoryStore();
        store.Add(Pending("bob", "creative"));
        store.Add(new WhitelistEntry { PlayerUid = "uid-bob", PlayerName = "bob", ServerId = "creative" });

        // Both live at once, on their own keys, so listing bob before he connects does not collide
        // with the entry he gets once he has.
        Assert.NotNull(store.FindPending("bob", "creative"));
        Assert.NotNull(store.FindCovering("uid-bob", "creative"));
        Assert.Equal(2, store.Active().Count);
    }

    [Fact]
    public void ASecondPendingEntryForTheSameNameAndScopeReplacesTheFirst()
    {
        var store = MemoryStore();
        store.Add(new WhitelistEntry { PlayerName = "bob", ServerId = "creative", Note = "first" });
        store.Add(new WhitelistEntry { PlayerName = "bob", ServerId = "creative", Note = "second" });

        var single = Assert.Single(store.Active());
        Assert.Equal("second", single.Note);
    }

    [Fact]
    public void APendingEntryScopedToOneBackendDoesNotCoverAnother()
    {
        var store = MemoryStore();
        store.Add(Pending("builder", "staff"));

        Assert.NotNull(store.FindPending("builder", "staff"));
        Assert.Null(store.FindPending("builder", "creative"));
        // An empty scope asks about the network itself, which a scoped entry never covers.
        Assert.Null(store.FindPending("builder", ""));
    }

    [Fact]
    public void ANetworkWidePendingEntryCoversEveryBackend()
    {
        var store = MemoryStore();
        store.Add(Pending("builder"));

        Assert.NotNull(store.FindPending("builder", "creative"));
        Assert.NotNull(store.FindPending("builder", "staff"));
        Assert.NotNull(store.FindPending("builder", ""));
    }

    [Fact]
    public void ATimedPendingEntryStopsBeingFoundWhenItRunsOut()
    {
        var store = MemoryStore();
        store.Add(new WhitelistEntry { PlayerName = "builder", ExpiresAtUnix = clock.NowUnix + 3600 });

        Assert.NotNull(store.FindPending("builder"));
        clock.Advance(TimeSpan.FromSeconds(3601));
        Assert.Null(store.FindPending("builder"));
    }

    [Fact]
    public void TheSweepDropsExpiredPendingEntriesAlongsideUidOnes()
    {
        var store = MemoryStore();
        store.Add(new WhitelistEntry { PlayerName = "day-pass", ExpiresAtUnix = clock.NowUnix + 60 });
        store.Add(new WhitelistEntry { PlayerUid = "uid-perm", PlayerName = "perm" });
        store.Add(Pending("keeper"));

        clock.Advance(TimeSpan.FromSeconds(61));
        // One expired pending row dropped; the permanent uid entry and the permanent pending one
        // stay. Prune reaches both lists so the background sweep retires either kind.
        Assert.Equal(1, store.Prune());
        Assert.Equal(2, store.Active().Count);
        Assert.Equal(0, store.Prune());
    }

    // ---- binding ----

    [Fact]
    public void BindingRewritesThePendingEntryToCarryTheUid()
    {
        var store = MemoryStore();
        store.Add(new WhitelistEntry { PlayerName = "bob", ServerId = "creative", Note = "event list" });

        Assert.True(store.Bind("bob", "uid-bob", "creative"));

        // The pending row is gone and a uid entry stands in its place, carrying the same metadata,
        // so the next join matches by uid with no pending path.
        Assert.Null(store.FindPending("bob", "creative"));
        var bound = store.FindCovering("uid-bob", "creative");
        Assert.NotNull(bound);
        Assert.Equal("event list", bound!.Note);
        Assert.Equal("bob", bound.PlayerName);
        Assert.False(bound.IsPending);
    }

    [Fact]
    public void BindingANameThatIsNotPendingIsANoOp()
    {
        var store = MemoryStore();

        // Nothing to move: absent is a no-op reported false, which the endpoint and client read as
        // an idempotent success.
        Assert.False(store.Bind("ghost", "uid-ghost", "creative"));
        Assert.Empty(store.Active());
    }

    [Fact]
    public void BindingTwiceIsIdempotent()
    {
        var store = MemoryStore();
        store.Add(Pending("bob", "creative"));

        Assert.True(store.Bind("bob", "uid-bob", "creative"));
        // The second bind finds no pending entry left and changes nothing, leaving the one bound
        // entry standing rather than stacking a second.
        Assert.False(store.Bind("bob", "uid-bob", "creative"));
        Assert.Single(store.Active());
        Assert.NotNull(store.FindCovering("uid-bob", "creative"));
    }

    [Fact]
    public void BindingWithNoUidOrNoNameDoesNothing()
    {
        var store = MemoryStore();
        store.Add(Pending("bob", "creative"));

        Assert.False(store.Bind("", "uid-bob", "creative"));
        Assert.False(store.Bind("bob", "", "creative"));
        Assert.NotNull(store.FindPending("bob", "creative"));
    }

    // ---- persistence ----

    [Fact]
    public void APendingEntryOutlivesTheProcessThatMadeIt()
    {
        FileStore().Add(new WhitelistEntry { PlayerName = "builder", ServerId = "staff", Note = "trial" });

        var restarted = FileStore();

        var entry = Assert.Single(restarted.Active());
        Assert.True(entry.IsPending);
        Assert.Equal("builder", entry.PlayerName);
        Assert.Equal("trial", entry.Note);
        Assert.NotNull(restarted.FindPending("builder", "staff"));
    }

    [Fact]
    public void ABoundEntrySurvivesARestartAsAUidEntry()
    {
        var store = FileStore();
        store.Add(Pending("bob", "creative"));
        Assert.True(store.Bind("bob", "uid-bob", "creative"));

        // The restart is a second store over the same directory, exactly what the next process does.
        var restarted = FileStore();

        Assert.Null(restarted.FindPending("bob", "creative"));
        var bound = restarted.FindCovering("uid-bob", "creative");
        Assert.NotNull(bound);
        Assert.False(bound!.IsPending);
    }

    [Fact]
    public void PendingAndUidEntriesLandInSeparateFiles()
    {
        var store = FileStore();
        store.Add(new WhitelistEntry { PlayerUid = "uid-live", PlayerName = "live" });
        store.Add(new WhitelistEntry { PlayerName = "pendingsoul" });

        // The uid list holds the settled entry and the pending list holds the name-only one, and
        // neither file carries the other's, so a corrupt pending file cannot take the live list down.
        string whitelist = File.ReadAllText(Path.Combine(dir, RegistryStateFiles.WhitelistFileName));
        string pending = File.ReadAllText(Path.Combine(dir, RegistryStateFiles.WhitelistPendingFileName));
        Assert.Contains("uid-live", whitelist);
        Assert.DoesNotContain("pendingsoul", whitelist);
        Assert.Contains("pendingsoul", pending);
        Assert.DoesNotContain("uid-live", pending);
    }

    [Fact]
    public void ARemovedPendingEntryDoesNotComeBackAfterARestart()
    {
        var store = FileStore();
        store.Add(Pending("bob", "creative"));
        // A pending removal is keyed by name, the same way a uid removal is keyed by uid.
        store.Bind("bob", "uid-bob", "creative");

        var restarted = FileStore();
        // The pending file was rewritten without the bound name, so it does not resurrect as a
        // second, still-pending copy alongside the uid entry.
        Assert.Single(restarted.Active());
    }
}
