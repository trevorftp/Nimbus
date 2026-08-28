using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// A stand-in backend on an ephemeral port that records every connection it accepts and every
/// byte it is sent. Shared by the socket-level session tests.
/// </summary>
internal sealed class RecordingBackend : IDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource cts = new();
    private readonly List<byte> received = new();
    private readonly List<TcpClient> accepted = new();
    private readonly object gate = new();
    private int connections;

    public RecordingBackend()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public int BytesReceived { get { lock (gate) return received.Count; } }

    /// <summary>Connections accepted so far. The only signal available when a session is routed
    /// here but has not sent anything yet.</summary>
    public int Connections => Volatile.Read(ref connections);

    public BackendEndpoint Endpoint(string serverId = "")
        => new() { Host = "127.0.0.1", Port = Port, ServerId = serverId };

    public bool Sent(string needle)
    {
        lock (gate) return Encoding.UTF8.GetString(received.ToArray()).Contains(needle);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
                Interlocked.Increment(ref connections);
                lock (gate) accepted.Add(client);
                _ = Task.Run(() => ReadLoopAsync(client));
            }
        }
        catch { }
    }

    private async Task ReadLoopAsync(TcpClient client)
    {
        var buf = new byte[4096];
        try
        {
            while (!cts.IsCancellationRequested)
            {
                int read = await client.GetStream().ReadAsync(buf, cts.Token).ConfigureAwait(false);
                if (read <= 0) return;
                lock (gate) received.AddRange(buf.AsSpan(0, read).ToArray());
            }
        }
        catch { }
    }

    /// <summary>Closes every connection accepted so far, leaving the listener up. This is what a
    /// backend process dying under a live session looks like from the proxy's side, which
    /// Dispose does not reproduce: cancelling the read loop leaves the socket open.</summary>
    public void DropConnections()
    {
        lock (gate)
        {
            foreach (var c in accepted) { try { c.Close(); } catch { /* already gone */ } }
            accepted.Clear();
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        try { listener.Stop(); } catch { }
        cts.Dispose();
    }
}
