using Nimbus.Registry.Services;
using Nimbus.Shared.Models;
using Xunit;

namespace Nimbus.Registry.Core.Tests;

public class TransferFailureStoreTests
{
    private readonly FakeClock clock = new();

    private static TransferFailed Failure(string source = "source", string transfer = "transfer-1")
        => new()
        {
            SourceServerId = source,
            ClientTransferId = transfer,
            Reason = "target unavailable",
        };

    [Fact]
    public void Add_RejectsAnIncompleteNotice()
    {
        var store = new TransferFailureStore(clock);

        Assert.False(store.Add(null!));
        Assert.False(store.Add(Failure(source: "")));
        Assert.False(store.Add(Failure(transfer: "")));
        Assert.Empty(store.DrainForSource("source"));
        Assert.Empty(store.DrainForSource(""));
    }

    [Fact]
    public void Add_DeduplicatesAnOutstandingTransfer()
    {
        var store = new TransferFailureStore(clock);

        Assert.True(store.Add(Failure()));
        Assert.True(store.Add(Failure()));

        Assert.Single(store.DrainForSource("source"));
        Assert.Empty(store.DrainForSource("source"));
    }

    [Fact]
    public void Drain_IsolatedBySourceAndExpiresEntries()
    {
        var store = new TransferFailureStore(clock, TimeSpan.FromSeconds(30));
        store.Add(Failure(source: "source-a", transfer: "a"));
        store.Add(Failure(source: "source-b", transfer: "b"));

        Assert.Equal("a", Assert.Single(store.DrainForSource("source-a")).ClientTransferId);
        Assert.Empty(store.DrainForSource("source-a"));
        Assert.Equal("b", Assert.Single(store.DrainForSource("source-b")).ClientTransferId);

        store.Add(Failure(transfer: "expired"));
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Empty(store.DrainForSource("source"));
    }

    [Fact]
    public void Add_PreservesTheFailureTimeAndFillsAnAbsentOne()
    {
        var store = new TransferFailureStore(clock);
        var stamped = Failure(transfer: "stamped");
        stamped.FailedAtUnix = 123;
        var unstamped = Failure(transfer: "unstamped");

        store.Add(stamped);
        store.Add(unstamped);

        var notices = store.DrainForSource("source");
        Assert.Equal(123, notices[0].FailedAtUnix);
        Assert.Equal(clock.NowUnix, notices[1].FailedAtUnix);
    }

    [Fact]
    public void Drain_PreservesInsertionOrderWhenFailureTimesMatch()
    {
        var store = new TransferFailureStore(clock);
        store.Add(new TransferFailed
        {
            SourceServerId = "source",
            ClientTransferId = "z-first",
            Reason = null!,
            FailedAtUnix = 100,
        });
        store.Add(new TransferFailed
        {
            SourceServerId = "source",
            ClientTransferId = "a-second",
            FailedAtUnix = 100,
        });

        Assert.Equal(new[] { "z-first", "a-second" },
            store.DrainForSource("source").Select(notice => notice.ClientTransferId));
    }

    [Fact]
    public void Prune_DropsExpiredEntries()
    {
        var store = new TransferFailureStore(clock, TimeSpan.FromSeconds(30));
        store.Add(Failure());
        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.Equal(1, store.Prune());
        Assert.Equal(0, store.Prune());
    }

    [Fact]
    public void Add_ReplacesAnExpiredNotice()
    {
        var store = new TransferFailureStore(clock, TimeSpan.FromSeconds(30));
        store.Add(Failure());
        clock.Advance(TimeSpan.FromSeconds(31));

        store.Add(new TransferFailed
        {
            SourceServerId = "source",
            ClientTransferId = "transfer-1",
            Reason = "a later attempt",
        });

        var notice = Assert.Single(store.DrainForSource("source"));
        Assert.Equal("a later attempt", notice.Reason);
    }

    [Fact]
    public void Add_StripsNewlinesFromReason()
    {
        var store = new TransferFailureStore(clock);
        store.Add(new TransferFailed
        {
            SourceServerId = "source",
            ClientTransferId = "t1",
            Reason = "line one\nline two\r\nline three",
        });

        var notice = Assert.Single(store.DrainForSource("source"));
        Assert.Equal("line one line two  line three", notice.Reason);
    }

    [Fact]
    public void Add_CapsReasonLengthAt200()
    {
        var store = new TransferFailureStore(clock);
        string longReason = new string('x', 300);
        store.Add(new TransferFailed
        {
            SourceServerId = "source",
            ClientTransferId = "t1",
            Reason = longReason,
        });

        var notice = Assert.Single(store.DrainForSource("source"));
        Assert.Equal(200, notice.Reason.Length);
    }

    [Fact]
    public void Add_FallsBackToDefaultWhenReasonIsNull()
    {
        var store = new TransferFailureStore(clock);
        store.Add(new TransferFailed
        {
            SourceServerId = "source",
            ClientTransferId = "t1",
            Reason = null!,
        });

        var notice = Assert.Single(store.DrainForSource("source"));
        Assert.Equal("seamless transfer failed", notice.Reason);
    }
}
