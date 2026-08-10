using System.IO;
using System.Text.Json;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

public interface IRestoreHistoryStore
{
    /// <summary>Most recent first. Never throws - an unreadable history is not worth an error dialog.</summary>
    List<RestoreHistoryEntry> Load();

    /// <summary>Adds one execution. Never throws: recording history must not be able to fail a restore.</summary>
    void Append(RestoreHistoryEntry entry);

    void Clear();

    string FilePath { get; }
}

/// <summary>
/// Past executions, kept next to the config so they survive the app closing (#31).
///
/// Separate from <see cref="OperationLog"/> on purpose. The log is an append-only text stream for
/// reading and attaching to a bug report; this is structured, bounded and meant to be listed,
/// searched and re-used.
///
/// Two rules, both learned from the config-loss defect (#7):
///   - a history that cannot be read returns empty and is NOT then overwritten blindly
///   - writes go through a temp file and an atomic swap, so a crash mid-write cannot truncate it
/// </summary>
public sealed class RestoreHistoryStore : IRestoreHistoryStore
{
    /// <summary>
    /// Older entries fall off the end. Each carries a script and a console log, so this is a cap
    /// on file size as much as on count - and a restore history nobody has looked at in 200
    /// restores is not what anyone is reaching for.
    /// </summary>
    private const int MaxEntries = 200;

    private readonly string _path;
    private readonly object _gate = new();

    /// <summary>
    /// Serialises writers ACROSS processes (#298). The in-process gate was correct for one
    /// process, but the CLI made it two: a scheduled 9lives rehearse writing its receipt
    /// while the app is open is a read-modify-write race where the last writer silently
    /// drops the other's entry - and the receipt that vanishes is the proof the rehearsal
    /// existed, the exact thing the exposure dashboard's Proven column reads.
    ///
    /// A sidecar .lock file held with FileShare.None; DeleteOnClose so a crash cannot
    /// orphan it. Writers queue in a short retry loop. After the timeout the write proceeds
    /// WITHOUT the lock: the overlap window is milliseconds, and losing this entry for
    /// certain is worse than the small chance of the race the lock exists to close.
    /// </summary>
    private FileStream? AcquireCrossProcessLock()
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var lockPath = _path + ".lock";

        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                return new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }

        return null;
    }

    public RestoreHistoryStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NineLives"))
    { }

    /// <summary>Lets the tests write somewhere disposable instead of the real profile.</summary>
    internal RestoreHistoryStore(string directory)
        => _path = Path.Combine(directory, "restore-history.json");

    public string FilePath => _path;

    public List<RestoreHistoryEntry> Load()
    {
        lock (_gate)
        {
            return ReadUnlocked() ?? [];
        }
    }

    public void Append(RestoreHistoryEntry entry)
    {
        try
        {
            lock (_gate)
            {
                using var crossProcess = AcquireCrossProcessLock();

                var existing = ReadUnlocked();

                // Null means the file is there and could not be read. Appending to an empty list
                // and saving would throw away every entry it holds - the exact shape of the
                // config-loss bug - so the honest move is to leave the file alone.
                if (existing == null) return;

                // Everything is redacted on the way IN, at the one boundary, so a future caller
                // cannot leak a token by forgetting to.
                entry.Script = LogRedactor.Redact(entry.Script);
                entry.Log = LogRedactor.Redact(entry.Log);
                if (entry.ErrorMessage != null)
                    entry.ErrorMessage = LogRedactor.Redact(entry.ErrorMessage);

                existing.Insert(0, entry);
                if (existing.Count > MaxEntries)
                    existing.RemoveRange(MaxEntries, existing.Count - MaxEntries);

                WriteUnlocked(existing);
            }
        }
        catch
        {
            // Recording what happened must never be able to fail the thing that happened.
        }
    }

    public void Clear()
    {
        try
        {
            lock (_gate)
            {
                using var crossProcess = AcquireCrossProcessLock();
                WriteUnlocked([]);
            }
        }
        catch
        {
        }
    }

    /// <summary>Null when the file exists but could not be read; empty list when there is no file.</summary>
    private List<RestoreHistoryEntry>? ReadUnlocked()
    {
        if (!File.Exists(_path)) return [];

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<RestoreHistoryEntry>>(json);
        }
        catch
        {
            return null;
        }
    }

    private void WriteUnlocked(List<RestoreHistoryEntry> entries)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });

        var temp = _path + ".tmp";
        File.WriteAllText(temp, json);

        if (File.Exists(_path))
        {
            try
            {
                File.Replace(temp, _path, null, ignoreMetadataErrors: true);
                return;
            }
            catch (PlatformNotSupportedException)
            {
                // A few network shares cannot do the atomic swap.
            }
            File.Delete(_path);
        }

        File.Move(temp, _path);
    }
}
