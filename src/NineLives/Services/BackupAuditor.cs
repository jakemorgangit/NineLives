using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Checks a database's backups against their own headers (#130).
///
/// Path-and-filename inference is right most of the time and wrong in ways that stay invisible
/// until a restore is needed - a log read as a full becomes a chain root and drops every earlier
/// log (#44); a full read as a differential never enters the fulls collection at all (#45). The
/// header is what the RESTORE itself reads, so it settles every one of those.
///
/// Scoped to ONE database because of what it costs. A header read is about 1.7 seconds, measured
/// against a real container, so a hundred sets is a few minutes and a whole 4,400-blob container is
/// most of an hour. One database is a coffee; the container is an afternoon, and nobody would run
/// it. That is why this is an explicit action with an estimate in front of it and a Stop behind it,
/// rather than something that happens on load.
/// </summary>
public class BackupAuditor(ISqlServerService sql, IBackupAuditStore store)
{
    /// <summary>
    /// What one header read costs, measured rather than guessed.
    ///
    /// Two runs against a real container: 5.8s per statement with a fresh connection each time, and
    /// 1.7s per statement once they shared one and connecting fell to nothing. The second is the
    /// figure to plan with; it is a blob round trip plus SQL Server reading a header, and it is not
    /// going to get much better.
    ///
    /// Deliberately a stated constant rather than a running average. An estimate that changes
    /// between one glance and the next is one nobody trusts, and being roughly right in front of a
    /// three-minute operation is the whole job.
    /// </summary>
    public static readonly TimeSpan MeasuredCostPerSet = TimeSpan.FromMilliseconds(1_700);

    /// <summary>How long auditing this many sets should take, given what is already cached.</summary>
    public static TimeSpan Estimate(int setsToRead) =>
        setsToRead <= 0 ? TimeSpan.Zero : MeasuredCostPerSet * setsToRead;

    /// <summary>
    /// The estimate as something to put in front of somebody about to wait for it.
    ///
    /// Rounded hard and upward. A number to the second implies a precision that is not there, and
    /// an estimate that runs over reads as a fault where one that comes in early does not.
    /// </summary>
    public static string DescribeEstimate(int setsToRead)
    {
        if (setsToRead <= 0) return "Everything here has been audited already - this will be instant.";

        var estimate = Estimate(setsToRead);

        var howLong = estimate.TotalSeconds < 90
            ? $"about {Math.Max(5, Math.Ceiling(estimate.TotalSeconds / 5) * 5):N0} seconds"
            : $"about {Math.Ceiling(estimate.TotalMinutes):N0} minute(s)";

        return $"Reading {setsToRead:N0} backup header(s) - {howLong}. " +
               "SQL Server reads each backup to answer, so this is a network round trip per set. " +
               "It can be stopped at any point, and what it has already checked is kept.";
    }

    /// <summary>Sets whose files are not already in the cache, and therefore have to be read.</summary>
    public static List<BackupSet> NotYetAudited(
        IEnumerable<BackupSet> sets, IReadOnlyDictionary<string, AuditRecord> cached) =>
        sets.Where(s => s.Files.Any(f => !cached.ContainsKey(BackupAuditStore.KeyFor(f)))).ToList();

    /// <summary>
    /// Reads the header of every set not already known, and reports where they disagree.
    ///
    /// Findings are reported rather than applied. A container full of them almost always means the
    /// PATH PATTERN is wrong, and quietly correcting each symptom hides the one thing worth
    /// knowing - so this says what it found and leaves the decision where it belongs.
    /// </summary>
    public async Task<List<BackupAuditFinding>> AuditAsync(
        ServerConnection server,
        IReadOnlyList<BackupSet> sets,
        IProgress<int>? progress = null,
        Action<string>? timing = null,
        CancellationToken ct = default)
    {
        var cached = store.Load();
        var findings = new List<BackupAuditFinding>();

        // Everything the cache already answers, without a round trip. This is what makes a second
        // audit worth running at all.
        var toRead = new List<BackupSet>();
        foreach (var set in sets)
        {
            var record = set.Files
                .Select(f => cached.GetValueOrDefault(BackupAuditStore.KeyFor(f)))
                .FirstOrDefault(r => r != null);

            if (record == null)
            {
                toRead.Add(set);
                continue;
            }

            MarkFiles(set, record.Passed);
            if (!record.Passed) findings.Add(FindingFor(set, record));
        }

        if (toRead.Count == 0)
        {
            progress?.Report(sets.Count);
            return findings;
        }

        var requests = toRead
            .Select(s => (IReadOnlyList<string>)s.Files.Select(f => BlobUrlEncoder.Encode(f.BlobUrl)).ToList())
            .ToList();

        var done = sets.Count - toRead.Count;
        var headers = await sql.RestoreHeaderOnlyBatchAsync(
            server, requests,
            new Progress<int>(n => progress?.Report(done + n)),
            timing, ct);

        var written = cached.Values.ToList();

        for (var i = 0; i < toRead.Count; i++)
        {
            var set = toRead[i];
            var header = i < headers.Count ? headers[i] : null;
            var finding = Compare(set, header);

            if (finding != null) findings.Add(finding);

            var passed = finding == null;
            MarkFiles(set, passed);

            // Recorded whatever the verdict. A file that failed does not want re-reading either -
            // the answer will not have changed, and it is the same round trip to find that out.
            foreach (var file in set.Files)
                written.Add(new AuditRecord(
                    BackupAuditStore.KeyFor(file),
                    passed,
                    header?.DatabaseName,
                    header?.BackupTypeCode,
                    DateTime.Now));
        }

        store.Save(written);

        return findings;
    }

    private static void MarkFiles(BackupSet set, bool passed)
    {
        foreach (var file in set.Files)
            file.AuditState = passed ? BackupAuditState.Passed : BackupAuditState.Failed;
    }

    /// <summary>What the header says against what the path claimed, or null when they agree.</summary>
    private static BackupAuditFinding? Compare(BackupSet set, BackupFileInfo? header)
    {
        var fileName = set.Files.FirstOrDefault()?.BlobName ?? set.SetId;

        if (header == null)
            return new BackupAuditFinding(set.SetId, fileName, BackupAuditVerdict.Unreadable,
                set.TypeDisplay, "SQL Server could not read a backup header from it.");

        // Type first: it is the disagreement that breaks a chain outright rather than misfiling it.
        if (header.Type != BackupType.Unknown && header.Type != set.Type)
            return new BackupAuditFinding(set.SetId, fileName, BackupAuditVerdict.WrongType,
                set.TypeDisplay, header.Type.ToString());

        if (!string.IsNullOrWhiteSpace(header.DatabaseName) &&
            !string.Equals(header.DatabaseName, set.DatabaseName, StringComparison.OrdinalIgnoreCase))
            return new BackupAuditFinding(set.SetId, fileName, BackupAuditVerdict.WrongDatabase,
                set.DatabaseName ?? "(no database)", header.DatabaseName);

        return null;
    }

    /// <summary>A disagreement remembered from a previous run, restated from what was cached.</summary>
    private static BackupAuditFinding FindingFor(BackupSet set, AuditRecord record)
    {
        var fileName = set.Files.FirstOrDefault()?.BlobName ?? set.SetId;

        return new BackupAuditFinding(
            set.SetId, fileName, BackupAuditVerdict.WrongType,
            set.TypeDisplay,
            record.DatabaseName is { } db
                ? $"{db} (from a previous audit)"
                : "something else (from a previous audit)");
    }
}
