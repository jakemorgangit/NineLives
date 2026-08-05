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
    BlobLastModified
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

    /// <summary>Explains an approximate reading. Shown as a tooltip wherever one is displayed.</summary>
    public const string ApproximateNote =
        "Approximate. This file's name carries no timestamp, so the time shown is when the blob "
        + "was last modified (UTC), not when the backup was taken on the server. It may sit hours "
        + "away from the other entries.";

    /// <summary>Prefix marking a displayed time as approximate.</summary>
    public const string ApproximateMarker = "~";

    public static string Format(DateTime value, bool approximate)
        => approximate
            ? ApproximateMarker + value.ToString("yyyy-MM-dd HH:mm:ss")
            : value.ToString("yyyy-MM-dd HH:mm:ss");
}

public class BackupFileInfo
{
    public string BlobName { get; set; } = string.Empty;
    public string BlobUrl { get; set; } = string.Empty;
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
    public bool IsTimestampApproximate => TimestampSource == BackupTimestampSource.BlobLastModified;

    public string TimestampDisplay => BackupTime.Format(Timestamp, IsTimestampApproximate);

    public string? TimestampNote => IsTimestampApproximate ? BackupTime.ApproximateNote : null;

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
    public BackupSet PrimarySet { get; set; } = null!;
    public BackupSet RequiredFullSet { get; set; } = null!;
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
    public bool IsTimestampApproximate => PrimarySet?.IsTimestampApproximate ?? false;

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
    public BackupSet FullSet { get; set; } = null!;
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
