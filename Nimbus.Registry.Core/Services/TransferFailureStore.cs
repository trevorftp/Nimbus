using Nimbus.Shared.Models;

namespace Nimbus.Registry.Services;

// Short-lived notices from the proxy to the source backend. The heartbeat is the
// delivery channel, so a notice remains available until the source reads it or the
// bounded delivery window expires.
public sealed class TransferFailureStore
{
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(2);

    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly long ttlSeconds;
    private long nextSequence;

    public TransferFailureStore(TimeProvider? clock = null, TimeSpan? ttl = null)
    {
        this.clock = clock ?? TimeProvider.System;
        TimeSpan window = ttl ?? DefaultTtl;
        ttlSeconds = Math.Max(1, (long)Math.Ceiling(window.TotalSeconds));
    }

    public bool Add(TransferFailed failure)
    {
        if (failure is null
            || string.IsNullOrWhiteSpace(failure.ClientTransferId)
            || string.IsNullOrWhiteSpace(failure.SourceServerId))
            return false;

        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        TransferFailed notice = new TransferFailed
        {
            ClientTransferId = failure.ClientTransferId.Trim(),
            SourceServerId = failure.SourceServerId.Trim(),
            Reason = SanitizeReason(failure.Reason),
            FailedAtUnix = failure.FailedAtUnix > 0 ? failure.FailedAtUnix : now,
        };
        string key = Key(notice.SourceServerId, notice.ClientTransferId);
        lock (gate)
        {
            if (!entries.TryGetValue(key, out Entry? existing) || existing.ExpiresAtUnix <= now)
                entries[key] = new Entry(notice, now + ttlSeconds, ++nextSequence);
        }
        return true;
    }

    public List<TransferFailed> DrainForSource(string sourceServerId)
    {
        if (string.IsNullOrWhiteSpace(sourceServerId)) return new List<TransferFailed>();

        sourceServerId = sourceServerId.Trim();
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        List<Entry> result = new List<Entry>();
        lock (gate)
        {
            foreach (var pair in entries.ToArray())
            {
                Entry entry = pair.Value;
                if (entry.ExpiresAtUnix <= now)
                {
                    entries.Remove(pair.Key);
                    continue;
                }

                if (!string.Equals(entry.Notice.SourceServerId, sourceServerId, StringComparison.OrdinalIgnoreCase))
                    continue;
                entries.Remove(pair.Key);
                result.Add(entry);
            }
        }

        result.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
        List<TransferFailed> notices = new List<TransferFailed>(result.Count);
        foreach (Entry entry in result)
            notices.Add(entry.Notice);
        return notices;
    }

    public int Prune()
    {
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        int dropped = 0;
        lock (gate)
        {
            foreach (var pair in entries.ToArray())
            {
                if (pair.Value.ExpiresAtUnix <= now && entries.Remove(pair.Key))
                    dropped++;
            }
        }
        return dropped;
    }

    private const int MaxReasonLength = 200;

    private static string SanitizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "seamless transfer failed";
        string clean = reason.Replace('\n', ' ').Replace('\r', ' ');
        return clean.Length > MaxReasonLength ? clean[..MaxReasonLength] : clean;
    }

    private static string Key(string sourceServerId, string clientTransferId)
        => sourceServerId.Length + ":" + sourceServerId + clientTransferId;

    private sealed record Entry(TransferFailed Notice, long ExpiresAtUnix, long Sequence);
}
