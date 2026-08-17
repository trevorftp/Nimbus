using Atlas.Api;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Nimbus.ServerMod.Tests;

/// <summary>
/// Configures the mod the way an operator does: write nimbus-server.json into the live
/// data path, run "/nimbus reload". No private state is touched on the way in; since
/// /nimbus reload recreates the registry client (#4), the file + reload pair brings the
/// mod from any state to a fully wired one.
///
/// Reads and the narrow packet callback injection go through reflection: the game's ModLoader loads a COPY of the staged
/// Nimbus.ServerMod.dll, so its types are never identity-equal to compile-time
/// references and a typed cast cannot work.
/// </summary>
public sealed class NimbusHarness
{
    private readonly ModSystem modSystem;

    private NimbusHarness(ModSystem modSystem) => this.modSystem = modSystem;

    public static async Task<NimbusHarness> ConfigureAsync(
        IWorldSession world,
        string registryUrl,
        string sharedSecret,
        bool reservationRequired = true,
        string transferMode = "redirect",
        bool allowPlayerServerCommand = true,
        int seamlessPrepareAckTimeoutSeconds = 1,
        bool failClosedWhenRegistryUnreachable = false,
        string? shortcutCommandsJson = null)
    {
        WriteConfig(registryUrl, sharedSecret, reservationRequired, transferMode,
            allowPlayerServerCommand, seamlessPrepareAckTimeoutSeconds, failClosedWhenRegistryUnreachable,
            shortcutCommandsJson);

        CommandResult reload = await world.ExecuteCommand("/nimbus reload");
        if (!reload.Ok)
            throw new InvalidOperationException($"/nimbus reload failed: {reload.Message}");

        ModSystem ms = world.Api.ModLoader.Systems
            .FirstOrDefault(s => s.GetType().FullName == "Nimbus.ServerMod.NimbusServerModSystem")
            ?? throw new InvalidOperationException(
                "NimbusServerModSystem not loaded; check the AtlasMods staging paths and the server logs.");
        return new NimbusHarness(ms);
    }

    /// <summary>Writes nimbus-server.json into the embedded server's ModConfig folder.</summary>
    public static void WriteConfig(
        string registryUrl,
        string sharedSecret,
        bool reservationRequired = true,
        string transferMode = "redirect",
        bool allowPlayerServerCommand = true,
        int seamlessPrepareAckTimeoutSeconds = 1,
        bool failClosedWhenRegistryUnreachable = false,
        string? shortcutCommandsJson = null)
    {
        string path = Path.Combine(GamePaths.DataPath, "ModConfig", "nimbus-server.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            {
              "Enabled": true,
              "ServerId": "backend-test",
              "DisplayName": "Atlas backend",
              "PublicHost": "127.0.0.1",
              "RegistryUrl": "{{registryUrl}}",
              "SharedSecret": "{{sharedSecret}}",
              "ReservationRequired": {{(reservationRequired ? "true" : "false")}},
              "FailClosedWhenRegistryUnreachable": {{(failClosedWhenRegistryUnreachable ? "true" : "false")}},
              "TransferMode": "{{transferMode}}",
              "AllowPlayerServerCommand": {{(allowPlayerServerCommand ? "true" : "false")}},
              "HeartbeatIntervalSeconds": 1,
              "SeamlessPrepareAckTimeoutSeconds": {{seamlessPrepareAckTimeoutSeconds}},
              "ShortcutCommands": {{shortcutCommandsJson ?? "[]"}}
            }
            """);
    }

    /// <summary>Runs a command with a player caller. World.ExecuteCommand runs as the console,
    /// which the RequiresPlayer precondition on /server and the shortcuts rejects.</summary>
    public static Task<CommandResult> ExecuteAs(IWorldSession world, ITestPlayer player, string command)
    {
        var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Api.ChatCommands.ExecuteUnparsed(command, new TextCommandCallingArgs
        {
            Caller = new Caller
            {
                Player = player.Player,
                FromChatGroupId = GlobalConstants.GeneralChatGroup,
            },
        }, result =>
        {
            if (result.Status == EnumCommandStatus.Deferred) return;
            tcs.TrySetResult(new CommandResult(
                result.Status == EnumCommandStatus.Success, result.StatusMessage ?? "", result));
        });
        return tcs.Task;
    }

    /// <summary>The mod's LastSeamlessCommit, empty until the target sends a commit.</summary>
    public string LastSeamlessCommit
        => (string)(modSystem.GetType().GetProperty("LastSeamlessCommit")!.GetValue(modSystem) ?? "");

    public string LastSeamlessAbort
        => (string)(modSystem.GetType().GetProperty("LastSeamlessAbort")!.GetValue(modSystem) ?? "");

    public string PendingSeamlessTransferId
    {
        get
        {
            FieldInfo mapField = modSystem.GetType().GetField("pendingSeamless",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            object map = mapField.GetValue(modSystem)!;
            PropertyInfo keys = map.GetType().GetProperty("Keys")!;
            return ((IEnumerable<string>)keys.GetValue(map)!).FirstOrDefault() ?? "";
        }
    }

    /// <summary>Completes the server-side ready handler with a packet-shaped object. Atlas does
    /// not run a Nimbus client, so the scenario uses the mod's real private handler after reading
    /// the generated transfer id from the pending handshake map.</summary>
    public void AcknowledgeSeamlessReady(ITestPlayer player, string transferId)
    {
        Type modType = modSystem.GetType();
        Type readyType = modType.Assembly.GetType("Nimbus.ServerMod.NimbusSeamlessReady")!;
        object ready = Activator.CreateInstance(readyType)!;
        readyType.GetProperty("TransferId")!.SetValue(ready, transferId);
        MethodInfo handler = modType.GetMethod("OnSeamlessReady",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        handler.Invoke(modSystem, new[] { player.Player, ready });
    }

    /// <summary>Calls the mod's public GetForwardedPlayer(uid); null when not forwarded.</summary>
    public object? GetForwardedPlayer(string playerUid)
        => modSystem.GetType().GetMethod("GetForwardedPlayer")!
            .Invoke(modSystem, new object[] { playerUid });

    /// <summary>RealRemoteIp recorded on the consumed reservation, or null.</summary>
    public string? ForwardedRealIp(string playerUid)
    {
        object? reservation = GetForwardedPlayer(playerUid);
        return reservation?.GetType().GetProperty("RealRemoteIp")?.GetValue(reservation) as string;
    }
}
