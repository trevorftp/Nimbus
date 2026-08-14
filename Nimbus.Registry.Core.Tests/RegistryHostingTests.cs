using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nimbus.Registry.Services;
using Xunit;

namespace Nimbus.Registry.Core.Tests;

/// <summary>Covers how AddNimbusRegistry wires the clock into the container.</summary>
public class RegistryHostingTests
{
    private static WebApplicationBuilder NewBuilder()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        return builder;
    }

    [Fact]
    public void AddNimbusRegistry_RegistersTheSystemClock_WhenTheHostHasNone()
    {
        var builder = NewBuilder();
        builder.AddNimbusRegistry(new RegistryConfig(), withMasterServer: false);

        using var app = builder.Build();

        Assert.Same(TimeProvider.System, app.Services.GetRequiredService<TimeProvider>());
        Assert.NotNull(app.Services.GetRequiredService<RegistryStores>());
    }

    [Fact]
    public void AddNimbusRegistry_KeepsAClockTheHostRegisteredFirst()
    {
        // A host that embeds the registry may own the clock (tests, or a process that
        // wants one clock across several subsystems). TryAdd inside AddNimbusRegistry is
        // what preserves it; a plain Add would register last and win.
        var hostClock = new FakeClock();
        var builder = NewBuilder();
        builder.Services.AddSingleton<TimeProvider>(hostClock);
        builder.AddNimbusRegistry(new RegistryConfig(), withMasterServer: false);

        using var app = builder.Build();

        Assert.Same(hostClock, app.Services.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void SweepOnce_PrunesFailuresAndLogsDebugDrops()
    {
        var clock = new FakeClock();
        var failures = new TransferFailureStore(clock, TimeSpan.FromSeconds(1));
        failures.Add(new Nimbus.Shared.Models.TransferFailed
        {
            SourceServerId = "source",
            ClientTransferId = "expired",
        });
        clock.Advance(TimeSpan.FromSeconds(2));
        var logger = new DebugLogger();
        var sweeper = CreateSweeper(clock, failures, logger);

        Assert.Equal(1, sweeper.SweepOnce());
        Assert.Equal(1, logger.DebugMessages);
    }

    [Fact]
    public void SweepOnce_DoesNotLogWhenNothingExpires()
    {
        var clock = new FakeClock();
        var logger = new DebugLogger();

        Assert.Equal(0, CreateSweeper(clock, new TransferFailureStore(clock), logger).SweepOnce());
        Assert.Equal(0, logger.DebugMessages);
    }

    [Fact]
    public void SweepOnce_SkipsDebugLogWhenDebugIsDisabled()
    {
        var clock = new FakeClock();
        var failures = new TransferFailureStore(clock, TimeSpan.FromSeconds(1));
        failures.Add(new Nimbus.Shared.Models.TransferFailed
        {
            SourceServerId = "source",
            ClientTransferId = "expired",
        });
        clock.Advance(TimeSpan.FromSeconds(2));
        var logger = new DebugLogger(debugEnabled: false);

        Assert.Equal(1, CreateSweeper(clock, failures, logger).SweepOnce());
        Assert.Equal(0, logger.DebugMessages);
    }

    private static RegistrySweeper CreateSweeper(
        FakeClock clock, TransferFailureStore failures, DebugLogger logger)
    {
        var cfg = new RegistryConfig();
        var stores = new RegistryStores
        {
            Backends = new BackendRegistry(cfg, clock),
            Reservations = new ReservationStore(clock),
            Intents = new TransferIntentStore(clock),
            Failures = failures,
            Bans = new BanStore(clock),
            Whitelist = new WhitelistStore(clock),
            Tokens = new ApiTokenStore(clock),
        };
        return new RegistrySweeper(stores, new NonceCache(cfg, clock), logger);
    }

    private sealed class DebugLogger : ILogger<RegistrySweeper>
    {
        private readonly bool debugEnabled;

        public DebugLogger(bool debugEnabled = true)
        {
            this.debugEnabled = debugEnabled;
        }

        public int DebugMessages { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => debugEnabled && logLevel == LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Debug) DebugMessages++;
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
