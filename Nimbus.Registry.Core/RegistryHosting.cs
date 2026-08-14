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
    private readonly BackendRegistry _backends;
    private readonly ReservationStore _reservations;
    private readonly TransferIntentStore _intents;
    private readonly TransferFailureStore _failures;
    private readonly NonceCache _nonces;
    private readonly BanStore _bans;
    private readonly WhitelistStore _whitelist;
    private readonly ILogger<RegistrySweeper> _log;

    public RegistrySweeper(BackendRegistry b, ReservationStore r, TransferIntentStore i, TransferFailureStore failures,
        NonceCache n, BanStore bans, WhitelistStore whitelist, ILogger<RegistrySweeper> log)
    { _backends = b; _reservations = r; _intents = i; _failures = failures; _nonces = n; _bans = bans; _whitelist = whitelist; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromSeconds(15);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int b = _backends.Prune();
                int r = _reservations.Prune();
                int i = _intents.Prune();
                int f = _failures.Prune();
                int n = _nonces.Prune();
                int x = _bans.Prune();
                int w = _whitelist.Prune();
                // The level check is not ceremony here: the registry runs at minimum level Warning
                // with a provider that accepts nothing below it, so this line never prints, and
                // without the check the seven counters are boxed into an object[] every fifteen
                // seconds for a message that is discarded on the way out.
                if (b + r + i + f + n + x + w > 0 && _log.IsEnabled(LogLevel.Debug))
                    _log.LogDebug("sweep: dropped backends={B} reservations={R} intents={I} failures={F} nonces={N} bans={X} whitelist={W}", b, r, i, f, n, x, w);
            }
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
