using Microsoft.Extensions.Options;

namespace Kupro.BackupAgent;

/// <summary>
/// Which data volume is live and which is the restore target (ADR-016). Both are mounted in
/// Postgres and here at /pgdata/a and /pgdata/b. Postgres picks one at start from the file
/// /pg-control/active, so going live is "write the other letter, restart": no env vars to edit.
/// While Postgres runs, its data_directory is the truth; the file only says what the next start
/// does. When Postgres is down, the last volume the agent saw live counts.
/// </summary>
public sealed class DataVolumes(IOptions<AgentOptions> options, WalState wal, StateStore state)
{
    private static readonly string[] Slots = ["a", "b"];
    private readonly AgentOptions _options = options.Value;

    private string ActiveFile => Path.Combine(_options.ControlDirectory, "active");

    public string Directory(string slot) => Path.Combine(_options.DataRoot, slot);

    public string Name(string slot) => _options.VolumeNamePrefix + slot;

    public static string Other(string slot) => slot == "a" ? "b" : "a";

    /// <summary>The slot whose directory Postgres reports, or null when it's down or runs somewhere else.</summary>
    public string? SlotOf(string? dataDirectory) =>
        dataDirectory is null ? null : Slots.FirstOrDefault(s => string.Equals(Directory(s), dataDirectory.TrimEnd('/'), StringComparison.Ordinal));

    /// <summary>Postgres' data_directory when it's neither volume. Then nothing may be wiped.</summary>
    public string? UnknownLiveDirectory =>
        wal.Current.Server?.DataDirectory is { } directory && SlotOf(directory) is null ? directory : null;

    public string Live =>
        SlotOf(wal.Current.Server?.DataDirectory)
        ?? SlotFromName(state.Read(s => s.LiveVolumeName))
        ?? Next
        ?? "a";

    public string Spare => Other(Live);

    /// <summary>The slot Postgres starts on next, from /pg-control/active. Null before the first write.</summary>
    public string? Next
    {
        get
        {
            try
            {
                var slot = File.ReadAllText(ActiveFile).Trim();
                return Slots.Contains(slot) ? slot : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>The spare goes live on the next Postgres start (the console armed it, nobody has restarted the stack yet).</summary>
    public bool GoLivePending => Next is { } next && next != Live;

    public string LiveName => Name(Live);
    public string SpareName => Name(Spare);
    public string LiveDirectory => Directory(Live);
    public string SpareDirectory => Directory(Spare);

    /// <summary>Sets the slot for the next Postgres start. Atomic, so Postgres never reads half a file.</summary>
    public void SetNext(string slot)
    {
        if (!Slots.Contains(slot))
        {
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "Slot must be a or b.");
        }

        System.IO.Directory.CreateDirectory(_options.ControlDirectory);
        var temp = ActiveFile + ".tmp";
        File.WriteAllText(temp, slot + "\n");
        File.Move(temp, ActiveFile, overwrite: true);
    }

    private string? SlotFromName(string? name) =>
        name is not null && name.StartsWith(_options.VolumeNamePrefix, StringComparison.Ordinal) && Slots.Contains(name[_options.VolumeNamePrefix.Length..])
            ? name[_options.VolumeNamePrefix.Length..]
            : null;
}
