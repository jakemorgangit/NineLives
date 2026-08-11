using System.IO;
using System.Text.Json;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

public interface IRestoreHistoryStore
{
    /// <summary>Most recent first. Never throws - an unreadable history is not worth an error dialog.</summary>
    List<RestoreHistoryEntry> Load();

    /// <summary>
    /// True when the file is there and could not be read (#370).
    ///
    /// Load answers an empty list either way, and the two mean opposite things: one is a fresh
    /// install, the other is a history that exists and is unreadable. Reporting the second as
    /// "no restores recorded yet" hides it - and then Clear, offered right next to that
    /// sentence, would overwrite a file that may still hold every receipt.
    /// </summary>
    bool CouldNotRead { get; }

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
    /// orphan it. Writers queue with an exponential backoff, and a writer that never gets the
    /// lock does not write.
    ///
    /// That last rule reverses the original one, which let the write proceed WITHOUT the lock
    /// on the reasoning that losing this entry for certain was worse than a small chance of
    /// the race. The premise was wrong in both halves, and CI proved it: this test lost FOUR
    /// of fifty entries on a loaded runner. An unlocked read-modify-write does not risk losing
    /// THIS entry - it silently drops whatever the other writer put in between the read and
    /// the write, so the trade was one certain loss against several possible ones, and the
    /// entries at risk are the receipts proving a restore happened.
    ///
    /// The backoff below is the other half of the fix, and the reason the timeout is now
    /// rarely reached: it starts at 2ms rather than 50 (the first attempt after a release
    /// should be quick - the old fixed sleep made every writer wait out most of a slot it
    /// could have had immediately), and it jitters, because writers that collide once will
    /// collide forever if they all back off by exactly the same amount.
    /// </summary>
    private const int LockTimeoutMs = 10_000;

    /// <summary>
    /// Per instance rather than a static seam, so a test that shortens it cannot affect another
    /// test running beside it.
    /// </summary>
    private readonly int _lockTimeoutMs;

    private FileStream? AcquireCrossProcessLock()
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var lockPath = _path + ".lock";

        var deadline = Environment.TickCount64 + _lockTimeoutMs;
        var wait = 2;

        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            if (Environment.TickCount64 >= deadline) return null;

            Thread.Sleep(wait + Random.Shared.Next(wait));
            wait = Math.Min(wait * 2, 50);
        }
    }

    public RestoreHistoryStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NineLives"))
    { }

    /// <summary>
    /// Lets the tests write somewhere disposable instead of the real profile, and shorten the
    /// lock wait so the refusal path can be exercised without a ten-second test.
    /// </summary>
    internal RestoreHistoryStore(string directory, int lockTimeoutMs = LockTimeoutMs)
    {
        _path = Path.Combine(directory, "restore-history.json");
        _lockTimeoutMs = lockTimeoutMs;
    }

    public string FilePath => _path;

    /// <inheritdoc />
    public bool CouldNotRead { get; private set; }

    public List<RestoreHistoryEntry> Load()
    {
        lock (_gate)
        {
            var entries = ReadUnlocked();
            CouldNotRead = entries == null;
            return entries ?? [];
        }
    }

    public void Append(RestoreHistoryEntry entry)
    {
        try
        {
            lock (_gate)
            {
                using var crossProcess = AcquireCrossProcessLock();

                // A lock that could not be taken is not a lock. Writing anyway makes this a
                // read-modify-write with no exclusion, which drops whatever the other writer
                // added in between - so the entries lost are somebody else's, not this one's.
                // `using` a null is a silent no-op, which is exactly how this went unnoticed.
                if (crossProcess == null) return;

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

                // Same rule as Append. Clearing without the lock would take the other writer's
                // in-flight entry with it, and a Clear that did not happen is visible - the
                // list is still there - where a silently destroyed receipt is not.
                if (crossProcess == null) return;

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
