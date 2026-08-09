using System.Text.Json;

namespace Nimbus.Proxy;

// Per-request bundle passed to admin command handlers.
internal sealed class AdminContext
{
    public ProxyListener Proxy { get; }
    public ProxyConfig Cfg { get; }
    public AdminRequest Request { get; }
    public CancellationToken StopToken { get; }
    public AdminPermissions Permissions { get; }
    public IReadOnlyCollection<IAdminCommand> Commands { get; }
    public IReadOnlyList<LoadedPlugin> Plugins { get; }
    public Func<string>? Reload { get; }

    // AdminContext is itself the parameter object: it exists to hand every command handler the eight
    // distinct things it might read, each surfaced as its own property above. The constructor takes
    // those eight once (S107 suppressed) and assigns them straight across. Wrapping a subset in a
    // nested record would not remove any parameter, only push it down a level, so a handler reaching
    // for ctx.Proxy would go through ctx.Something.Proxy for no gain. There is nothing to bundle here
    // that this class is not already the bundle for.
    public AdminContext(ProxyListener proxy, ProxyConfig cfg, JsonElement request, // NOSONAR
        CancellationToken stopToken, AdminPermissions permissions, IReadOnlyCollection<IAdminCommand> commands,
        IReadOnlyList<LoadedPlugin> plugins, Func<string>? reload = null)
    {
        Proxy = proxy;
        Cfg = cfg;
        Request = new AdminRequest(request);
        StopToken = stopToken;
        Permissions = permissions;
        Commands = commands;
        Plugins = plugins;
        Reload = reload;
    }
}

internal readonly struct AdminRequest
{
    private readonly JsonElement root;

    public AdminRequest(JsonElement root)
    {
        this.root = root;
    }

    // Whether the caller mentioned a field at all, whatever they put in it. Tells an optional
    // field left out from one sent with a value the reader cannot use, which is the difference
    // between falling back to a default and refusing a typo.
    public bool Has(string name) => root.TryGetProperty(name, out _);

    public bool TryString(string name, out string value)
    {
        value = "";
        if (!root.TryGetProperty(name, out var el)) return false;
        if (el.ValueKind != JsonValueKind.String) return false;
        value = el.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    public string? OptionalString(string name)
        => TryString(name, out var value) ? value : null;

    public bool TryInt64(string name, out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out var el) &&
               el.ValueKind == JsonValueKind.Number &&
               el.TryGetInt64(out value);
    }

    public bool TryInt32(string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var el) &&
               el.ValueKind == JsonValueKind.Number &&
               el.TryGetInt32(out value);
    }

    public bool Bool(string name, bool fallback = false)
    {
        if (!root.TryGetProperty(name, out var el)) return fallback;
        return el.ValueKind == JsonValueKind.True || (el.ValueKind != JsonValueKind.False && fallback);
    }
}

internal interface IAdminCommand
{
    string Name { get; }
    IReadOnlyList<string> Aliases => Array.Empty<string>();
    string Permission { get; }
    string Summary { get; }
    string Usage { get; }
    Task<object> ExecuteAsync(AdminContext ctx);
}

internal static class AdminCommandError
{
    public static object Missing(IAdminCommand command, string name)
        => new { ok = false, reason = $"missing '{name}'", usage = command.Usage };

    public static object Invalid(IAdminCommand command, string name)
        => new { ok = false, reason = $"invalid '{name}'", usage = command.Usage };

    public static object Usage(IAdminCommand command, string reason)
        => new { ok = false, reason, usage = command.Usage };
}

internal sealed class AdminPermissions
{
    private readonly HashSet<string> granted;

    public AdminPermissions(IEnumerable<string> granted)
    {
        this.granted = new HashSet<string>(granted.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
    }

    public bool Allows(string permission)
    {
        if (granted.Contains("*")) return true;
        if (string.IsNullOrWhiteSpace(permission)) return true;
        if (granted.Contains(permission)) return true;

        int dot = permission.Length;
        while ((dot = permission.LastIndexOf('.', dot - 1)) > 0)
        {
            if (granted.Contains(string.Concat(permission.AsSpan(0, dot), ".*")))
                return true;
        }
        return false;
    }
}
