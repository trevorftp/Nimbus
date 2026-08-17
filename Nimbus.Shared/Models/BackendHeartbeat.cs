namespace Nimbus.Shared.Models;

// Heartbeat payload sent by a backend to the Nimbus registry.
public sealed class BackendHeartbeat
{
    public string ServerId { get; set; } = "";
    public string DisplayName { get; set; } = "";

    // Where the NETWORK reaches this backend (proxy-side dials and redirect stamping),
    // not where players connect. Distinct from the registry's Identity.PublicHost, which
    // is the PROXY's public address advertised on the VS master server.
    public string PublicHost { get; set; } = "";
    public int PublicPort { get; set; } = 42420;
    public string[] Tags { get; set; } = Array.Empty<string>();

    public int Players { get; set; }
    public int MaxPlayers { get; set; }
    public double Tps { get; set; }
    public long UptimeSeconds { get; set; }

    // True when the backend is draining or in maintenance, rejecting new joins.
    public bool Maintenance { get; set; }

    // True when the backend requires a valid reservation on identification.
    public bool ReservationRequired { get; set; }

    public string GameVersion { get; set; } = "";
    public BackendModInfo[] RequiredClientMods { get; set; } = Array.Empty<BackendModInfo>();
}

public sealed class BackendHeartbeatResponse
{
    public bool Ok { get; set; } = true;
    public int NextHeartbeatSeconds { get; set; } = 5;
    public int SeamlessReadyWaitTimeoutSeconds { get; set; } = 75;
    public List<TransferFailed> FailedTransfers { get; set; } = new();
    public string? Message { get; set; }
}
