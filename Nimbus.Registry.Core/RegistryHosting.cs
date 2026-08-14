using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nimbus.Registry.MasterServer;
using Nimbus.Registry.Services;

namespace Nimbus.Registry;

// Wires the registry services (BackendRegistry, ReservationStore, TransferIntentStore,
// TransferFailureStore,
// NonceCache, sweeper, optional master-server broadcaster) into a WebApplicationBuilder,
// and maps the HMAC-authed /api/* endpoints. Used by the standalone Nimbus.Registry exe
// and by Nimbus.Proxy's embedded registry mode (single-process deployments).
public static class RegistryHosting
{
    public static void AddNimbusRegistry(this WebApplicationBuilder builder, RegistryConfig cfg, bool withMasterServer = true)
    {
        builder.Services.AddSingleton(cfg);
        // TryAdd, not Add: a host that embeds the registry may register its own clock
        // before calling this, and the last registration would otherwise win and drop it.
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<BackendRegistry>();
        builder.Services.AddSingleton<ReservationStore>();
        builder.Services.AddSingleton<TransferIntentStore>();
        builder.Services.AddSingleton<TransferFailureStore>();
        builder.Services.AddSingleton<NonceCache>();
        builder.Services.AddSingleton(sp => new RegistryStores
        {
            Backends = sp.GetRequiredService<BackendRegistry>(),
            Reservations = sp.GetRequiredService<ReservationStore>(),
            Intents = sp.GetRequiredService<TransferIntentStore>(),
            Failures = sp.GetRequiredService<TransferFailureStore>(),
            Bans = sp.GetRequiredService<BanStore>(),
            Whitelist = sp.GetRequiredService<WhitelistStore>(),
            Tokens = sp.GetRequiredService<ApiTokenStore>(),
        });
        // The two moderation lists are the only state worth keeping across a restart, and they
        // are built by hand rather than by type so the state directory from config reaches them.
        builder.Services.AddSingleton(sp => new BanStore(sp.GetRequiredService<TimeProvider>(),
            RegistryStateFiles.Bans(cfg.StateDir, StateLogger(sp))));
        builder.Services.AddSingleton(sp => new WhitelistStore(sp.GetRequiredService<TimeProvider>(),
            RegistryStateFiles.Whitelist(cfg.StateDir, StateLogger(sp))));
        // Third: the issued scoped credentials. A token that died with the registry process would
        // be unusable, and a revocation that died with it would be worse.
        builder.Services.AddSingleton(sp => new ApiTokenStore(sp.GetRequiredService<TimeProvider>(),
            RegistryStateFiles.Tokens(cfg.StateDir, StateLogger(sp))));
        builder.Services.AddSingleton<ApiTokenService>();
        // Named rather than resolved by type: the limiter has a second constructor taking the
        // rate directly, for the tests that pin its arithmetic, and which of the two the container
        // would pick is not a question worth leaving open.
        builder.Services.AddSingleton(sp => new ApiTokenRateLimiter(cfg, sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<ReservationService>();
        builder.Services.AddHostedService<RegistrySweeper>();
        if (withMasterServer) builder.Services.AddHostedService<MasterServerBroadcaster>();
    }

    private static ILogger StateLogger(IServiceProvider sp)
        => sp.GetRequiredService<ILoggerFactory>().CreateLogger("RegistryState");

    public static void UseNimbusRegistry(this WebApplication app)
    {
        // In front of the HMAC one, and only interested in requests carrying a bearer token.
        // Everything else reaches HmacAuthMiddleware exactly as it did before.
        app.UseMiddleware<TokenAuthMiddleware>();
        app.UseMiddleware<HmacAuthMiddleware>();
        Endpoints.Map(app);
    }
}

// Background sweep: prune stale backends, expired reservations, and old nonces.
public sealed class RegistrySweeper : BackgroundService
{
    private readonly RegistryStores _stores;
    private readonly NonceCache _nonces;
    private readonly ILogger<RegistrySweeper> _log;

    public RegistrySweeper(RegistryStores stores, NonceCache nonces, ILogger<RegistrySweeper> log)
    { _stores = stores; _nonces = nonces; _log = log; }

    internal int SweepOnce()
    {
        int b = _stores.Backends.Prune();
        int r = _stores.Reservations.Prune();
        int i = _stores.Intents.Prune();
        int f = _stores.Failures.Prune();
        int n = _nonces.Prune();
        int x = _stores.Bans.Prune();
        int w = _stores.Whitelist.Prune();
        int dropped = b + r + i + f + n + x + w;
        if (dropped > 0 && _log.IsEnabled(LogLevel.Debug))
            _log.LogDebug("sweep: dropped backends={B} reservations={R} intents={I} failures={F} nonces={N} bans={X} whitelist={W}", b, r, i, f, n, x, w);
        return dropped;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromSeconds(15);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { SweepOnce(); }
            catch (Exception ex)
            {
                _log.LogError(ex, "sweep failed");
            }
            // Host shutdown cancels the wait, and the loop condition above reads the same token
            // on the next pass, so this exits on its own without the cancellation going anywhere.
            try { await Task.Delay(period, stoppingToken); } catch (TaskCanceledException) { /* shutting down */ }
        }
    }
}
