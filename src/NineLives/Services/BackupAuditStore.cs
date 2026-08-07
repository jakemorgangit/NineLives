using System.IO;
using System.Text.Json;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>What an audit established about one backup file, kept so it need not be established again.</summary>
/// <param name="Key">Blob name and ETag - see <see cref="BackupAuditStore.KeyFor"/>.</param>
/// <param name="Passed">Whether the header agreed with what the path claimed.</param>
/// <param name="DatabaseName">What the header said the database was.</param>
/// <param name="BackupTypeCode">What the header said the type was.</param>
/// <param name="AuditedAt">When, so a person can see how stale the answer is.</param>
public sealed record AuditRecord(
    string Key,
    bool Passed,
    string? DatabaseName,
    int? BackupTypeCode,
    DateTime AuditedAt);

public interface IBackupAuditStore
{
    /// <summary>What is already known. Never throws - an unreadable cache is a slow audit, not an error.</summary>
    Dictionary<string, AuditRecord> Load();

    void Save(IEnumerable<AuditRecord> records);

    string FilePath { get; }
}

/// <summary>
/// What previous audits established, kept between runs (#130).
///
/// The point is cost. A header read is about 1.7 seconds - measured, not guessed - so auditing a
/// database of a hundred backup sets is a few minutes. Nobody does that twice. Cached, the second
/// run is instant, which turns the audit from a thing you do once into a thing you can use.
///
/// The key is the blob name and its ETag. A backup header never changes, so the only reason to
/// re-read one is that the blob itself is a different blob - and the ETag is precisely the thing
/// Azure changes when that happens. A file replaced under the same name gets re-audited; one that
/// has merely been listed again does not.
///
/// Same two rules as the restore history, both learned from the config-loss defect (#7): a cache
/// that cannot be read comes back empty rather than being overwritten blindly, and writes go
/// through a temp file and an atomic swap so a crash mid-write cannot truncate it.
/// </summary>
public sealed class BackupAuditStore : IBackupAuditStore
{
    /// <summary>
    /// Older records fall off the end. Each is small, but a cache that grows for every blob ever
    /// listed across every container is one nobody ever prunes.
    /// </summary>
    private const int MaxRecords = 20_000;

    private readonly string _path;
    private readonly object _gate = new();

    public BackupAuditStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NineLives"))
    { }

    /// <summary>Lets the tests write somewhere disposable instead of the real profile.</summary>
    internal BackupAuditStore(string directory)
        => _path = Path.Combine(directory, "audit-cache.json");

    public string FilePath => _path;

    /// <summary>
    /// The identity of a blob's CONTENT, not its name.
    ///
    /// ETag when Azure gave one, which is the whole point - it changes when the blob does and not
    /// otherwise. Size and last-modified as a fallback, which is weaker but still catches a file
    /// replaced with a different one; without either, the name alone, which would treat a
    /// replacement as the same file and is therefore only used when nothing better exists.
    /// </summary>
    public static string KeyFor(BackupFileInfo file)
    {
        if (!string.IsNullOrWhiteSpace(file.ETag))
            return $"{file.BlobName}|{file.ETag}";

        return $"{file.BlobName}|{file.SizeBytes}|{file.LastModified.UtcTicks}";
    }

    public Dictionary<string, AuditRecord> Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return [];

                var records = JsonSerializer.Deserialize<List<AuditRecord>>(File.ReadAllText(_path));
                if (records == null) return [];

                // Last one wins on a duplicate key rather than throwing: a cache is not worth an
                // exception, and a duplicate only means an older record for the same content.
                var map = new Dictionary<string, AuditRecord>(StringComparer.Ordinal);
                foreach (var record in records) map[record.Key] = record;
                return map;
            }
            catch
            {
                // Unreadable, half-written, or from a future version. An audit that runs again is a
                // few minutes; a cache file overwritten on a bad read is the defect #7 was.
                return [];
            }
        }
    }

    public void Save(IEnumerable<AuditRecord> records)
    {
        lock (_gate)
        {
            try
            {
                var kept = records
                    .OrderByDescending(r => r.AuditedAt)
                    .Take(MaxRecords)
                    .ToList();

                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(kept, JsonOptions));
                File.Move(temp, _path, overwrite: true);
            }
            catch
            {
                // Caching must never be able to fail the thing it was speeding up.
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
