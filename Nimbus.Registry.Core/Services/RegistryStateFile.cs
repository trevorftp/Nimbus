using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nimbus.Shared.Models;

namespace Nimbus.Registry.Services;

// Outside the generic type on purpose: a static field on RegistryStateFile<T> is one instance
// per closed type, so the bans file and the whitelist file would each build their own copy of
// options that are identical and immutable.
internal static class RegistryStateJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
}

// The JSON file behind one of the registry's lists. Same shape as the proxy's
// PersistentDrainStore: one small file, written whole through a temp file and a rename so a
// process that dies mid-write leaves the previous version intact, read once at construction.
//
// A registry restart used to drop every ban and every whitelist entry with no signal that
// anything had been lost (#79), which is the opposite of what vanilla per-server bans do.
public sealed class RegistryStateFile<T>
{
    // Serialising a whole file per write is fine at this size (a busy network holds tens of
    // bans, not millions) and it is what makes every write atomic. The lock keeps two
    // concurrent writers from interleaving on the temp file.
    private readonly object gate = new();
    private readonly ILogger? log;

    public RegistryStateFile(string filePath, ILogger? log = null)
    {
        FilePath = filePath;
        this.log = log;
    }

    public string FilePath { get; }

    // Everything the file holds, expired entries included: judging expiry needs a clock, which
    // belongs to the store rather than to the file.
    public List<T> Load()
    {
        if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath)) return new List<T>();

        string text;
        try
        {
            text = File.ReadAllText(FilePath);
        }
        catch (Exception ex)
        {
            // Unreadable is not the same as corrupt: the file may be perfectly good and owned by
            // somebody else, so it is left exactly where it is.
            Loud($"cannot read registry state file '{FilePath}': {ex.Message}. Starting with an empty list; it will be overwritten on the next change.");
            return new List<T>();
        }

        try
        {
            var state = JsonSerializer.Deserialize<StateEnvelope>(text, RegistryStateJson.Options);
            if (state?.Entries is null) throw new InvalidDataException("no 'entries' array");
            return state.Entries;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            // Neither of the two silent options is acceptable: refusing to start turns one bad
            // file into an outage, and starting empty over the top of it destroys the evidence
            // along with the list. So the file is kept under another name and the operator is
            // told, loudly, where it went.
            Quarantine(ex);
            return new List<T>();
        }
    }

    public void Save(IEnumerable<T> entries, long nowUnix)
    {
        if (string.IsNullOrWhiteSpace(FilePath)) return;

        lock (gate)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var payload = new StateEnvelope { Entries = entries.ToList(), UpdatedAtUnix = nowUnix };
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(payload, RegistryStateJson.Options));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                // The in-memory list is already updated, so the ban is in force either way. What
                // is at risk is only the next restart, which is exactly what the operator needs
                // to hear about.
                Loud($"cannot write registry state file '{FilePath}': {ex.Message}. The list is in force but will not survive a restart.");
            }
        }
    }

    private void Quarantine(Exception ex)
    {
        var quarantined = FilePath + ".bad";
        try
        {
            File.Move(FilePath, quarantined, overwrite: true);
            Loud($"registry state file '{FilePath}' is not valid JSON ({ex.Message}). Renamed to '{quarantined}' and starting from an empty list. Nothing in it is in force.");
        }
        catch (Exception moveEx)
        {
            Loud($"registry state file '{FilePath}' is not valid JSON ({ex.Message}) and could not be renamed aside ({moveEx.Message}). Starting from an empty list; the file will be overwritten on the next change.");
        }
    }

    // Errors, always. The embedded registry can run without a container, and therefore without
    // an ILoggerFactory to build a logger from, so there is a console fallback in the same
    // shape RegistryConsoleLoggerProvider prints.
    private void Loud(string message)
    {
        if (log is not null) log.LogError("{Message}", message);
        else Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} [nimbus] error: {message}");
    }

    private sealed class StateEnvelope
    {
        // Nullable so a file with no entries array at all is told apart from one holding an
        // empty list. The first is a file the registry did not write and should not trust, the
        // second is a list somebody emptied.
        public List<T>? Entries { get; set; }

        // Not read back. It is there so an operator looking at the file can tell how old it is.
        public long UpdatedAtUnix { get; set; }
    }
}

// The registry's state files, named once so the standalone exe and the proxy's embedded mode
// cannot disagree about where they live.
public static class RegistryStateFiles
{
    public const string BansFileName = "nimbus.bans.json";
    public const string WhitelistFileName = "nimbus.whitelist.json";
    public const string WhitelistPendingFileName = "nimbus.whitelist.pending.json";
    public const string TokensFileName = "nimbus.tokens.json";

    public static RegistryStateFile<NetworkBan> Bans(string? stateDir, ILogger? log = null)
        => new(Path.Combine(Resolve(stateDir), BansFileName), log);

    public static RegistryStateFile<WhitelistEntry> Whitelist(string? stateDir, ILogger? log = null)
        => new(Path.Combine(Resolve(stateDir), WhitelistFileName), log);

    // Pending entries get their own file for the same reason bans and tokens do: a name-keyed row
    // that has not bound yet is a different kind of state from a settled uid entry, and a corrupt
    // pending file must not be able to take the live whitelist down with it.
    public static RegistryStateFile<WhitelistEntry> WhitelistPending(string? stateDir, ILogger? log = null)
        => new(Path.Combine(Resolve(stateDir), WhitelistPendingFileName), log);

    // Its own file for the same reason the other two have theirs: a corrupt token list must not
    // be able to take the ban list down with it. What it holds is hashes, never secrets.
    public static RegistryStateFile<ApiToken> Tokens(string? stateDir, ILogger? log = null)
        => new(Path.Combine(Resolve(stateDir), TokensFileName), log);

    // An unset directory means the working directory, which is where nimbus.registry.toml is
    // read from as well.
    private static string Resolve(string? stateDir)
        => string.IsNullOrWhiteSpace(stateDir) ? "." : stateDir;
}
