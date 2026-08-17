namespace Nimbus.Shared.Models;

// A failure published by the proxy for a seamless transfer that the source backend
// already handed off to the registry. The source uses ClientTransferId to find the
// player whose client needs an abort.
public sealed class TransferFailed
{
    public string ClientTransferId { get; set; } = "";
    public string SourceServerId { get; set; } = "";
    public string Reason { get; set; } = "";
    public long FailedAtUnix { get; set; }
}
