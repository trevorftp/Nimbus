using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nimbus.Registry;
using Nimbus.Registry.Services;

namespace Nimbus.Proxy;

internal sealed class ProxyRegistryHost : IAsyncDisposable
{
    private readonly WebApplication? embeddedHost;

    private ProxyRegistryHost(IRegistryClient? client, WebApplication? embeddedHost)
    {
        Client = client;
        this.embeddedHost = embeddedHost;
    }

    public IRegistryClient? Client { get; }

    public static ProxyRegistryHost Build(ProxyConfig cfg, CancellationToken stopToken)
    {
        var mode = (cfg.Registry.Mode ?? "disabled").Trim().ToLowerInvariant();
        switch (mode)
        {
            case "disabled":
            case "":
                Log.Info("registry: disabled");
                return new ProxyRegistryHost(null, null);

            case "remote":
                if (string.IsNullOrWhiteSpace(cfg.Registry.Url) || string.IsNullOrWhiteSpace(cfg.Registry.SharedSecret))
                {
                    Log.Warn("registry.mode = remote but url/shared_secret is unset; treating as disabled");
                    return new ProxyRegistryHost(null, null);
                }
                Log.Info($"registry: remote url={cfg.Registry.Url} proxy_id={cfg.Registry.ProxyId} fail_on_error={cfg.Registry.FailOnError}");
                return new ProxyRegistryHost(new HttpRegistryClient(cfg.Registry), null);

            case "embedded":
                return BuildEmbedded(cfg, stopToken);

            default:
                Log.Warn($"registry.mode = '{mode}' is not recognized; treating as disabled");
                return new ProxyRegistryHost(null, null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (embeddedHost != null)
        {
            // The proxy is on its way out and the embedded registry dies with the process. Two
            // seconds to drain in-flight heartbeats, then it goes down the hard way; neither step
            // has a caller left to report to.
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await embeddedHost.StopAsync(stopCts.Token).ConfigureAwait(false); } catch { /* drain timed out */ }
            try { await embeddedHost.DisposeAsync().ConfigureAwait(false); } catch { /* half-stopped host */ }
        }

        if (Client is IDisposable disposable)
            disposable.Dispose();
    }

    // Same rule as ProxyRuntime uses for the drain flags: a relative directory hangs off the
    // proxy executable, so a proxy started from a service manager with some other working
    // directory still finds yesterday's bans.
    private static string ResolveStateDir(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return AppContext.BaseDirectory;
        return Path.IsPathRooted(dir) ? dir : Path.Combine(AppContext.BaseDirectory, dir);
    }

    private static ProxyRegistryHost BuildEmbedded(ProxyConfig cfg, CancellationToken stopToken)
    {
        var coreCfg = new Nimbus.Registry.RegistryConfig
        {
            BindUrl = cfg.Registry.EmbeddedBind,
            SharedSecret = cfg.Registry.EmbeddedSharedSecret,
            BackendStaleSeconds = cfg.Registry.BackendStaleSeconds,
            BackendDropSeconds = cfg.Registry.BackendDropSeconds,
            NonceWindowSeconds = cfg.Registry.NonceWindowSeconds,
            MaxReservationTtlSeconds = cfg.Registry.MaxReservationTtlSeconds,
            SeamlessReadyWaitTimeoutSeconds = cfg.Registry.SeamlessReadyWaitTimeoutSeconds,
            LogHeartbeats = false,
            StateDir = ResolveStateDir(cfg.Registry.EmbeddedStateDir),
            ApiTokens = new ApiTokensConfig
            {
                Enabled = cfg.Registry.ApiTokensEnabled,
                RateLimitPerMinute = cfg.Registry.ApiTokensRateLimitPerMinute,
                TrustForwardedProto = cfg.Registry.ApiTokensTrustForwardedProto,
            },
        };
        coreCfg.Identity.AdvertiseOnMasterServer = cfg.Registry.AdvertiseOnMasterServer;

        if (!string.IsNullOrWhiteSpace(cfg.Registry.EmbeddedBind))
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls(cfg.Registry.EmbeddedBind);
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft", LogLevel.None);
            builder.Logging.AddFilter("System", LogLevel.None);
            builder.Logging.AddProvider(new RegistryConsoleLoggerProvider());
            builder.AddNimbusRegistry(coreCfg, withMasterServer: cfg.Registry.AdvertiseOnMasterServer);

            var app = builder.Build();
            app.UseNimbusRegistry();
            try
            {
                app.StartAsync(stopToken).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"embedded registry failed to start on '{cfg.Registry.EmbeddedBind}': {ex.Message}", ex);
            }
            Log.Info($"registry: embedded http bind={cfg.Registry.EmbeddedBind} proxy_id={cfg.Registry.ProxyId} state_dir={coreCfg.StateDir}");

            var stores = new RegistryStores
            {
                Backends = app.Services.GetRequiredService<BackendRegistry>(),
                Reservations = app.Services.GetRequiredService<ReservationStore>(),
                Intents = app.Services.GetRequiredService<TransferIntentStore>(),
                Failures = app.Services.GetRequiredService<TransferFailureStore>(),
                Bans = app.Services.GetRequiredService<BanStore>(),
                Whitelist = app.Services.GetRequiredService<WhitelistStore>(),
                Tokens = app.Services.GetRequiredService<ApiTokenStore>(),
            };
            return new ProxyRegistryHost(new InProcRegistryClient(stores, cfg.Registry), app);
        }

        // No listener means no container either, so the stores are built here with the same
        // state files AddNimbusRegistry would have given them. Bans have to outlive a restart
        // in this mode as much as in any other; it is the default one.
        var unhosted = new RegistryStores
        {
            Backends = new BackendRegistry(coreCfg),
            Reservations = new ReservationStore(),
            Intents = new TransferIntentStore(),
            Failures = new TransferFailureStore(),
            Bans = new BanStore(state: RegistryStateFiles.Bans(coreCfg.StateDir)),
            Whitelist = new WhitelistStore(state: RegistryStateFiles.Whitelist(coreCfg.StateDir)),
            // Tokens are worth minting in this mode even though nothing here answers a bearer
            // header: an operator preparing a bot on a proxy with no listener still needs the
            // credential, and the registry that will answer it reads the same file.
            Tokens = new ApiTokenStore(state: RegistryStateFiles.Tokens(coreCfg.StateDir)),
        };
        Log.Info($"registry: embedded (no http listener) proxy_id={cfg.Registry.ProxyId} state_dir={coreCfg.StateDir}");
        return new ProxyRegistryHost(new InProcRegistryClient(unhosted, cfg.Registry), null);
    }
}
