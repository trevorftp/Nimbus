using System.Text.Json;
using Atlas.Api;
using Atlas.XUnit;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Xunit;

namespace Nimbus.ServerMod.Tests;

/// <summary>
/// Covers the outbound half of a transfer: the /nimbus send and /server commands,
/// BeginTransfer's eager validation against the registry snapshot, the redirect-mode
/// intent POST, and the seamless prepare/ack timeout. The fake registry serves the
/// snapshot the mod polls through its heartbeat loop (1s interval in test config).
/// </summary>
public class TransferCommandScenarios : AtlasScenarioBase
{
    private const string Secret = "transfer-secret";

    /// <summary>Waits until the heartbeat loop has pulled the snapshot into LastSnapshot.
    /// Polls through the /nimbus servers command; no blocking waits inside an Until
    /// predicate, the command itself needs the game thread to complete.</summary>
    private async Task WaitForSnapshot(string backendId)
    {
        for (int i = 0; i < 100; i++)
        {
            CommandResult servers = await World.ExecuteCommand("/nimbus servers");
            if (servers.Message.Contains(backendId)) return;
            await World.Ticks(10);
        }
        throw new Xunit.Sdk.XunitException($"registry snapshot never listed '{backendId}'");
    }

    [AtlasScenario]
    public async Task Send_PostsASignedTransferIntent_InRedirectMode()
    {
        using var registry = new FakeRegistry(Secret);
        registry.ServersSnapshot = FakeRegistry.Snapshot(FakeRegistry.Backend("hub2"));
        registry.TransferIntentResponse = new { ok = true };
        await NimbusHarness.ConfigureAsync(World, registry.Url, Secret, reservationRequired: false);

        ITestPlayer trent = await World.JoinPlayer("trent");
        await WaitForSnapshot("hub2");

        CommandResult send = await World.ExecuteCommand("/nimbus send trent hub2 rebalancing");
        Assert.True(send.Ok, send.Message);
        Assert.Contains("Transferring", send.Message);

        await World.Until(() => registry.Requests.Any(r => r.Path == "/api/transfer-intents"));
        var intent = registry.Requests.Last(r => r.Path == "/api/transfer-intents");
        Assert.True(intent.SignatureValid, "transfer intents must be HMAC-signed");

        using JsonDocument body = JsonDocument.Parse(intent.Body);
        Assert.Equal(trent.Player.PlayerUID, body.RootElement.GetProperty("PlayerUid").GetString());
        Assert.Equal("hub2", body.RootElement.GetProperty("TargetServerId").GetString());
        Assert.Equal("backend-test", body.RootElement.GetProperty("SourceServerId").GetString());
        Assert.Equal("redirect", body.RootElement.GetProperty("Mode").GetString());
        Assert.Equal("rebalancing", body.RootElement.GetProperty("Reason").GetString());
        Assert.StartsWith("admin:", body.RootElement.GetProperty("RequestedBy").GetString());
    }

    [AtlasScenario]
    public async Task Send_FailsEagerly_WhenTargetIsUnknownStaleOrInMaintenance()
    {
        using var registry = new FakeRegistry(Secret);
        registry.ServersSnapshot = FakeRegistry.Snapshot(
            FakeRegistry.Backend("hub-stale", stale: true),
            FakeRegistry.Backend("hub-maint", maintenance: true));
        await NimbusHarness.ConfigureAsync(World, registry.Url, Secret, reservationRequired: false);

        await World.JoinPlayer("victor");
        await WaitForSnapshot("hub-stale");

        CommandResult unknown = await World.ExecuteCommand("/nimbus send victor nowhere");
        Assert.False(unknown.Ok);
        Assert.Contains("not in the registry snapshot", unknown.Message);

        CommandResult stale = await World.ExecuteCommand("/nimbus send victor hub-stale");
        Assert.False(stale.Ok);
        Assert.Contains("stale", stale.Message);

        CommandResult maint = await World.ExecuteCommand("/nimbus send victor hub-maint");
        Assert.False(maint.Ok);
        Assert.Contains("maintenance", maint.Message);

        CommandResult self = await World.ExecuteCommand("/nimbus send victor backend-test");
        Assert.False(self.Ok);
        Assert.Contains("already on", self.Message);

        // All four were rejected before any registry call.
        Assert.DoesNotContain(registry.Requests, r => r.Path == "/api/transfer-intents");
    }

    [AtlasScenario]
    public async Task SelfServerCommand_RespectsTheConfigGate()
    {
        using var registry = new FakeRegistry(Secret);
        await NimbusHarness.ConfigureAsync(World, registry.Url, Secret,
            reservationRequired: false, allowPlayerServerCommand: false);
        ITestPlayer walter = await World.JoinPlayer("walter");

        CommandResult disabled = await ExecuteAs(walter, "/server hub2");
        Assert.False(disabled.Ok);
        Assert.Contains("disabled", disabled.Message);

        // Console callers never reach the handler: the engine-side RequiresPlayer
        // precondition rejects them first.
        CommandResult console = await World.ExecuteCommand("/server hub2");
        Assert.False(console.Ok, $"console unexpectedly ok: '{console.Message}' status={console.Raw.Status}");

        await NimbusHarness.ConfigureAsync(World, registry.Url, Secret,
            reservationRequired: false, allowPlayerServerCommand: true);

        // Gate open, but the target is unknown: BeginTransfer's eager snapshot check.
        // LastSnapshot survives reconfiguration (scenarios in this class share the mod
        // instance), so aim at a name no scenario ever puts in a snapshot.
        CommandResult unknown = await ExecuteAs(walter, "/server nowhere-at-all");
        Assert.False(unknown.Ok);
        Assert.Contains("not in the registry snapshot", unknown.Message);
    }

    /// <summary>Runs a chat command as the given player; shared with the shortcut scenarios.</summary>
    private Task<CommandResult> ExecuteAs(ITestPlayer player, string command)
        => NimbusHarness.ExecuteAs(World, player, command);

    [AtlasScenario]
    public async Task Seamless_WithoutClientAck_AbortsBeforeAnyRegistryCall()
    {
        using var registry = new FakeRegistry(Secret);
        registry.ServersSnapshot = FakeRegistry.Snapshot(FakeRegistry.Backend("hub2"));
        await NimbusHarness.ConfigureAsync(World, registry.Url, Secret,
            reservationRequired: false, transferMode: "seamless", seamlessPrepareAckTimeoutSeconds: 1);

        await World.JoinPlayer("sybil");
        await WaitForSnapshot("hub2");

        // The dummy player has no Nimbus client mod, so the NimbusSeamlessReady ack never
        // arrives and the 1s prepare window must expire.
        CommandResult send = await World.ExecuteCommand("/nimbus send sybil hub2");
        Assert.True(send.Ok, send.Message);

        await World.Ticks(90); // comfortably past the 1s ack timeout

        Assert.DoesNotContain(registry.Requests, r => r.Path == "/api/transfer-intents");
    }

    [AtlasScenario]
    public async Task AProxyFailureNoticeOnTheNextHeartbeat_AbortsTheSourceTransfer()
    {
        using var registry = new FakeRegistry(Secret);
        registry.ServersSnapshot = FakeRegistry.Snapshot(FakeRegistry.Backend("hub2"));
        registry.TransferIntentResponse = new { ok = true };
        registry.FailNextSeamlessIntent = true;
        NimbusHarness nimbus = await NimbusHarness.ConfigureAsync(World, registry.Url, Secret,
            reservationRequired: false, transferMode: "seamless", seamlessPrepareAckTimeoutSeconds: 1);

        ITestPlayer player = await World.JoinPlayer("yara");
        await WaitForSnapshot("hub2");

        CommandResult send = await World.ExecuteCommand("/nimbus send yara hub2");
        Assert.True(send.Ok, send.Message);
        await World.Until(() => nimbus.PendingSeamlessTransferId.Length > 0);

        string transferId = nimbus.PendingSeamlessTransferId;
        nimbus.AcknowledgeSeamlessReady(player, transferId);

        await World.Until(() => registry.Requests.Any(request =>
            request.Path == "/api/transfer-intents"));
        await World.Until(() => registry.FailureNoticeDeliveries == 1);
        await World.Until(() => nimbus.LastSeamlessAbort.Contains(transferId, StringComparison.Ordinal));

        Assert.Contains("target backend is unavailable", nimbus.LastSeamlessAbort);
    }
}
