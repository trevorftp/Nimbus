using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// The check that stands between a mistyped nimbus.proxy.toml and a proxy that starts anyway.
/// Errors stop the boot and stop a hot reload; warnings let it through and go in the log. Which
/// of the two a given mistake earns is the whole point, so every case here pins the severity as
/// well as the message.
/// </summary>
public class ProxyConfigValidatorTests
{
    /// <summary>A config that passes clean, as the shipped defaults do, so a test only has to
    /// break the one thing it is about.</summary>
    private static ProxyConfig Valid()
    {
        var cfg = new ProxyConfig
        {
            Bind = "0.0.0.0:42420",
            Servers = new Dictionary<string, string> { ["hub"] = "127.0.0.1:42421" },
            Try = new List<string> { "hub" },
        };
        cfg.Registry.Mode = "disabled";
        cfg.Metrics.Enabled = false;
        cfg.Persistence.PersistDrainFlags = false;
        return cfg;
    }

    private static ProxyConfigValidation Validate(Action<ProxyConfig> break_)
    {
        var cfg = Valid();
        break_(cfg);
        return ProxyConfigValidator.Validate(cfg);
    }

    private static void AssertError(ProxyConfigValidation result, string fragment)
    {
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains(fragment, StringComparison.Ordinal));
    }

    private static void AssertWarning(ProxyConfigValidation result, string fragment)
    {
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Contains(result.Warnings, w => w.Contains(fragment, StringComparison.Ordinal));
    }

    [Fact]
    public void AConfigWithNothingWrongWithIt_PassesWithNothingToSay()
    {
        var result = ProxyConfigValidator.Validate(Valid());

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void TheBuiltInDefaults_PassTheirOwnValidator()
    {
        // What a first run writes, and equally what every key an operator left out of their file
        // resolves to. Until #87 this object failed the very check Program.Main runs on it two
        // lines after writing it: registry.embedded_bind defaulted to http://0.0.0.0:8765 while
        // registry.embedded_shared_secret defaulted to the literal placeholder, so README step 1
        // ended in exit 2 on a file the operator had never seen. The bind is loopback now and the
        // pair agrees.
        //
        // This is the assertion that keeps it that way: any future default a validator rule
        // refuses fails here rather than in a first-time operator's terminal.
        var defaults = ProxyConfigValidator.Validate(new ProxyConfig());

        Assert.True(defaults.IsValid, string.Join("; ", defaults.Errors));
        Assert.Empty(defaults.Errors);
        // A warning on a file nobody has edited yet is noise the operator can do nothing about.
        Assert.Empty(defaults.Warnings);
    }

    // ---- bind ----

    [Theory]
    [InlineData("", "bind: empty")]
    [InlineData("   ", "bind: empty")]
    [InlineData("42420", "must be 'host:port'")]
    [InlineData(":42420", "must be 'host:port'")]
    [InlineData("0.0.0.0:", "must be 'host:port'")]
    [InlineData("0.0.0.0:notaport", "invalid port")]
    [InlineData("0.0.0.0:0", "invalid port")]
    [InlineData("0.0.0.0:65536", "invalid port")]
    [InlineData("0.0.0.0:-1", "invalid port")]
    [InlineData("localhost:42420", "host must be an IP address")]
    public void ABindTheProxyCannotListenOn_IsAnError(string bind, string fragment)
    {
        // bind is resolved with IPAddress.Parse at startup, so anything this lets through and
        // the parser does not is a crash rather than a message.
        AssertError(Validate(cfg => cfg.Bind = bind), fragment);
    }

    [Theory]
    [InlineData("0.0.0.0:42420")]
    [InlineData("127.0.0.1:1")]
    [InlineData("[::]:42420")]
    [InlineData("[::1]:65535")]
    public void ABindTheProxyCanListenOn_IsAccepted(string bind)
    {
        var result = Validate(cfg => cfg.Bind = bind);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ---- servers ----

    [Fact]
    public void NoBackendsAtAll_IsAnError()
    {
        // Nothing to proxy to. The proxy would accept players and have nowhere to send them.
        AssertError(Validate(cfg => cfg.Servers = new Dictionary<string, string>()),
            "[servers] must contain at least one backend");
    }

    [Fact]
    public void AnEmptyServerId_IsAnError()
    {
        AssertError(Validate(cfg => cfg.Servers = new Dictionary<string, string> { [" "] = "127.0.0.1:42421" }),
            "empty server id");
    }

    [Fact]
    public void TwoServerIdsDifferingOnlyInCase_AreAnError()
    {
        // Every lookup in the proxy matches server ids case-insensitively, so these two are the
        // same backend to everything downstream and only one of them would ever be reachable.
        AssertError(Validate(cfg => cfg.Servers = new Dictionary<string, string>
        {
            ["hub"] = "127.0.0.1:42421",
            ["HUB"] = "127.0.0.1:42422",
        }), "duplicate server id");
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:")]
    [InlineData("127.0.0.1:port")]
    [InlineData("127.0.0.1:0")]
    public void ABackendAddressThatIsNotHostPort_IsAnError(string address)
    {
        AssertError(Validate(cfg => cfg.Servers = new Dictionary<string, string> { ["hub"] = address }),
            "servers.hub");
    }

    [Fact]
    public void ABackendNamedByHostname_IsAccepted()
    {
        // Backends are dialled by name at connect time, so unlike bind they do not have to be
        // literal addresses.
        var result = Validate(cfg => cfg.Servers = new Dictionary<string, string>
        {
            ["hub"] = "hub.internal:42421",
        });

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void ATryListNamingABackendThatDoesNotExist_IsAWarning()
    {
        // Unknown names are skipped at routing time rather than fatal, so this is a warning: the
        // proxy still works, using whatever else is in the list.
        AssertWarning(Validate(cfg => cfg.Try = new List<string> { "hub", "typo" }),
            "try references unknown server 'typo'");
    }

    [Fact]
    public void ABlankEntryInTheTryList_IsPassedOverSilently()
    {
        var result = Validate(cfg => cfg.Try = new List<string> { "hub", "", "   " });

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ATryListNamingABackendInAnotherCase_IsFine()
    {
        var result = Validate(cfg => cfg.Try = new List<string> { "HUB" });

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ProxyProtocolNamingABackendThatDoesNotExist_IsAWarning()
    {
        // The consequence of the typo is a backend that never gets its PROXY v2 header and so
        // sees every player as coming from the proxy's address.
        AssertWarning(Validate(cfg => cfg.ProxyProtocolServers = new List<string> { "typo" }),
            "proxy_protocol_servers references unknown server 'typo'");
    }

    [Fact]
    public void ABlankEntryInProxyProtocolServers_IsPassedOverSilently()
    {
        var result = Validate(cfg => cfg.ProxyProtocolServers = new List<string> { "" });

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void AForcedHostWithNoHostname_IsAWarning()
    {
        AssertWarning(Validate(cfg => cfg.ForcedHosts = new Dictionary<string, List<string>>
        {
            [" "] = new() { "hub" },
        }), "[forced-hosts] contains an empty hostname");
    }

    [Fact]
    public void AForcedHostPointingAtABackendThatDoesNotExist_IsAWarning()
    {
        AssertWarning(Validate(cfg => cfg.ForcedHosts = new Dictionary<string, List<string>>
        {
            ["play.example.net"] = new() { "hub", "typo" },
        }), "forced-hosts.play.example.net references unknown server 'typo'");
    }

    // ---- transfers ----

    [Theory]
    [InlineData("teleport")]
    [InlineData("")]
    public void ATransferModeThatIsNeitherRedirectNorSeamless_IsAnError(string mode)
    {
        AssertError(Validate(cfg => cfg.Transfers.DefaultMode = mode),
            "transfers.default_mode must be 'redirect' or 'seamless'");
    }

    [Theory]
    [InlineData("REDIRECT")]
    [InlineData("Redirect")]
    public void TheTransferMode_IsReadWithoutRegardToCase(string mode)
    {
        var result = Validate(cfg => cfg.Transfers.DefaultMode = mode);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void TheLegacySpliceMode_StillMeansSeamless()
    {
        // "splice" is what the mode was called before the rename. Reading it as unknown would
        // stop an existing config from booting after an upgrade.
        var result = Validate(cfg =>
        {
            cfg.Transfers.DefaultMode = "splice";
            cfg.Transfers.AllowSeamless = true;
        });

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void SeamlessAsTheDefaultWithSeamlessTurnedOff_IsAnError()
    {
        // Every transfer would be refused, which reads to an operator as transfers being broken
        // rather than as a config that contradicts itself.
        AssertError(Validate(cfg =>
        {
            cfg.Transfers.DefaultMode = "seamless";
            cfg.Transfers.AllowSeamless = false;
        }), "requires transfers.allow_seamless = true");
    }

    [Fact]
    public void SeamlessWithNoFallbackForVanillaClients_IsAWarning()
    {
        AssertWarning(Validate(cfg =>
        {
            cfg.Transfers.DefaultMode = "seamless";
            cfg.Transfers.AllowSeamless = true;
            cfg.Transfers.RequireSeamlessCapability = true;
            cfg.Transfers.FallbackToRedirectWhenSeamlessUnavailable = false;
        }), "will reject players without Nimbus client capability");
    }

    [Fact]
    public void SeamlessWithoutTheCapabilityHandshake_IsAWarning()
    {
        AssertWarning(Validate(cfg =>
        {
            cfg.Transfers.AllowSeamless = true;
            cfg.Transfers.RequireSeamlessCapability = false;
        }), "allows seamless requests without the Nimbus client handshake");
    }

    [Fact]
    public void TheUnsafeSpliceFlag_IsAWarningRatherThanARefusal()
    {
        // An operator running backends with auth verification off can use it. It is loud, not
        // forbidden.
        AssertWarning(Validate(cfg => cfg.Transfers.EnableUnsafeSeamlessSplice = true),
            "allows live splice without Nimbus client capability");
    }

    [Theory]
    [InlineData("play.example.net")]
    [InlineData("play.example.net:42420")]
    [InlineData("203.0.113.9")]
    [InlineData("203.0.113.9:42420")]
    [InlineData("[2001:db8::1]")]
    [InlineData("")]
    public void ARedirectAddressAVanillaClientCanDial_IsAccepted(string address)
    {
        var result = Validate(cfg => cfg.Transfers.RedirectAddress = address);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData("http://play.example.net")]
    [InlineData("play example net")]
    [InlineData("play.example.net:")]
    [InlineData(":42420")]
    [InlineData("play.example.net:0")]
    [InlineData("play.example.net:70000")]
    [InlineData("play.example.net:port")]
    public void ARedirectAddressAVanillaClientCannotDial_IsAnError(string address)
    {
        // The address is stamped into the redirect packet verbatim. A client that cannot parse it
        // does not fall back to anything, it fails to reconnect.
        AssertError(Validate(cfg => cfg.Transfers.RedirectAddress = address),
            "transfers.redirect_address must be 'host' or 'host:port'");
    }

    [Fact]
    public void ARedirectAddressWithSurroundingSpace_IsTrimmedRatherThanRefused()
    {
        var result = Validate(cfg => cfg.Transfers.RedirectAddress = "  play.example.net  ");

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ---- admin ----

    [Fact]
    public void AnAdminSocketOffTheLoopbackWithNoSecret_IsAnError()
    {
        // Anyone who can reach the port can kick, ban and reroute players.
        AssertError(Validate(cfg =>
        {
            cfg.Admin.Bind = "0.0.0.0:42499";
            cfg.Admin.Secret = "";
        }), "admin.bind is not loopback, so admin.secret must be set");
    }

    [Fact]
    public void AnAdminSocketOffTheLoopbackWithASecret_IsAccepted()
    {
        var result = Validate(cfg =>
        {
            cfg.Admin.Bind = "0.0.0.0:42499";
            cfg.Admin.Secret = "operator-secret";
        });

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData("127.0.0.1:42499")]
    [InlineData("127.0.0.5:42499")]
    [InlineData("[::1]:42499")]
    public void AnAdminSocketOnTheLoopbackWithNoSecret_IsAccepted(string bind)
    {
        var result = Validate(cfg =>
        {
            cfg.Admin.Bind = bind;
            cfg.Admin.Secret = "";
        });

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void AnAdminBindThatIsNotAnAddressAtAll_IsAnError()
    {
        AssertError(Validate(cfg => cfg.Admin.Bind = "not-an-endpoint"), "admin.bind");
    }

    [Fact]
    public void AnAdminSocketWithNoPermissionsGranted_IsAWarning()
    {
        // The socket comes up and refuses every command. Fatal would be wrong (it is a valid way
        // to stand the surface down) but silent would be worse.
        AssertWarning(Validate(cfg => cfg.Admin.GrantedPermissions = new List<string>()),
            "every admin command will be denied");
    }

    [Fact]
    public void WithTheAdminSocketOff_ItsOtherSettingsAreNotChecked()
    {
        // Nothing is listening, so a bind left pointing at the wrong thing costs nobody anything.
        var result = Validate(cfg =>
        {
            cfg.Admin.Enabled = false;
            cfg.Admin.Bind = "nonsense";
            cfg.Admin.GrantedPermissions = new List<string>();
        });

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Empty(result.Warnings);
    }

    // ---- registry ----

    [Theory]
    [InlineData("")]
    [InlineData("disabled")]
    [InlineData("embedded")]
    [InlineData("remote")]
    [InlineData("EMBEDDED")]
    [InlineData("  embedded  ")]
    public void ARegistryModeTheProxyKnows_IsAccepted(string mode)
    {
        var result = Validate(cfg =>
        {
            cfg.Registry.Mode = mode;
            cfg.Registry.Url = "https://registry.example";
            cfg.Registry.SharedSecret = "shared";
            cfg.Registry.EmbeddedBind = "";
        });

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void ARegistryModeTheProxyDoesNotKnow_IsAnError()
    {
        AssertError(Validate(cfg => cfg.Registry.Mode = "cloud"),
            "registry.mode must be 'disabled', 'embedded', or 'remote'");
    }

    [Fact]
    public void AnUnknownRegistryMode_StopsTheRestOfTheRegistryChecks()
    {
        // Nothing else in the block can be judged when the mode itself is meaningless, and a pile
        // of consequential errors buries the one that matters.
        var result = Validate(cfg =>
        {
            cfg.Registry.Mode = "cloud";
            cfg.Registry.ReservationTtlSeconds = 0;
        });

        Assert.Single(result.Errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AReservationTtlOfZeroOrLess_IsAnError(int ttl)
    {
        // Every reservation would be expired the moment it was minted.
        AssertError(Validate(cfg => cfg.Registry.ReservationTtlSeconds = ttl),
            "registry.reservation_ttl_seconds must be greater than zero");
    }

    [Fact]
    public void AMaxReservationTtlOfZeroOrLess_IsAnError()
    {
        AssertError(Validate(cfg => cfg.Registry.MaxReservationTtlSeconds = 0),
            "registry.max_reservation_ttl_seconds must be greater than zero");
    }

    [Fact]
    public void AReservationTtlAboveTheMaximum_IsAWarningBecauseItIsClamped()
    {
        AssertWarning(Validate(cfg =>
        {
            cfg.Registry.ReservationTtlSeconds = 600;
            cfg.Registry.MaxReservationTtlSeconds = 300;
        }), "will be clamped");
    }

    [Fact]
    public void AnIntentPollFasterThanTheFloor_IsAWarningBecauseItIsClamped()
    {
        AssertWarning(Validate(cfg => cfg.Registry.TransferIntentPollMs = 100),
            "registry.transfer_intent_poll_ms below 250 will be clamped to 250");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ASeamlessReadyWaitTimeoutOfZeroOrLess_IsAnError(int timeout)
    {
        AssertError(Validate(cfg => cfg.Registry.SeamlessReadyWaitTimeoutSeconds = timeout),
            "registry.seamless_ready_wait_timeout_seconds must be greater than zero");
    }

    [Fact]
    public void RemoteModeWithNoUrl_IsAnError()
    {
        AssertError(Validate(cfg =>
        {
            cfg.Registry.Mode = "remote";
            cfg.Registry.Url = "";
            cfg.Registry.SharedSecret = "shared";
        }), "registry.url is required when registry.mode = 'remote'");
    }

    [Theory]
    [InlineData("registry.example")]
    [InlineData("ftp://registry.example")]
    [InlineData("/api/registry")]
    public void RemoteModeWithAUrlThatIsNotHttp_IsAnError(string url)
    {
        AssertError(Validate(cfg =>
        {
            cfg.Registry.Mode = "remote";
            cfg.Registry.Url = url;
            cfg.Registry.SharedSecret = "shared";
        }), "registry.url must be an absolute http or https URL");
    }

    [Fact]
    public void RemoteModeWithNoSharedSecret_IsAnError()
    {
        // The registry authenticates every call with an HMAC over this. Empty means every call is
        // refused.
        AssertError(Validate(cfg =>
        {
            cfg.Registry.Mode = "remote";
            cfg.Registry.Url = "https://registry.example";
            cfg.Registry.SharedSecret = "";
        }), "registry.shared_secret is required when registry.mode = 'remote'");
    }

    [Theory]
    [InlineData("registry.example:8765")]
    [InlineData("not a url")]
    public void AnEmbeddedBindThatIsNotAUrl_IsAnError(string bind)
    {
        AssertError(Validate(cfg =>
        {
            cfg.Registry.Mode = "embedded";
            cfg.Registry.EmbeddedBind = bind;
        }), "registry.embedded_bind must be an absolute http or https URL, or empty");
    }

    [Fact]
    public void AnEmbeddedBindLeftEmpty_IsAccepted()
    {
        // Empty is how an operator turns the embedded registry's HTTP listener off while keeping
        // the in-process registry.
        var result = Validate(cfg =>
        {
            cfg.Registry.Mode = "embedded";
            cfg.Registry.EmbeddedBind = "";
        });

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData("")]
    [InlineData("change-me-and-keep-secret")]
    [InlineData("REPLACE_ME_WITH_A_LONG_RANDOM_STRING")]
    // The two literals the other halves of #40 write: the panel eggs' variable default and the
    // one a backend's nimbus-server.json is created with. Neither reaches a proxy config on its
    // own, and a config an operator assembled by pasting from the wiki, the panel and the mod is
    // exactly the config this rule exists for.
    [InlineData(Nimbus.Shared.SecretPlaceholders.Egg)]
    [InlineData(Nimbus.Shared.SecretPlaceholders.BackendConfig)]
    public void AnEmbeddedRegistryExposedBeyondLoopbackOnADefaultSecret_IsAnError(string secret)
    {
        // The registry mints reservations and holds the ban list. On a published secret, anyone
        // who can reach it can let themselves onto any backend.
        //
        // This is also the rule the loopback default leans on: widening embedded_bind for off-box
        // backends is one line, and an operator who changes that line and no other is stopped here
        // rather than left serving reservations to the internet.
        AssertError(Validate(cfg =>
        {
            cfg.Registry.Mode = "embedded";
            cfg.Registry.EmbeddedBind = "http://0.0.0.0:8765";
            cfg.Registry.EmbeddedSharedSecret = secret;
        }), "registry.embedded_shared_secret must be changed from the default");
    }

    [Fact]
    public void AnEmbeddedRegistryExposedBeyondLoopbackOnARealSecret_IsAccepted()
    {
        var result = Validate(cfg =>
        {
            cfg.Registry.Mode = "embedded";
            cfg.Registry.EmbeddedBind = "http://0.0.0.0:8765";
            cfg.Registry.EmbeddedSharedSecret = "a-long-random-string";
        });

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8765")]
    [InlineData("http://localhost:8765")]
    [InlineData("http://LOCALHOST:8765")]
    [InlineData("http://[::1]:8765")]
    public void AnEmbeddedRegistryOnTheLoopbackWithTheDefaultSecret_IsAccepted(string bind)
    {
        // Nothing off the box can reach it, which is the shipped default and has to stay quiet.
        var result = Validate(cfg =>
        {
            cfg.Registry.Mode = "embedded";
            cfg.Registry.EmbeddedBind = bind;
        });

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ---- whitelist ----

    [Fact]
    public void WhitelistEnforcementWithNoRegistryToReadTheListFrom_IsAnError()
    {
        // The list lives in the registry. Without one the proxy has nothing to check against and
        // would refuse every join.
        AssertError(Validate(cfg =>
        {
            cfg.Registry.Mode = "disabled";
            cfg.Whitelist.Network = true;
        }), "whitelist enforcement needs a registry to read the list from");
    }

    [Fact]
    public void WhitelistOnOneBackendWithNoRegistry_IsTheSameError()
    {
        AssertError(Validate(cfg =>
        {
            cfg.Registry.Mode = "disabled";
            cfg.Whitelist.Servers = new List<string> { "hub" };
        }), "whitelist enforcement needs a registry to read the list from");
    }

    [Fact]
    public void FailingOpenUntilTheFirstSync_IsAWarning()
    {
        AssertWarning(Validate(cfg =>
        {
            cfg.Registry.Mode = "embedded";
            cfg.Registry.EmbeddedBind = "";
            cfg.Whitelist.Network = true;
            cfg.Whitelist.FailOpenUntilFirstSync = true;
        }), "lets everyone in until the registry answers once");
    }

    [Fact]
    public void AWhitelistedBackendThatDoesNotExist_IsAWarning()
    {
        // The consequence is a backend nobody is gating, which is the opposite of what was meant.
        AssertWarning(Validate(cfg =>
        {
            cfg.Registry.Mode = "embedded";
            cfg.Registry.EmbeddedBind = "";
            cfg.Whitelist.Servers = new List<string> { "hub", "staff" };
        }), "whitelist.servers references unknown server 'staff'");
    }

    [Fact]
    public void ABlankEntryInTheWhitelistedBackends_IsAWarning()
    {
        AssertWarning(Validate(cfg =>
        {
            cfg.Registry.Mode = "embedded";
            cfg.Registry.EmbeddedBind = "";
            cfg.Whitelist.Servers = new List<string> { "hub", "  " };
        }), "whitelist.servers contains an empty server id");
    }

    [Fact]
    public void WhitelistedBackendsAreCheckedEvenWhenEnforcementIsOff()
    {
        // whitelist.servers being non-empty is itself what turns enforcement on, so there is no
        // configuration where the names go unchecked. This pins that the name check runs before
        // the enabled check rather than after it.
        var result = Validate(cfg =>
        {
            cfg.Registry.Mode = "embedded";
            cfg.Registry.EmbeddedBind = "";
            cfg.Whitelist.Servers = new List<string> { "typo" };
        });

        Assert.Contains(result.Warnings, w => w.Contains("references unknown server 'typo'"));
    }

    // ---- metrics ----

    [Fact]
    public void WithMetricsOff_TheirOtherSettingsAreNotChecked()
    {
        var result = Validate(cfg =>
        {
            cfg.Metrics.Enabled = false;
            cfg.Metrics.Bind = "nonsense";
            cfg.Metrics.Path = "no-slash";
        });

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("127.0.0.1:42500")]
    [InlineData("nonsense")]
    [InlineData("ftp://127.0.0.1:42500")]
    public void AMetricsBindThatIsNotAnHttpUrl_IsAnError(string bind)
    {
        AssertError(Validate(cfg =>
        {
            cfg.Metrics.Enabled = true;
            cfg.Metrics.Bind = bind;
        }), "metrics.bind must be an absolute http or https URL");
    }

    [Fact]
    public void AMetricsEndpointReachableOffTheBox_IsAWarning()
    {
        // The metrics endpoint has no authentication of its own, so where it is bound is the
        // only thing keeping player counts and backend names private.
        AssertWarning(Validate(cfg =>
        {
            cfg.Metrics.Enabled = true;
            cfg.Metrics.Bind = "http://0.0.0.0:42500";
            cfg.Metrics.StatusApi = false;
        }), "Metrics are unauthenticated");
    }

    [Fact]
    public void TheStatusApiExposedOffTheBoxWithNoToken_IsASecondWarning()
    {
        var result = Validate(cfg =>
        {
            cfg.Metrics.Enabled = true;
            cfg.Metrics.Bind = "http://0.0.0.0:42500";
            cfg.Metrics.StatusApi = true;
            cfg.Metrics.StatusApiToken = "";
        });

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Contains(result.Warnings, w => w.Contains("Metrics are unauthenticated"));
        Assert.Contains(result.Warnings, w => w.Contains("/status is readable by anyone who can reach the bind"));
    }

    [Fact]
    public void TheStatusApiExposedOffTheBoxWithAToken_EarnsOnlyTheMetricsWarning()
    {
        var result = Validate(cfg =>
        {
            cfg.Metrics.Enabled = true;
            cfg.Metrics.Bind = "http://0.0.0.0:42500";
            cfg.Metrics.StatusApi = true;
            cfg.Metrics.StatusApiToken = "panel-token";
        });

        Assert.DoesNotContain(result.Warnings, w => w.Contains("/status is readable"));
    }

    [Fact]
    public void AMetricsEndpointOnTheLoopback_IsQuiet()
    {
        var result = Validate(cfg =>
        {
            cfg.Metrics.Enabled = true;
            cfg.Metrics.Bind = "http://127.0.0.1:42500";
        });

        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("metrics")]
    public void AMetricsPathThatIsNotRooted_IsAnError(string path)
    {
        // Prometheus scrapes an absolute path. A relative one is a 404 the operator finds later.
        AssertError(Validate(cfg =>
        {
            cfg.Metrics.Enabled = true;
            cfg.Metrics.Path = path;
        }), "metrics.path must start with '/'");
    }

    // ---- status ----

    [Fact]
    public void WithTheStatusResponderOff_ItsOtherSettingsAreNotChecked()
    {
        var result = Validate(cfg =>
        {
            cfg.Status.Enabled = false;
            cfg.Status.Name = "";
            cfg.Status.MaxPlayers = -1;
            cfg.Status.QueryTimeoutMs = 0;
        });

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void AStatusResponderWithNoName_IsAnError()
    {
        // The name is what shows in the client's server list entry.
        AssertError(Validate(cfg => cfg.Status.Name = " "), "status.name must be set");
    }

    [Fact]
    public void ANegativeStatusMaxPlayers_IsAnError()
    {
        AssertError(Validate(cfg => cfg.Status.MaxPlayers = -1), "status.max_players cannot be negative");
    }

    [Fact]
    public void AStatusMaxPlayersOfZero_IsAccepted()
    {
        // Zero advertises a full network, which is a legitimate way to hold players off.
        var result = Validate(cfg => cfg.Status.MaxPlayers = 0);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void AQueryTimeoutBelowTheFloor_IsAnError()
    {
        // This doubles as the window the proxy waits for a client's first frame. Too short and
        // every join is treated as a status ping.
        AssertError(Validate(cfg => cfg.Status.QueryTimeoutMs = 50), "status.query_timeout_ms must be at least 100");
    }

    // ---- plugins ----

    [Fact]
    public void WithPluginsOff_TheirOtherSettingsAreNotChecked()
    {
        var result = Validate(cfg =>
        {
            cfg.Plugins.Enabled = false;
            cfg.Plugins.Directory = "";
            cfg.Plugins.Disabled = new List<string> { "not a plugin id" };
        });

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void PluginsOnWithNoDirectory_IsAnError()
    {
        AssertError(Validate(cfg => cfg.Plugins.Directory = "  "),
            "plugins.directory must be set when plugins.enabled = true");
    }

    [Theory]
    [InlineData("not a plugin id")]
    [InlineData("")]
    [InlineData("plugin/id")]
    public void ADisabledPluginIdNoPluginCouldHave_IsAnError(string id)
    {
        // An id that no plugin can be loaded under silently disables nothing, and the operator
        // finds out when the plugin they meant to turn off keeps running.
        AssertError(Validate(cfg => cfg.Plugins.Disabled = new List<string> { id }),
            "plugins.disabled contains invalid plugin id");
    }

    [Theory]
    [InlineData("hub-fallback")]
    [InlineData("hub.fallback")]
    [InlineData("hub_fallback2")]
    public void ADisabledPluginIdThatAPluginCouldHave_IsAccepted(string id)
    {
        var result = Validate(cfg => cfg.Plugins.Disabled = new List<string> { id });

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ---- persistence ----

    [Fact]
    public void PersistingDrainFlagsWithNowhereToPutThem_IsAnError()
    {
        AssertError(Validate(cfg =>
        {
            cfg.Persistence.PersistDrainFlags = true;
            cfg.Persistence.DrainFlagsFile = " ";
        }), "persistence.drain_flags_file must be set");
    }

    [Fact]
    public void WithDrainFlagPersistenceOff_TheFileNameIsNotChecked()
    {
        var result = Validate(cfg =>
        {
            cfg.Persistence.PersistDrainFlags = false;
            cfg.Persistence.DrainFlagsFile = "";
        });

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ---- advanced ----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AConnectTimeoutOfZeroOrLess_IsAnError(int timeout)
    {
        // CancelAfter with a non-positive delay cancels immediately, so every backend connection
        // would be abandoned before it opened.
        AssertError(Validate(cfg => cfg.Advanced.ConnectTimeoutMs = timeout),
            "advanced.connect_timeout_ms must be greater than zero");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1023)]
    public void ABufferSmallerThanAFrameHeader_IsAnError(int size)
    {
        AssertError(Validate(cfg => cfg.Advanced.BufferSize = size),
            "advanced.buffer_size must be at least 1024");
    }

    [Fact]
    public void ABufferOfExactlyTheFloor_IsAccepted()
    {
        var result = Validate(cfg => cfg.Advanced.BufferSize = 1024);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    // ---- the whole picture ----

    [Fact]
    public void SeveralMistakesAtOnce_AreAllReportedRatherThanJustTheFirst()
    {
        // An operator fixing a config one error per restart is the failure mode this avoids.
        var result = Validate(cfg =>
        {
            cfg.Bind = "";
            cfg.Advanced.BufferSize = 16;
            cfg.Advanced.ConnectTimeoutMs = 0;
            cfg.Status.Name = "";
        });

        Assert.False(result.IsValid);
        Assert.Equal(4, result.Errors.Count);
    }

    [Fact]
    public void WarningsAloneDoNotStopTheProxyStarting()
    {
        var result = Validate(cfg =>
        {
            cfg.Try = new List<string> { "typo" };
            cfg.Admin.GrantedPermissions = new List<string>();
            cfg.Transfers.EnableUnsafeSeamlessSplice = true;
        });

        Assert.True(result.IsValid);
        Assert.Equal(3, result.Warnings.Count);
    }
}
