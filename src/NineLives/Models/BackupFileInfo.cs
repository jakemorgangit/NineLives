using System.Text.RegularExpressions;

namespace Blackcat.NineLives.Models;

public enum BackupType
{
    Full,
    Differential,
    TransactionLog,
    Unknown
}

/// <summary>
/// Where a backup's time reading came from - which also says what clock it is on.
///
/// These are NOT the same time base, and nothing in a container tells us the offset between
/// them, so they can only be compared once you accept the skew. A container whose files are
/// homogeneous is fine either way; the problem case is one database with both kinds, where a
/// fallback-derived set can sort hours out of position.
/// </summary>
public enum BackupTimestampSource
{
    /// <summary>Parsed out of the filename, so it reads the BACKUP SERVER's local clock.</summary>
    FileName,

    /// <summary>Read from SQL Server's own header, so it reads the backup server's local clock.</summary>
    BackupHeader,

    /// <summary>
    /// Fallback: the blob's LastModified, which is UTC, and additionally records when the file
    /// finished uploading rather than when the backup was taken.
    /// </summary>
    BlobLastModified,

    /// <summary>
    /// The blob's LastModified, converted into the backup server's own time zone because the
    /// container says which one that is (#102).
    ///
    /// This is on the SAME clock as a filename or header reading, so it sorts correctly against
    /// them - the skew is gone. What remains is that LastModified records when the upload
    /// FINISHED rather than when the backup was taken, which no time zone can fix, so it is still
    /// shown as approximate.
    /// </summary>
    BlobLastModifiedConverted
}

/// <summary>
/// Helpers for the app's backup time readings.
/// </summary>
public static class BackupTime
{
    /// <summary>
    /// Reduces a blob's LastModified to a bare wall-clock reading.
    ///
    /// Kind is deliberately Unspecified. Every time value the app sorts on is a wall clock whose
    /// zone is recorded separately in a <see cref="BackupTimestampSource"/>; leaving Kind unset
    /// keeps anything from quietly converting one base into another - notably
    /// <c>new DateTimeOffset(value)</c>, which silently assumes the WORKSTATION's offset, and
    /// <c>ToLocalTime()</c>, which is the workstation's zone rather than the backup server's.
    /// </summary>
    public static DateTime WallClock(DateTimeOffset value)
        => DateTime.SpecifyKind(value.UtcDateTime, DateTimeKind.Unspecified);

    /// <summary>
    /// Converts a blob's LastModified into the backup server's own local time.
    ///
    /// This is the only thing that genuinely reconciles the two clocks: a filename says what the
    /// backup server's clock read, and the blob says UTC. Given the server's zone the second can
    /// be expressed as the first, and the two become comparable rather than merely labelled.
    ///
    /// A zone the machine does not know - a config edited by hand, an IANA id on an older Windows
    /// build - returns null rather than throwing, and the caller falls back to the unconverted
    /// reading. A wrong time is worse than an honest one, but refusing to open the container over
    /// it would be worse still.
    /// </summary>
    public static DateTime? ToBackupServerTime(DateTimeOffset value, string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return null;

        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);

            // From the instant, not from the wall clock: this is what makes DST correct. A backup
            // taken in July and one taken in December convert with different offsets, which a
            // fixed number could never do.
            return DateTime.SpecifyKind(
                TimeZoneInfo.ConvertTime(value, zone).DateTime, DateTimeKind.Unspecified);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>Explains an approximate reading. Shown as a tooltip wherever one is displayed.</summary>
    public const string ApproximateNote =
        "Approximate. This file's name carries no timestamp, so the time shown is when the blob "
        + "was last modified (UTC), not when the backup was taken on the server. It may sit hours "
        + "away from the other entries.";

    /// <summary>
    /// Explains a converted reading: on the right clock now, but still the upload time.
    /// </summary>
    public const string ConvertedNote =
        "Approximate. This file's name carries no timestamp, so the time shown is when the blob "
        + "was last modified - converted into the backup server's time zone, so it sorts correctly "
        + "against the others. It is still when the upload finished rather than when the backup "
        + "was taken.";

    /// <summary>Prefix marking a displayed time as approximate.</summary>
    public const string ApproximateMarker = "~";

    public static string Format(DateTime value, bool approximate)
        => approximate
            ? ApproximateMarker + value.ToString("yyyy-MM-dd HH:mm:ss")
            : value.ToString("yyyy-MM-dd HH:mm:ss");
}

/// <summary>Whether a backup has been checked against its own header (#130).</summary>
public enum BackupAuditState
{
    /// <summary>Nobody has read this backup's header, so what it is remains an inference.</summary>
    Unaudited,

    /// <summary>The header agreed with what the path and filename claimed.</summary>
    Passed,

    /// <summary>The header disagreed, or could not be read at all.</summary>
    Failed
}

public class BackupFileInfo
{
    public string BlobName { get; set; } = string.Empty;
    public string BlobUrl { get; set; } = string.Empty;

    /// <summary>
    /// Azure's identity for this blob's CONTENT, when it gave one.
    ///
    /// The key an audit result is cached against. A backup header never changes, so the only reason
    /// to read one twice is that the blob is a different blob - and the ETag is exactly what Azure
    /// changes when that happens (#130).
    /// </summary>
    public string? ETag { get; set; }

    /// <summary>
    /// Whether this backup has been checked against its own header.
    ///
    /// Worth showing on the file rather than only in a findings list: a chain built from inference
    /// and a chain confirmed by the backups themselves look identical otherwise, and the difference
    /// is exactly what somebody wants to know before restoring from it.
    /// </summary>
    public BackupAuditState AuditState { get; set; } = BackupAuditState.Unaudited;

    public bool AuditPassed => AuditState == BackupAuditState.Passed;
    public bool AuditFailed => AuditState == BackupAuditState.Failed;

    /// <summary>
    /// Where this backup lives on disk, when it was never in blob storage at all (#149).
    ///
    /// Set for backups discovered through a source instance's msdb rather than by listing a
    /// container. It is what decides how the restore addresses the file - a file with a local path
    /// is restored FROM DISK, everything else FROM URL - so the data says where it lives rather
    /// than a flag somewhere else having to agree with it.
    /// </summary>
    public string? LocalPath { get; set; }

    /// <summary>The device clause a RESTORE should name this file by.</summary>
    public string RestoreDevice => string.IsNullOrWhiteSpace(LocalPath) ? BlobUrl : LocalPath!;

    /// <summary>True when this is a file on a path rather than a blob.</summary>
    public bool IsOnDisk => !string.IsNullOrWhiteSpace(LocalPath);
    public BackupType Type { get; set; } = BackupType.Unknown;
    public long SizeBytes { get; set; }
    public DateTimeOffset LastModified { get; set; }

    public string? InferredServerName { get; set; }
    public string? InferredInstanceName { get; set; }
    public string? InferredDatabaseName { get; set; }
    public string? InferredClusterName { get; set; }
    public string? InferredAgName { get; set; }
    /// <summary>
    /// True when this file was identified using Ola Hallengren default AG naming (flat filename).
    /// </summary>
    public bool IsAgDefaultNaming { get; set; }

    /// <summary>
    /// True when this is a COPY_ONLY backup. Copy-only fulls do not reset the differential base,
    /// so they can never serve as the base for a differential restore.
    /// </summary>
    public bool IsCopyOnly { get; set; }
    /// <summary>
    /// When IsAgDefaultNaming, the backup set id (e.g. 20260226_200032) used for grouping stripes.
    /// </summary>
    public string? InferredSetId { get; set; }
    public string FileName => System.IO.Path.GetFileName(BlobName);

    // Populated from RESTORE HEADERONLY when connected to SQL Server
    public string? DatabaseName { get; set; }
    public DateTime? BackupStartDate { get; set; }
    public DateTime? BackupFinishDate { get; set; }
    public int? BackupTypeCode { get; set; }
    public decimal? FirstLsn { get; set; }
    public decimal? LastLsn { get; set; }

    /// <summary>
    /// For a differential, the CheckpointLSN of the full backup it is based on. Comparing this
    /// against a candidate full's CheckpointLSN is the authoritative test of whether the pair
    /// actually belongs together - timestamps only suggest it.
    /// </summary>
    public decimal? DatabaseBackupLsn { get; set; }

    /// <summary>
    /// LSN of the checkpoint taken during this backup. A differential's DatabaseBackupLSN must
    /// equal its base full's CheckpointLSN.
    /// </summary>
    public decimal? CheckpointLsn { get; set; }

    public bool HasDetailedMetadata => BackupStartDate.HasValue;

    /// <summary>Server as the filter dropdowns present it: <c>HOST\INSTANCE</c> or <c>HOST</c>.</summary>
    public string? ServerDisplay => ServerIdentity.Format(InferredServerName, InferredInstanceName);

    /// <summary>True when this file belongs to the given server filter (empty filter matches all).</summary>
    public bool MatchesServer(string? serverFilter)
        => ServerIdentity.Matches(InferredServerName, InferredInstanceName, serverFilter);

    /// <summary>
    /// Best available time for this file. Prefer the header's BackupStartDate, which is the
    /// backup server's local clock; without it fall back to the blob's LastModified, which is
    /// UTC. <see cref="EffectiveDateSource"/> says which you got - the two are not interchangeable.
    /// </summary>
    public DateTime EffectiveDate => BackupStartDate ?? BackupTime.WallClock(LastModified);

    public BackupTimestampSource EffectiveDateSource => BackupStartDate.HasValue
        ? BackupTimestampSource.BackupHeader
        : BackupTimestampSource.BlobLastModified;

    public bool IsEffectiveDateApproximate => !BackupStartDate.HasValue;

    public string EffectiveDateDisplay => BackupTime.Format(EffectiveDate, IsEffectiveDateApproximate);

    public string? EffectiveDateNote => IsEffectiveDateApproximate ? BackupTime.ApproximateNote : null;

    public string TypeDisplay => Type switch
    {
        BackupType.Full => "Full",
        BackupType.Differential => "Differential",
        BackupType.TransactionLog => "Transaction Log",
        _ => "Unknown"
    };

    public string SizeDisplay => ByteSize.Format(SizeBytes);

    public override string ToString()
        => $"[{TypeDisplay}] {BlobName} ({SizeDisplay}) - {EffectiveDate:yyyy-MM-dd HH:mm:ss}";
}

/// <summary>
/// Represents a logical backup operation that may consist of multiple striped files.
/// Files like 20260128_114441_1.bak and 20260128_114441_2.bak form one BackupSet.
/// </summary>
public class BackupSet
{
    public string SetId { get; set; } = string.Empty;
    public BackupType Type { get; set; }
    public List<BackupFileInfo> Files { get; set; } = [];
    /// <summary>
    /// When this backup was taken, as a bare wall clock. <see cref="TimestampSource"/> says which
    /// clock it is on - sets in one container can differ, and nothing reconciles them.
    /// </summary>
    public DateTime Timestamp { get; set; }

    public BackupTimestampSource TimestampSource { get; set; } = BackupTimestampSource.FileName;

    /// <summary>
    /// True when the time had to be taken from the blob rather than the filename, which puts it
    /// on a different clock (UTC) from its neighbours and can sort it hours out of position.
    /// </summary>
    public bool IsTimestampApproximate =>
        TimestampSource is BackupTimestampSource.BlobLastModified
                        or BackupTimestampSource.BlobLastModifiedConverted;

    /// <summary>
    /// True when the reading is not only approximate but on a DIFFERENT clock from its
    /// neighbours - which is what makes it sort out of position rather than merely be imprecise.
    /// Converting it into the backup server's zone (#102) removes this while leaving the
    /// approximation.
    /// </summary>
    public bool IsTimestampOnAnotherClock =>
        TimestampSource == BackupTimestampSource.BlobLastModified;

    public string TimestampDisplay => BackupTime.Format(Timestamp, IsTimestampApproximate);

    public string? TimestampNote => TimestampSource switch
    {
        BackupTimestampSource.BlobLastModified => BackupTime.ApproximateNote,
        BackupTimestampSource.BlobLastModifiedConverted => BackupTime.ConvertedNote,
        _ => null
    };

    public string? DatabaseName { get; set; }
    public string? ServerName { get; set; }

    /// <summary>
    /// Named instance this set came from, when the path pattern supplies one. Without it, two
    /// instances of the same host are indistinguishable once files are grouped into sets, and
    /// their backups interleave into a single restore timeline.
    /// </summary>
    public string? InstanceName { get; set; }

    /// <summary>
    /// True when this is a COPY_ONLY backup set. A copy-only full is a perfectly good restore
    /// point on its own and a valid anchor for a log chain, but it does NOT reset the
    /// differential base - so it can never be the base for a differential restore.
    /// </summary>
    public bool IsCopyOnly { get; set; }

    // ── LSNs, when the source knew them ─────────────────────────────────────────
    //
    // Null for a set discovered by listing a container: a blob name carries a type and a time and
    // nothing else, which is the whole of #130's complaint. An instance's own msdb hands these
    // over directly, so a set read from there can be paired definitively rather than by proximity
    // in time. Anything reading them has to cope with their absence.

    /// <summary>The checkpoint a full was taken at. A differential's <see cref="DatabaseBackupLsn"/> matches it.</summary>
    public decimal? CheckpointLsn { get; set; }

    /// <summary>
    /// Which full this set belongs to. Matched against a full's <see cref="CheckpointLsn"/>, this
    /// is the definitive test of whether a differential belongs to that full - timestamps only
    /// suggest it, and a copy-only full taken in between makes the suggestion wrong.
    /// </summary>
    public decimal? DatabaseBackupLsn { get; set; }

    public decimal? FirstLsn { get; set; }
    public decimal? LastLsn { get; set; }

    /// <summary>True when this set can be paired by LSN rather than by time.</summary>
    public bool HasLsns => CheckpointLsn.HasValue || DatabaseBackupLsn.HasValue;

    // ── whether this set has been checked against its own header (#130) ─────────
    //
    // Derived from the files rather than stored, so there is one answer and it cannot drift from
    // what the audit actually marked. A striped set is audited as a whole - one HEADERONLY covering
    // every stripe - so its files always agree.

    /// <summary>Every file checked, and every one of them matched.</summary>
    public bool AuditPassed =>
        Files.Count > 0 && Files.All(f => f.AuditState == BackupAuditState.Passed);

    /// <summary>The header disagreed with what the path claimed, or could not be read.</summary>
    public bool AuditFailed => Files.Any(f => f.AuditState == BackupAuditState.Failed);

    /// <summary>
    /// What to show in a grid column.
    ///
    /// Blank rather than "no" when nothing has been checked: not having asked is not a finding
    /// about the backup, and a column full of crosses would say the opposite.
    /// </summary>
    public string AuditDisplay =>
        AuditFailed ? "\u2717 mismatch"
        : AuditPassed ? "\u2713 audited"
        : string.Empty;

    /// <summary>Server as the filter dropdowns present it: <c>HOST\INSTANCE</c> or <c>HOST</c>.</summary>
    public string? ServerDisplay => ServerIdentity.Format(ServerName, InstanceName);

    /// <summary>True when this set belongs to the given server filter (empty filter matches all).</summary>
    public bool MatchesServer(string? serverFilter)
        => ServerIdentity.Matches(ServerName, InstanceName, serverFilter);

    public long TotalSizeBytes => Files.Sum(f => f.SizeBytes);
    public int FileCount => Files.Count;
    public bool IsStriped => Files.Count > 1;

    public string SizeDisplay => ByteSize.Format(TotalSizeBytes);

    public string TypeDisplay => Type switch
    {
        BackupType.Full => "Full",
        BackupType.Differential => "Diff",
        BackupType.TransactionLog => "Log",
        _ => "Unknown"
    };

    public string FilesDisplay => IsStriped
        ? $"{FileCount} files ({SizeDisplay})"
        : SizeDisplay;

    /// <summary>
    /// Extracts the backup set identifier (timestamp portion) from a filename.
    /// E.g. "20260128_114441_1.bak" → "20260128_114441", stripe 1
    ///      "20260128_114441.bak"   → "20260128_114441", stripe 0
    /// </summary>
    public static (string setId, int stripe) ParseFileName(string fileName)
    {
        var baseName = System.IO.Path.GetFileNameWithoutExtension(fileName);

        var match = Regex.Match(baseName, @"^(.+?)_(\d{1,2})$");
        if (match.Success && int.TryParse(match.Groups[2].Value, out int stripe))
        {
            var candidate = match.Groups[1].Value;
            if (Regex.IsMatch(candidate, @"\d{8}_\d{4,6}"))
                return (candidate, stripe);
        }

        return (baseName, 0);
    }

    /// <summary>
    /// Tries to parse a datetime from a set ID like "20260128_114441" or "20260128_220000".
    /// </summary>
    public static DateTime? ParseTimestamp(string setId)
    {
        var match = Regex.Match(setId, @"(\d{4})(\d{2})(\d{2})_(\d{2})(\d{2})(\d{2})?");
        if (!match.Success) return null;

        int year = int.Parse(match.Groups[1].Value);
        int month = int.Parse(match.Groups[2].Value);
        int day = int.Parse(match.Groups[3].Value);
        int hour = int.Parse(match.Groups[4].Value);
        int minute = int.Parse(match.Groups[5].Value);
        int second = match.Groups[6].Success ? int.Parse(match.Groups[6].Value) : 0;

        try { return new DateTime(year, month, day, hour, minute, second); }
        catch { return null; }
    }
}

/// <summary>
/// Represents a specific point in time that can be restored to, with the full chain needed.
/// </summary>
public class RestorePoint
{
    public DateTime Timestamp { get; set; }
    public BackupType Type { get; set; }
    // required, not null!. The old annotation claimed "never null" while the code disagreed with
    // itself about it: IsTimestampApproximate defended with ?., TotalFiles dereferenced bare, and
    // BackupChainValidator had a null check the compiler believed was always true. Both are only
    // ever built fully populated, so the compiler can be told that and then enforce it.
    public required BackupSet PrimarySet { get; set; }
    public required BackupSet RequiredFullSet { get; set; }
    public List<BackupSet> RequiredDiffSets { get; set; } = [];
    public List<BackupSet> RequiredLogSets { get; set; } = [];

    /// <summary>
    /// Position on timeline as a ratio (0.0 to 1.0). Computed by ViewModel.
    /// </summary>
    public double TimelinePosition { get; set; }

    /// <summary>
    /// Vertical stacking row (0 = bottom/track level, 1 = above, etc.). Computed by ViewModel.
    /// </summary>
    public int Row { get; set; }

    /// <summary>
    /// True when this point's own time is a blob-derived approximation, which is what decides
    /// where it lands on the timeline.
    /// </summary>
    public bool IsTimestampApproximate => PrimarySet.IsTimestampApproximate;

    public string TimestampDisplay => BackupTime.Format(Timestamp, IsTimestampApproximate);

    public string? TimestampNote => IsTimestampApproximate ? BackupTime.ApproximateNote : null;

    public string TypeDisplay => Type switch
    {
        BackupType.Full => "Full",
        BackupType.Differential => RequiredDiffSets.Count > 1
            ? $"Full + {RequiredDiffSets.Count} Diffs"
            : "Full + Diff",
        BackupType.TransactionLog => RequiredDiffSets.Count > 0
            ? $"Full + {RequiredDiffSets.Count} Diff(s) + {RequiredLogSets.Count} Log(s)"
            : $"Full + {RequiredLogSets.Count} Log(s)",
        _ => "Unknown"
    };

    /// <summary>Every set this restore point needs, in the order a restore applies them.</summary>
    public IEnumerable<BackupSet> AllRequiredSets
    {
        get
        {
            yield return RequiredFullSet;
            foreach (var diff in RequiredDiffSets) yield return diff;
            foreach (var log in RequiredLogSets) yield return log;
        }
    }

    // ── whether this whole point has been checked against its headers (#130) ────
    //
    // Per POINT rather than per set, because that is the decision being made in this grid: this is
    // the moment somebody is about to restore to, and what they want to know is whether every
    // backup needed to get there has been confirmed - not whether some of them have.

    /// <summary>Every set needed to reach this moment has been read and matched.</summary>
    public bool AuditPassed => AllRequiredSets.All(s => s.AuditPassed);

    /// <summary>At least one set needed to reach this moment did not match its header.</summary>
    public bool AuditFailed => AllRequiredSets.Any(s => s.AuditFailed);

    /// <summary>
    /// Blank until something has been checked. Not having asked is not a finding about the backup,
    /// and a column full of crosses would say the opposite.
    /// </summary>
    public string AuditDisplay =>
        AuditFailed ? "✗ mismatch"
        : AuditPassed ? "✓ audited"
        : string.Empty;

    public int TotalFiles
    {
        get
        {
            int count = RequiredFullSet.FileCount;
            count += RequiredDiffSets.Sum(d => d.FileCount);
            count += RequiredLogSets.Sum(l => l.FileCount);
            return count;
        }
    }

    public long TotalSizeBytes
    {
        get
        {
            long size = RequiredFullSet.TotalSizeBytes;
            size += RequiredDiffSets.Sum(d => d.TotalSizeBytes);
            size += RequiredLogSets.Sum(l => l.TotalSizeBytes);
            return size;
        }
    }

    public string SizeDisplay => ByteSize.Format(TotalSizeBytes);

    public string ChainDescription
    {
        get
        {
            var parts = new List<string> { "1 Full" };
            if (RequiredDiffSets.Count > 0) parts.Add($"{RequiredDiffSets.Count} Diff(s)");
            if (RequiredLogSets.Count > 0) parts.Add($"{RequiredLogSets.Count} Log(s)");
            return $"{string.Join(" + ", parts)} | {TotalFiles} files | {SizeDisplay}";
        }
    }

    public override string ToString()
        => $"{Timestamp:yyyy-MM-dd HH:mm:ss} [{TypeDisplay}]";
}

public class BackupChain
{
    public required BackupSet FullSet { get; set; }
    public List<BackupSet> DiffSets { get; set; } = [];
    public List<BackupSet> LogSets { get; set; } = [];
    public DateTime? StopAt { get; set; }

    public IEnumerable<BackupFileInfo> AllFiles
    {
        get
        {
            foreach (var f in FullSet.Files) yield return f;
            foreach (var diffSet in DiffSets)
                foreach (var f in diffSet.Files) yield return f;
            foreach (var logSet in LogSets)
                foreach (var f in logSet.Files) yield return f;
        }
    }

    public List<BackupSet> AllSets
    {
        get
        {
            var sets = new List<BackupSet> { FullSet };
            sets.AddRange(DiffSets);
            sets.AddRange(LogSets);
            return sets;
        }
    }

    public long TotalSizeBytes => AllFiles.Sum(f => f.SizeBytes);

    public int FileCount => AllFiles.Count();

    public string Summary
    {
        get
        {
            var parts = new List<string>
            {
                FullSet.IsStriped ? $"1 Full ({FullSet.FileCount} files)" : "1 Full"
            };
            if (DiffSets.Count > 0)
                parts.Add($"{DiffSets.Count} Diff(s)");
            if (LogSets.Count > 0)
                parts.Add($"{LogSets.Count} Log(s)");
            return string.Join(" + ", parts);
        }
    }

    /// <summary>
    /// The window a STOPAT target must fall within for this chain.
    ///
    /// Lower bound (exclusive) is whatever the database is restored up to before the final log is
    /// applied - the previous log, else the latest differential, else the full. Upper bound
    /// (inclusive) is the final log itself.
    ///
    /// Constraining the target to the LAST log's window is what keeps the generated chain valid:
    /// every earlier log ends before the target and so applies in full, introducing no gap. A
    /// target inside an EARLIER log would require truncating the chain to that log, otherwise the
    /// later logs restore across a gap and fail with error 4305.
    ///
    /// Null when the chain has no logs - STOPAT only applies to a log restore.
    /// </summary>
    public (DateTime Earliest, DateTime Latest)? StopAtWindow
    {
        get
        {
            if (LogSets.Count == 0) return null;

            var earliest = LogSets.Count >= 2
                ? LogSets[^2].Timestamp
                : DiffSets.Count > 0
                    ? DiffSets[^1].Timestamp
                    : FullSet.Timestamp;

            return (earliest, LogSets[^1].Timestamp);
        }
    }

    public static BackupChain FromRestorePoint(RestorePoint rp)
    {
        return new BackupChain
        {
            FullSet = rp.RequiredFullSet,
            DiffSets = rp.RequiredDiffSets,
            LogSets = rp.RequiredLogSets,
            StopAt = null
        };
    }
}

/// <summary>
/// A labelled tick mark on the timeline.
/// </summary>
public class TimelineTick
{
    public double Position { get; set; }
    public string Label { get; set; } = string.Empty;
}
