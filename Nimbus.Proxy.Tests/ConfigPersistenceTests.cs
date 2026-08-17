using Nimbus.Shared;
using Xunit;

namespace Nimbus.Proxy.Tests;

/// <summary>
/// The TOML config Nimbus creates on first run and rereads on every reload, read back with the
/// same loader that wrote it: a field that round-trips wrong is one that silently reverts under
/// an operator on the next reload, and a key name that drifts is a config file that loads as
/// defaults without complaining.
/// </summary>
public class ConfigPersistenceTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "nimbus-cfg-" + Guid.NewGuid().ToString("N"));

    public ConfigPersistenceTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* a leftover temp dir is harmless */ }
    }

    private string Path_(string name) => System.IO.Path.Combine(dir, name);

    [Fact]
    public void WithNoConfigFile_TheDefaultsAreWrittenOutAndHandedBack()
    {
        string path = Path_("nimbus.proxy.toml");

        var cfg = TomlConfig.LoadOrCreate<ProxyConfig>(path);

        Assert.True(File.Exists(path), "first run left no config file for the operator to edit");
        Assert.Equal("0.0.0.0:42420", cfg.Bind);
    }

    [Fact]
    public void AConfigWrittenByNimbus_ReadsBackAsWhatWasWritten()
    {
        string path = Path_("nimbus.proxy.toml");
        var original = new ProxyConfig
        {
            Bind = "203.0.113.4:42420",
            Servers = new Dictionary<string, string>
            {
                ["hub"] = "10.0.0.1:42421",
                ["creative"] = "10.0.0.2:42421",
            },
            Try = new List<string> { "hub", "creative" },
            ProxyProtocolServers = new List<string> { "hub" },
        };
        original.Transfers.DefaultMode = "seamless";
        original.Transfers.AllowSeamless = true;
        original.Transfers.RedirectAddress = "play.example.net:42420";
        original.Registry.SeamlessReadyWaitTimeoutSeconds = 41;
        original.Admin.Secret = "operator-secret";
        original.Admin.GrantedPermissions = new List<string> { "nimbus.command.ban", "nimbus.command.kick" };
        original.Whitelist.Network = true;
        original.Whitelist.Servers = new List<string> { "staff" };
        original.Plugins.Disabled = new List<string> { "hub-fallback" };
        original.Advanced.BufferSize = 32768;

        TomlConfig.Save(path, original);
        var loaded = TomlConfig.LoadFile<ProxyConfig>(path);

        // Every one of these is a decision an operator made. A field that does not survive the
        // round trip is one that silently reverts on the next reload.
        Assert.Equal("203.0.113.4:42420", loaded.Bind);
        Assert.Equal(2, loaded.Servers.Count);
        Assert.Equal("10.0.0.2:42421", loaded.Servers["creative"]);
        Assert.Equal(new[] { "hub", "creative" }, loaded.Try);
        Assert.Equal(new[] { "hub" }, loaded.ProxyProtocolServers);
        Assert.Equal("seamless", loaded.Transfers.DefaultMode);
        Assert.True(loaded.Transfers.AllowSeamless);
        Assert.Equal("play.example.net:42420", loaded.Transfers.RedirectAddress);
        Assert.Equal(41, loaded.Registry.SeamlessReadyWaitTimeoutSeconds);
        Assert.Equal("operator-secret", loaded.Admin.Secret);
        Assert.Equal(2, loaded.Admin.GrantedPermissions.Count);
        Assert.True(loaded.Whitelist.Network);
        Assert.Equal(new[] { "staff" }, loaded.Whitelist.Servers);
        Assert.Equal(new[] { "hub-fallback" }, loaded.Plugins.Disabled);
        Assert.Equal(32768, loaded.Advanced.BufferSize);
    }

    [Fact]
    public void TheKeysOnDisk_AreTheSnakeCaseOnesOperatorsAreToldToType()
    {
        string path = Path_("nimbus.proxy.toml");

        TomlConfig.Save(path, new ProxyConfig());
        string text = File.ReadAllText(path);

        // What an operator types has to be what the loader reads. A rename here is a config file
        // that loads as defaults with no complaint.
        Assert.Contains("default_mode", text);
        Assert.Contains("allow_seamless", text);
        Assert.Contains("granted_permissions", text);
        Assert.Contains("fail_open_until_first_sync", text);
        Assert.Contains("connect_timeout_ms", text);
        Assert.Contains("[servers]", text);
    }

    [Fact]
    public void AnExistingConfig_IsReadRatherThanOverwritten()
    {
        string path = Path_("nimbus.proxy.toml");
        File.WriteAllText(path, "bind = \"127.0.0.1:12345\"\n");

        var cfg = TomlConfig.LoadOrCreate<ProxyConfig>(path);

        Assert.Equal("127.0.0.1:12345", cfg.Bind);
        // The file the operator wrote is still theirs, comments and all.
        Assert.Equal("bind = \"127.0.0.1:12345\"\n", File.ReadAllText(path));
    }

    [Fact]
    public void AFieldTheConfigDoesNotMention_KeepsItsDefault()
    {
        string path = Path_("nimbus.proxy.toml");
        File.WriteAllText(path, "bind = \"127.0.0.1:12345\"\n");

        var cfg = TomlConfig.LoadOrCreate<ProxyConfig>(path);

        // Upgrades add settings. An operator's old file must not come back with those zeroed.
        Assert.Equal(5000, cfg.Advanced.ConnectTimeoutMs);
        Assert.Equal("redirect", cfg.Transfers.DefaultMode);
    }

    [Fact]
    public void ALegacyJsonConfig_IsMigratedToTomlAndPutAside()
    {
        string toml = Path_("nimbus.proxy.toml");
        string json = Path_("nimbus.proxy.json");
        File.WriteAllText(json, """{"bind":"127.0.0.1:12345","try":["hub"]}""");

        var cfg = TomlConfig.LoadOrCreate<ProxyConfig>(toml);

        // The operator's settings come across rather than being replaced by defaults.
        Assert.Equal("127.0.0.1:12345", cfg.Bind);
        Assert.Equal(new[] { "hub" }, cfg.Try);
        Assert.True(File.Exists(toml), "the migration wrote no TOML");
        // And the original is renamed rather than deleted or left to be read again next boot.
        Assert.False(File.Exists(json));
        Assert.True(File.Exists(json + ".migrated"));
    }

    [Fact]
    public void ALegacyJsonConfigWithFieldsInTheWrongCase_IsStillMigrated()
    {
        string toml = Path_("nimbus.proxy.toml");
        File.WriteAllText(Path_("nimbus.proxy.json"), """{"BIND":"127.0.0.1:12345"}""");

        var cfg = TomlConfig.LoadOrCreate<ProxyConfig>(toml);

        Assert.Equal("127.0.0.1:12345", cfg.Bind);
    }

    [Fact]
    public void APathThatIsNotAToml_IsRefusedRatherThanWrittenTo()
    {
        // The migration renames a sibling .json, so a caller that passed the .json by mistake
        // would have it moved out from under itself.
        Assert.Throws<ArgumentException>(() => TomlConfig.LoadOrCreate<ProxyConfig>(Path_("nimbus.proxy.json")));
    }

    [Theory]
    [InlineData("DefaultMode", "default_mode")]
    [InlineData("ServerId", "server_id")]
    [InlineData("FailOpenUntilFirstSync", "fail_open_until_first_sync")]
    [InlineData("ConnectTimeoutMs", "connect_timeout_ms")]
    [InlineData("HTTPServer", "http_server")]
    [InlineData("Bind", "bind")]
    [InlineData("", "")]
    public void KeyNamesAreDerivedFromThePropertyNamesTheSameWayTheyAlwaysHaveBeen(string property, string key)
    {
        // Pinned rather than left to the naming policy: these are the strings in every operator's
        // config file on disk, so the acronym handling in particular cannot drift.
        Assert.Equal(key, TomlConfig.ToSnakeCase(property));
    }
}
