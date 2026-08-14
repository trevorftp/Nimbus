namespace Nimbus.ServerMod;

using System.Collections.Concurrent;
using System.Text;
using Nimbus.Shared;
using Nimbus.Shared.Models;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

public sealed class NimbusServerModSystem : ModSystem
{
    private const string ConfigFileName = "nimbus-server.json";
    private const int DefaultSeamlessReadyWaitTimeoutSeconds = 75;

    private ICoreServerAPI? api;
    private IServerNetworkChannel? channel;
    private NimbusServerConfig config = new();
    private NimbusRegistryClient? registry;
    private CancellationTokenSource? stop;
    private Task? heartbeatTask;
    private long startUnix;

    private readonly ConcurrentDictionary<string, NimbusClientCapability> capabilities = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PendingSeamlessHandshake> pendingSeamless = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, InFlightSeamlessTransfer> inFlightSeamless = new(StringComparer.OrdinalIgnoreCase);
    // Forwarding data keyed by playerUID. Populated when a player arrives via the proxy
    // (reservation consumed on join). Cleared on disconnect.
    private readonly ConcurrentDictionary<string, TransferReservation> forwarding = new(StringComparer.OrdinalIgnoreCase);
    private int advertisedReadyWaitTimeoutSeconds = DefaultSeamlessReadyWaitTimeoutSeconds;
    private int heartbeatIntervalSeconds = 5;

    public NetworkSnapshot LastSnapshot { get; private set; } = new();
    public DateTime LastSnapshotUtc { get; private set; }
    public string LastStatus { get; private set; } = "not started";

    // Last seamless handshake this backend completed as the transfer TARGET, shown by
    // /nimbus status. Seamless failures are otherwise invisible from the receiving side,
    // which is where an operator looks when a player reports a stuck transfer screen.
    public string LastSeamlessCommit { get; private set; } = "";
    public string LastSeamlessAbort { get; private set; } = "";

    public override bool ShouldLoad(EnumAppSide forSide)
        => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        this.api = api;
        config = LoadConfig(api);
        RegisterNetwork(api);
        RegisterCommands(api);

        api.Event.PlayerDisconnect += player =>
        {
            capabilities.TryRemove(player.PlayerUID, out _);
            forwarding.TryRemove(player.PlayerUID, out _);
            RemoveInFlightForPlayer(player.PlayerUID);
        };
        api.Event.PlayerJoin += OnPlayerJoin;

        if (!config.Enabled)
        {
            LastStatus = "disabled";
            api.Logger.Notification("Nimbus server mod loaded but disabled");
            return;
        }

        if (!IsConfigured(config))
        {
            LastStatus = "misconfigured";
            WarnUnconfigured();
            return;
        }

        registry = new NimbusRegistryClient(config);
        stop = new CancellationTokenSource();
        startUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Volatile.Write(ref heartbeatIntervalSeconds, Math.Max(1, config.HeartbeatIntervalSeconds));
        var stopToken = stop.Token;
        heartbeatTask = Task.Run(() => HeartbeatLoopAsync(stopToken), stopToken);
        LastStatus = "starting";
        api.Logger.Notification($"Nimbus server mod started as {config.ServerId}");
    }

    public override void Dispose()
    {
        try { stop?.Cancel(); } catch { /* already disposed by an earlier Dispose */ }
        // No token on the drain: stop is already cancelled above, and the 2s deadline is
        // the only bound this wait may have. Cancelling it would skip the loop's teardown.
        try { heartbeatTask?.Wait(TimeSpan.FromSeconds(2), CancellationToken.None); }
        catch { /* the loop faulted or timed out; Dispose has nothing left to do about it */ }
        registry?.Dispose();
        stop?.Dispose();
    }

    public bool HasSeamlessCapability(IServerPlayer player)
        => capabilities.TryGetValue(player.PlayerUID, out var cap) && cap.SupportsSeamlessTransfers;

    public async Task<TransferIntentResponse> RequestTransferAsync(IServerPlayer player, string targetServerId, string? reason, string requestedBy)
    {
        if (!config.Enabled || registry == null)
            return new TransferIntentResponse { Ok = false, Error = "Nimbus server mod is disabled" };

        var req = new TransferIntentRequest
        {
            PlayerUid = player.PlayerUID,
            PlayerName = player.PlayerName,
            SourceServerId = config.ServerId,
            TargetServerId = targetServerId,
            Mode = config.TransferMode,
            Reason = reason,
            RequestedBy = requestedBy,
            TtlSeconds = 30,
            ClientSupportsSeamlessTransfers = HasSeamlessCapability(player)
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(config.RegistryHttpTimeoutSeconds + 1));
        return await registry.PostTransferIntentAsync(req, cts.Token).ConfigureAwait(false)
            ?? new TransferIntentResponse { Ok = false, Error = "no response from registry" };
    }

    private static bool IsConfigured(NimbusServerConfig cfg) => MissingSettings(cfg).Count == 0;

    // The settings without which this backend cannot join a network, by the name they carry in
    // nimbus-server.json so the log line points at something the operator can find.
    //
    // SharedSecret is the one that is not simply about emptiness: the documented placeholders are
    // in this repository, in the wiki and on every panel egg's variable screen, so a backend
    // holding one is a backend authenticating with a value anybody can read. Treating them as
    // unset is what the proxy's validator already does for its own copy of the same secret.
    private static List<string> MissingSettings(NimbusServerConfig cfg)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(cfg.ServerId)) missing.Add("ServerId");
        if (string.IsNullOrWhiteSpace(cfg.RegistryUrl)) missing.Add("RegistryUrl");
        if (string.IsNullOrWhiteSpace(cfg.PublicHost)) missing.Add("PublicHost");
        if (SecretPlaceholders.IsPlaceholder(cfg.SharedSecret)) missing.Add("SharedSecret");
        return missing;
    }

    // Reached at boot and again on every /nimbus reload that does not wire the mod up, which is
    // where an operator is watching after editing the file.
    private void WarnUnconfigured()
    {
        var missing = MissingSettings(config);
        if (missing.Count == 0 || api == null) return;

        var message = new StringBuilder()
            .Append("Nimbus server mod is enabled but ").Append(ConfigFileName)
            .Append(" still needs: ").Append(string.Join(", ", missing))
            .Append(". Heartbeats are off until it does.");
        if (missing.Contains("SharedSecret"))
            message.Append(" SharedSecret is not generated here: copy the value the network already runs on, which is registry.embedded_shared_secret in the proxy's nimbus.proxy.toml, or shared_secret in a standalone nimbus.registry.toml. A secret invented on this side matches nothing.");
        api.Logger.Warning(message.ToString());
    }

    private static NimbusServerConfig LoadConfig(ICoreServerAPI api)
    {
        NimbusServerConfig? cfg = null;
        try { cfg = api.LoadModConfig<NimbusServerConfig>(ConfigFileName); }
        catch (Exception ex) { api.Logger.Warning($"Could not load {ConfigFileName}: {ex.Message}"); }

        cfg ??= new NimbusServerConfig();
        cfg.Normalize();
        api.StoreModConfig(cfg, ConfigFileName);
        return cfg;
    }

    private void RegisterNetwork(ICoreServerAPI api)
    {
        channel = api.Network.RegisterChannel(NimbusModProtocol.ChannelName)
            .RegisterMessageType<NimbusClientHello>()
            .RegisterMessageType<NimbusSeamlessPrepare>()
            .RegisterMessageType<NimbusSeamlessCommit>()
            .RegisterMessageType<NimbusSeamlessReady>()
            .RegisterMessageType<NimbusSeamlessAbort>()
            .SetMessageHandler<NimbusClientHello>(OnClientHello)
            .SetMessageHandler<NimbusSeamlessReady>(OnSeamlessReady);
    }

    private void OnClientHello(IServerPlayer player, NimbusClientHello hello)
    {
        capabilities[player.PlayerUID] = new NimbusClientCapability(hello.ProtocolVersion, hello.SupportsSeamlessTransfers);
    }

    private void OnSeamlessReady(IServerPlayer player, NimbusSeamlessReady ready)
    {
        if (string.IsNullOrWhiteSpace(ready.TransferId))
            return;

        if (pendingSeamless.TryGetValue(ready.TransferId, out var pending) &&
            string.Equals(pending.PlayerUid, player.PlayerUID, StringComparison.OrdinalIgnoreCase))
        {
            pending.Ready.TrySetResult(true);
            api?.Logger.Notification($"Nimbus seamless ready ack from {player.PlayerName} ({ready.TransferId})");
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        if (registry == null || api == null) return;

        int interval = Math.Max(1, config.HeartbeatIntervalSeconds);
        int failures = 0;
        while (!ct.IsCancellationRequested)
        {
            // Re-read the field each round: a config reload can swap or clear the client.
            var client = registry;
            if (client == null) break;
            try
            {
                var response = await client.HeartbeatAsync(BuildHeartbeat(), ct).ConfigureAwait(false);
                if (response != null)
                {
                    ProcessFailureNotices(response.FailedTransfers);
                    if (response.SeamlessReadyWaitTimeoutSeconds > 0)
                        Volatile.Write(ref advertisedReadyWaitTimeoutSeconds, response.SeamlessReadyWaitTimeoutSeconds);
                }
                if (response?.Ok == true)
                {
                    LastStatus = "ok";
                    failures = 0;
                    if (response.NextHeartbeatSeconds > 0)
                    {
                        interval = response.NextHeartbeatSeconds;
                        Volatile.Write(ref heartbeatIntervalSeconds, interval);
                    }
                }
                else
                {
                    failures++;
                    LastStatus = "heartbeat rejected: " + (response?.Message ?? "no response");
                }

                var snapshot = await client.GetServersAsync(ct).ConfigureAwait(false);
                if (snapshot != null)
                {
                    LastSnapshot = snapshot;
                    LastSnapshotUtc = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                failures++;
                LastStatus = "error: " + ex.Message;
                if (failures == 1 || failures % 12 == 0)
                    api.Logger.Warning($"Nimbus heartbeat failed ({failures}x): {ex.Message}");
            }

            PruneInFlightSeamless();

            try { await Task.Delay(TimeSpan.FromSeconds(interval), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private BackendHeartbeat BuildHeartbeat()
    {
        int players = api?.Server.Players.Count(p => p.ConnectionState != EnumClientState.Offline) ?? 0;
        int maxPlayers = api?.Server.Config.MaxClients ?? 0;

        return new BackendHeartbeat
        {
            ServerId = config.ServerId,
            DisplayName = string.IsNullOrWhiteSpace(config.DisplayName) ? config.ServerId : config.DisplayName,
            PublicHost = config.PublicHost,
            PublicPort = config.PublicPort,
            Tags = config.Tags.ToArray(),
            Players = players,
            MaxPlayers = maxPlayers,
            Tps = 0,
            UptimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - startUnix,
            Maintenance = config.Maintenance,
            ReservationRequired = config.ReservationRequired,
            GameVersion = GameVersion.OverallVersion,
            RequiredClientMods = BuildRequiredClientMods()
        };
    }

    private BackendModInfo[] BuildRequiredClientMods()
    {
        if (api == null) return Array.Empty<BackendModInfo>();

        var result = new List<BackendModInfo>();
        foreach (var info in api.ModLoader.Mods.Select(mod => mod.Info))
        {
            if (info == null || !info.RequiredOnClient) continue;
            if (info.Side != EnumAppSide.Universal) continue;
            result.Add(new BackendModInfo { Id = info.ModID ?? "", Version = info.Version ?? "" });
        }
        return result.ToArray();
    }

    private TextCommandResult ReloadConfigCommand()
    {
        if (api == null) return TextCommandResult.Error("Not initialized.");
        try
        {
            var fresh = api.LoadModConfig<NimbusServerConfig>(ConfigFileName) ?? new NimbusServerConfig();
            fresh.Normalize();
            config = fresh;
            api.StoreModConfig(config, ConfigFileName);
            RewireRegistry();
            return TextCommandResult.Success($"Nimbus config reloaded. {config.StatusSummary()}");
        }
        catch (Exception ex)
        {
            return TextCommandResult.Error($"Nimbus config reload failed: {ex.Message}");
        }
    }

    // Reload used to swap only the config object, so a changed RegistryUrl or SharedSecret
    // kept talking to the old registry until a full restart. Recreate the client to match
    // the fresh config, and start the heartbeat loop if the mod booted unconfigured.
    private void RewireRegistry()
    {
        var old = registry;
        if (config.Enabled && IsConfigured(config))
        {
            try { stop?.Cancel(); } catch { /* the previous loop is already stopping */ }
            stop?.Dispose();
            registry = new NimbusRegistryClient(config);
            stop = new CancellationTokenSource();
            if (startUnix == 0) startUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var stopToken = stop.Token;
            heartbeatTask = Task.Run(() => HeartbeatLoopAsync(stopToken), stopToken);
            LastStatus = "starting";
        }
        else
        {
            try { stop?.Cancel(); } catch { /* the previous loop is already stopping */ }
            stop?.Dispose();
            stop = null;
            registry = null;
            LastStatus = config.Enabled ? "misconfigured" : "disabled";
            // A reload that leaves the mod unwired used to say nothing at all: the command's own
            // reply repeats the config back, which reads like success. This is the operator who
            // just edited the file, so it is the best moment to name what is still missing.
            if (config.Enabled) WarnUnconfigured();
        }
        old?.Dispose();
    }

    private void OnPlayerJoin(IServerPlayer player)
    {
        // Always run when registry is wired — stores forwarding data even when gating is off.
        if (registry == null) return;
        // No token: the gating check must be scheduled even if the mod is stopping, since a
        // check that never runs admits a player who should have been kicked.
        _ = Task.Run(() => CheckForwardingAsync(player), CancellationToken.None);
    }

    private async Task CheckForwardingAsync(IServerPlayer player)
    {
        // Capture before await — player object may be partially cleaned up by the time
        // the network call returns if VS processes a voluntary disconnect in the meantime.
        string playerName = player.PlayerName;
        string playerUid = player.PlayerUID;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(config.RegistryHttpTimeoutSeconds + 1));
            // Load-bearing despite the analyser: registry is a nullable field with no assignment
            // in this method, so removing it is a CS8602. The caller only reaches here once
            // StartServerSide has constructed it.
            var reservation = await registry!.ConsumeReservationByUidAsync(playerUid, config.ServerId, cts.Token) // NOSONAR
                .ConfigureAwait(false);

            if (reservation != null)
            {
                // Store the forwarded identity so other systems can trust the real IP.
                forwarding[playerUid] = reservation;
                var realIp = string.IsNullOrEmpty(reservation.RealRemoteIp) ? "?" : reservation.RealRemoteIp;
                api?.Logger.Notification($"[Nimbus] {playerName} forwarded from {realIp}");

                // Commit half of the seamless handshake (#19): the source backend sent
                // NimbusSeamlessPrepare with this transferId and can no longer reach the
                // client, so the target closes the loop once the join consumed the
                // reservation. Sent from the game thread; SendPacket is not thread-safe.
                // Clients without the Nimbus channel never registered it and see nothing.
                if (!string.IsNullOrEmpty(reservation.ClientTransferId))
                {
                    var transferId = reservation.ClientTransferId;
                    api?.Event.EnqueueMainThreadTask(() =>
                    {
                        TrySendSeamlessCommit(player, transferId);
                        LastSeamlessCommit = $"{playerName} ({transferId})";
                        api?.Logger.Notification($"[Nimbus] seamless commit sent to {playerName} ({transferId})");
                    }, "nimbus-seamless-commit");
                }
            }
            else if (config.ReservationRequired)
            {
                api?.Logger.Notification($"[Nimbus] {playerName} blocked — no valid proxy reservation");
                KickForReservation(player);
            }
        }
        catch (OperationCanceledException)
        {
            // Registry unreachable (timeout; TaskCanceledException derives from this). Fail open
            // by default so an outage does not lock everyone out; fail closed only when the
            // operator opted in on a gated network.
            if (config.ReservationRequired && config.FailClosedWhenRegistryUnreachable)
            {
                api?.Logger.Warning($"[Nimbus] {playerName} blocked — registry unreachable (fail-closed)");
                KickForReservation(player);
            }
        }
        catch (Exception ex)
        {
            api?.Logger.Warning($"[Nimbus] Forwarding check failed for {playerName}: {ex.Message}");
            if (config.ReservationRequired && config.FailClosedWhenRegistryUnreachable)
            {
                api?.Logger.Warning($"[Nimbus] {playerName} blocked — registry error (fail-closed)");
                KickForReservation(player);
            }
        }
    }

    private void KickForReservation(IServerPlayer player)
    {
        const string reason = "Direct connections are not permitted. Please connect via the Nimbus proxy.";
        // CheckForwardingAsync resumes on a thread-pool thread, since it runs under Task.Run and
        // awaits with ConfigureAwait false. The server API is not safe to call off the game
        // thread, so hand the kick back.
        api?.Event.EnqueueMainThreadTask(() =>
        {
            // The player may have quit on their own between the check and this callback, in which
            // case both calls throw on a half-cleaned player object and the kick is moot.
            try { player.SendMessage(0, reason, EnumChatType.Notification); } catch { /* already gone */ }
            try { player.Disconnect(reason); } catch { /* already gone */ }
        }, "nimbus-reservation-kick");
    }

    // Returns Nimbus forwarding data for a player who joined via the proxy.
    // Null if the player connected directly or the registry is not configured.
    public TransferReservation? GetForwardedPlayer(string playerUid)
        => forwarding.TryGetValue(playerUid, out var r) ? r : null;

    private void RegisterCommands(ICoreServerAPI api)
    {
        var parsers = api.ChatCommands.Parsers;
        api.ChatCommands.GetOrCreate("nimbus")
            .WithDescription("Nimbus backend commands")
            .RequiresPrivilege(Privilege.controlserver)
            .BeginSubCommand("status")
                .HandleWith(_ => StatusCommand())
            .EndSubCommand()
            .BeginSubCommand("servers")
                .HandleWith(_ => ServersCommand())
            .EndSubCommand()
            .BeginSubCommand("reload")
                .HandleWith(_ => ReloadConfigCommand())
            .EndSubCommand()
            .BeginSubCommand("send")
                .WithArgs(parsers.Word("player"), parsers.Word("serverId"), parsers.OptionalAll("reason"))
                .HandleWith(SendCommand)
            .EndSubCommand();

        api.ChatCommands.GetOrCreate("server")
            .WithDescription("Move yourself to another Nimbus backend")
            // Without a required privilege the engine refuses to execute the command at
            // all ("Incomplete command"); chat matches the /join alias below.
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .WithArgs(parsers.Word("serverId"))
            .HandleWith(SelfServerCommand);

        api.ChatCommands.GetOrCreate("join")
            .WithDescription("Join a Nimbus backend by name")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .WithArgs(parsers.Word("serverId"))
            .HandleWith(SelfServerCommand);

        RegisterShortcutCommands(api);
    }

    // Config-driven shortcuts (/hub, /creative, ...). Registered once at startup: Vintage Story
    // has no way to unregister a chat command, so a /nimbus reload picks up target and privilege
    // changes for shortcuts that already exist, but adding or removing one needs a restart.
    private void RegisterShortcutCommands(ICoreServerAPI api)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "server", "join", "nimbus" };

        foreach (var shortcut in config.ShortcutCommands)
        {
            if (!shortcut.IsUsable())
            {
                api.Logger.Warning($"[Nimbus] ignoring shortcut command with no name or no targets");
                continue;
            }
            if (!taken.Add(shortcut.Name))
            {
                api.Logger.Warning($"[Nimbus] ignoring duplicate shortcut command '/{shortcut.Name}'");
                continue;
            }
            if (api.ChatCommands.Get(shortcut.Name) != null)
            {
                // Never shadow an existing command: /tp for instance is vanilla teleport, and
                // hijacking it would break coordinate teleports.
                api.Logger.Warning($"[Nimbus] shortcut '/{shortcut.Name}' already exists as another command, skipping");
                continue;
            }

            string name = shortcut.Name;
            api.ChatCommands.GetOrCreate(name)
                .WithDescription(shortcut.Description)
                .RequiresPrivilege(shortcut.Privilege)
                .RequiresPlayer()
                .HandleWith(args => ShortcutCommandHandler(name, args));

            api.Logger.Notification($"[Nimbus] shortcut /{name} -> {string.Join(" then ", shortcut.Targets)}");
        }
    }

    private TextCommandResult ShortcutCommandHandler(string name, TextCommandCallingArgs args)
    {
        if (!config.AllowPlayerServerCommand) return TextCommandResult.Error("Player server switching is disabled.");
        if (args.Caller.Player is not IServerPlayer player) return TextCommandResult.Error("Run this command in-game.");

        // Re-read from config on every call so /nimbus reload can retarget a shortcut.
        var shortcut = config.ShortcutCommands.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (shortcut == null || !shortcut.IsUsable())
            return TextCommandResult.Error($"/{name} is no longer configured.");

        // The engine-level RequiresPrivilege gate was baked in at registration and cannot be
        // changed afterwards, so an operator tightening a shortcut from "chat" to
        // "controlserver" and reloading would otherwise keep letting everyone through: a
        // fail-open on exactly the edit made to lock something down. Re-check the current
        // privilege here so tightening takes effect on reload. Loosening still needs a restart,
        // because the boot-time gate rejects the caller before this handler runs; that asymmetry
        // is deliberate, it fails closed.
        if (!string.IsNullOrWhiteSpace(shortcut.Privilege) && !player.HasPrivilege(shortcut.Privilege))
            return TextCommandResult.Error($"You do not have permission to use /{name}.");

        var resolved = ResolveShortcutTarget(shortcut, out string? whyNot);
        if (resolved == null)
            return TextCommandResult.Error(whyNot ?? $"No server available for /{name} right now.");

        return BeginTransfer(player, resolved.ServerId, $"shortcut /{name}", "player:" + player.PlayerUID);
    }

    // First target that is registered, healthy, and not this server. Returns null with a reason
    // when the whole chain is unusable, so the player gets told why instead of nothing happening.
    private BackendSnapshot? ResolveShortcutTarget(ShortcutCommand shortcut, out string? whyNot)
    {
        whyNot = null;
        bool alreadyHere = false;

        foreach (var targetId in shortcut.Targets)
        {
            if (string.Equals(targetId, config.ServerId, StringComparison.OrdinalIgnoreCase))
            {
                // Being on the first choice already is the common case for /lobby: remember it,
                // but keep looking in case a later target is a valid elsewhere.
                alreadyHere = true;
                continue;
            }

            var candidate = LastSnapshot.Backends.FirstOrDefault(b =>
                string.Equals(b.ServerId, targetId, StringComparison.OrdinalIgnoreCase));
            if (candidate == null || candidate.Stale || candidate.Maintenance) continue;
            return candidate;
        }

        whyNot = alreadyHere
            ? "You are already there."
            : $"No server available for /{shortcut.Name} right now.";
        return null;
    }

    private TextCommandResult StatusCommand()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Nimbus");
        sb.AppendLine("Mode: " + config.StatusSummary());
        sb.AppendLine("Last status: " + LastStatus);
        if (LastSnapshotUtc != default)
        {
            var age = (int)(DateTime.UtcNow - LastSnapshotUtc).TotalSeconds;
            sb.AppendLine($"Snapshot: {LastSnapshot.Backends.Count} backends, {LastSnapshot.TotalPlayers}/{LastSnapshot.TotalCapacity} players, {age}s old");
        }
        if (LastSeamlessCommit.Length > 0)
            sb.AppendLine("Last seamless commit: " + LastSeamlessCommit);
        if (LastSeamlessAbort.Length > 0)
            sb.AppendLine("Last seamless abort: " + LastSeamlessAbort);
        return TextCommandResult.Success(sb.ToString());
    }

    private TextCommandResult ServersCommand()
    {
        if (!config.Enabled) return TextCommandResult.Error("Nimbus is disabled.");
        if (LastSnapshot.Backends.Count == 0) return TextCommandResult.Success("No backend snapshot yet.");

        var sb = new StringBuilder();
        foreach (var backend in LastSnapshot.Backends.OrderBy(b => b.ServerId))
        {
            string flags = string.Join(" ", new[]
            {
                backend.Stale ? "stale" : "",
                backend.Maintenance ? "maintenance" : "",
                backend.ReservationRequired ? "reservation" : ""
            }.Where(s => s.Length > 0));
            sb.AppendLine($"{backend.ServerId}: {backend.DisplayName} {backend.PublicHost}:{backend.PublicPort} {backend.Players}/{backend.MaxPlayers} {flags}".TrimEnd());
        }
        return TextCommandResult.Success(sb.ToString());
    }

    private TextCommandResult SendCommand(TextCommandCallingArgs args)
    {
        string playerName = args[0] as string ?? "";
        string targetServerId = args[1] as string ?? "";
        string? reason = args.Parsers[2].IsMissing ? null : args[2] as string;

        var player = api?.Server.Players.FirstOrDefault(p => string.Equals(p.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));
        if (player == null) return TextCommandResult.Error($"Player '{playerName}' is not online.");
        return BeginTransfer(player, targetServerId, reason, "admin:" + (args.Caller?.GetName() ?? "console"));
    }

    private TextCommandResult SelfServerCommand(TextCommandCallingArgs args)
    {
        if (!config.AllowPlayerServerCommand) return TextCommandResult.Error("Player server switching is disabled.");
        if (args.Caller.Player is not IServerPlayer player) return TextCommandResult.Error("Run this command in-game.");
        return BeginTransfer(player, args[0] as string ?? "", null, "player:" + player.PlayerUID);
    }

    // Validates eagerly then fires the async handshake off the game tick thread so .Wait()
    // never blocks the thread that also needs to dispatch incoming ready-ack packets.
    private TextCommandResult BeginTransfer(IServerPlayer player, string targetServerId, string? reason, string requestedBy)
    {
        if (!config.Enabled) return TextCommandResult.Error("Nimbus is disabled.");
        if (string.Equals(targetServerId, config.ServerId, StringComparison.OrdinalIgnoreCase))
            return TextCommandResult.Error($"You are already on '{targetServerId}'.");

        var target = LastSnapshot.Backends.FirstOrDefault(b => string.Equals(b.ServerId, targetServerId, StringComparison.OrdinalIgnoreCase));
        if (target == null) return TextCommandResult.Error($"Target '{targetServerId}' is not in the registry snapshot.");
        if (target.Stale) return TextCommandResult.Error($"Target '{targetServerId}' is stale.");
        if (target.Maintenance) return TextCommandResult.Error($"Target '{targetServerId}' is in maintenance.");

        // No token: the command below already told the player the transfer is under way, so
        // the handshake has to be scheduled rather than dropped by a concurrent stop.
        _ = Task.Run(() => RunTransferAsync(player, target, reason, requestedBy), CancellationToken.None);
        return TextCommandResult.Success($"Transferring {player.PlayerName} to {target.DisplayName}...");
    }

    private async Task RunTransferAsync(IServerPlayer player, BackendSnapshot target, string? reason, string requestedBy)
    {
        bool seamlessMode = string.Equals(config.TransferMode, "seamless", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(config.TransferMode, "splice", StringComparison.OrdinalIgnoreCase);
        string? transferId = null;

        try
        {
            if (seamlessMode)
            {
                if (channel == null)
                {
                    api?.Logger.Warning("Nimbus seamless handshake failed: channel is null");
                    return;
                }

                transferId = NewTransferId();
                var pending = new PendingSeamlessHandshake(player.PlayerUID);
                pendingSeamless[transferId] = pending;
                int expirySeconds = ComputePrepareExpirySeconds();

                try
                {
                    channel.SendPacket(new NimbusSeamlessPrepare
                    {
                        TransferId = transferId,
                        TargetServerId = target.ServerId,
                        Reason = reason ?? "server transfer",
                        ExpiresInSeconds = expirySeconds,
                    }, player);

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(config.SeamlessPrepareAckTimeoutSeconds));
                    try
                    {
                        await pending.Ready.Task.WaitAsync(cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        TrySendSeamlessAbort(player, transferId, "client did not acknowledge seamless prepare in time");
                        api?.Logger.Warning($"Nimbus seamless timed out waiting for ready ack from {player.PlayerName}");
                        return;
                    }
                }
                finally
                {
                    pendingSeamless.TryRemove(transferId, out _);
                }

                inFlightSeamless[transferId] = new InFlightSeamlessTransfer(
                    player.PlayerUID,
                    player,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expirySeconds);
            }

            Task<TransferIntentResponse> task = string.IsNullOrWhiteSpace(transferId)
                ? RequestTransferAsync(player, target.ServerId, reason, requestedBy)
                : RequestTransferWithClientTransferIdAsync(player, target.ServerId, reason, requestedBy, transferId);

            var response = await task.ConfigureAwait(false);
            if (!response.Ok)
            {
                if (!string.IsNullOrWhiteSpace(transferId))
                {
                    inFlightSeamless.TryRemove(transferId, out _);
                    TrySendSeamlessAbort(player, transferId, "registry rejected seamless transfer");
                }
                api?.Logger.Warning($"Nimbus transfer for {player.PlayerName} rejected: {response.Error}");
                return;
            }

            api?.Logger.Notification($"Nimbus transfer queued: {player.PlayerName} -> {target.ServerId}");
        }
        catch (Exception ex)
        {
            api?.Logger.Warning($"Nimbus transfer async error: {ex.GetType().Name}: {ex.Message}");
            if (!string.IsNullOrWhiteSpace(transferId))
            {
                inFlightSeamless.TryRemove(transferId, out _);
                TrySendSeamlessAbort(player, transferId, "internal error");
            }
        }
    }

    private void ProcessFailureNotices(IEnumerable<TransferFailed>? notices)
    {
        if (api == null || notices == null) return;

        foreach (var notice in notices)
        {
            if (notice is null || string.IsNullOrWhiteSpace(notice.ClientTransferId)
                || !string.Equals(notice.SourceServerId, config.ServerId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!inFlightSeamless.TryRemove(notice.ClientTransferId, out var transfer))
                continue;

            string message = string.IsNullOrWhiteSpace(notice.Reason)
                ? "seamless transfer failed"
                : notice.Reason;
            api.Event.EnqueueMainThreadTask(() =>
            {
                TrySendSeamlessAbort(transfer.Player, notice.ClientTransferId, message);
            }, "nimbus-seamless-failure");
        }
    }

    private void PruneInFlightSeamless()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var pair in inFlightSeamless)
        {
            if (pair.Value.ExpiresAtUnix <= now && inFlightSeamless.TryRemove(pair.Key, out _))
                api?.Logger.Warning($"Nimbus seamless transfer expired while waiting for dispatch: {pair.Key}");
        }
    }

    private void RemoveInFlightForPlayer(string playerUid)
    {
        foreach (var pair in inFlightSeamless.Where(pair =>
            string.Equals(pair.Value.PlayerUid, playerUid, StringComparison.OrdinalIgnoreCase)))
        {
            inFlightSeamless.TryRemove(pair.Key, out _);
        }
    }

    private int ComputePrepareExpirySeconds()
    {
        int prepareAck = Math.Max(1, config.SeamlessPrepareAckTimeoutSeconds);
        int readyWait = Math.Max(1, Volatile.Read(ref advertisedReadyWaitTimeoutSeconds));
        int heartbeat = Math.Max(1, Volatile.Read(ref heartbeatIntervalSeconds));
        long total = (long)prepareAck + readyWait + heartbeat + 5;
        return (int)Math.Min(int.MaxValue - 5L, total);
    }

    private async Task<TransferIntentResponse> RequestTransferWithClientTransferIdAsync(IServerPlayer player,
        string targetServerId, string? reason, string requestedBy, string transferId)
    {
        if (!config.Enabled || registry == null)
            return new TransferIntentResponse { Ok = false, Error = "Nimbus server mod is disabled" };

        var req = new TransferIntentRequest
        {
            PlayerUid = player.PlayerUID,
            PlayerName = player.PlayerName,
            SourceServerId = config.ServerId,
            TargetServerId = targetServerId,
            Mode = config.TransferMode,
            Reason = reason,
            RequestedBy = requestedBy,
            TtlSeconds = 30,
            ClientTransferId = transferId,
            ClientSupportsSeamlessTransfers = HasSeamlessCapability(player)
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(config.RegistryHttpTimeoutSeconds + 1));
        return await registry.PostTransferIntentAsync(req, cts.Token).ConfigureAwait(false)
            ?? new TransferIntentResponse { Ok = false, Error = "no response from registry" };
    }

    private static string NewTransferId()
    {
        Span<byte> bytes = stackalloc byte[10];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    private void TrySendSeamlessCommit(IServerPlayer player, string transferId)
    {
        try { channel?.SendPacket(new NimbusSeamlessCommit { TransferId = transferId }, player); }
        catch (Exception ex) { api?.Logger.Warning($"Nimbus seamless commit send failed: {ex.Message}"); }
    }

    private void TrySendSeamlessAbort(IServerPlayer player, string transferId, string message)
    {
        try
        {
            if (channel == null) return;
            channel.SendPacket(new NimbusSeamlessAbort { TransferId = transferId, Message = message }, player);
            LastSeamlessAbort = $"{player.PlayerName} ({transferId}): {message}";
            api?.Logger.Warning($"Nimbus: seamless transfer aborted for {player.PlayerName}: {message}");
        }
        catch (Exception ex) { api?.Logger.Warning($"Nimbus: seamless abort send failed: {ex.Message}"); }
    }

    private sealed record NimbusClientCapability(int ProtocolVersion, bool SupportsSeamlessTransfers);
    private sealed record PendingSeamlessHandshake(string PlayerUid)
    {
        public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record InFlightSeamlessTransfer(string PlayerUid, IServerPlayer Player, long ExpiresAtUnix);
}
