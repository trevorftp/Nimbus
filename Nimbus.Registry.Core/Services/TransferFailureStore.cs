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
        var notice = new TransferFailed
        {
            ClientTransferId = failure.ClientTransferId.Trim(),
            SourceServerId = failure.SourceServerId.Trim(),
            Reason = failure.Reason ?? "seamless transfer failed",
            FailedAtUnix = failure.FailedAtUnix > 0 ? failure.FailedAtUnix : now,
        };
        var entry = new Entry(notice, now + ttlSeconds);
        string key = Key(notice.SourceServerId, notice.ClientTransferId);
        lock (gate)
        {
            if (!entries.TryGetValue(key, out Entry? existing) || existing.ExpiresAtUnix <= now)
                entries[key] = entry;
        }
        return true;
    }

    public List<TransferFailed> DrainForSource(string sourceServerId)
    {
        if (string.IsNullOrWhiteSpace(sourceServerId)) return new List<TransferFailed>();

        sourceServerId = sourceServerId.Trim();
        long now = clock.GetUtcNow().ToUnixTimeSeconds();
        var result = new List<TransferFailed>();
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
                result.Add(entry.Notice);
            }
        }

        result.Sort((left, right) =>
        {
            int time = left.FailedAtUnix.CompareTo(right.FailedAtUnix);
            return time != 0
                ? time
                : string.Compare(left.ClientTransferId, right.ClientTransferId, StringComparison.Ordinal);
        });
        return result;
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

    private static string Key(string sourceServerId, string clientTransferId)
        => sourceServerId.Length + ":" + sourceServerId + clientTransferId;

    private sealed record Entry(TransferFailed Notice, long ExpiresAtUnix);
}
