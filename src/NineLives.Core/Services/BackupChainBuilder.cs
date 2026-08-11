using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

public class BackupChainBuilder
{
    /// <summary>
    /// Computes all valid, discrete restore points from the available backup sets.
    /// Each restore point represents an actual time you can restore to with a valid chain.
    /// </summary>
    public List<RestorePoint> ComputeRestorePoints(List<BackupSet> allSets)
    {
        // A chain belongs to ONE database on ONE instance, and this used to partition the sets
        // by Type alone (#362).
        //
        // Grouping goes to real trouble to keep two servers' same-named databases apart - a
        // container holding Sales from SRV01 and SRV02 is the everyday DR pair - and then the
        // pairing below discarded the distinction and matched a full from one with the logs of
        // the other. The app never saw it because the inventory screen filters to one instance
        // before it gets here; the CLI has no such filter, and a container source cannot even
        // narrow to a server, so `9lives script --container backups --database Sales` produced
        // exactly that mixture. Nothing downstream catches it either: the identity check
        // compares database NAMES, and both are Sales.
        //
        // Chains are therefore built per identity and the results concatenated. Nothing is
        // dropped and nothing is refused: each server's points are offered, each internally
        // consistent. The single-identity case - which is every path through the app - takes
        // exactly the route it always did.
        var identities = allSets
            .GroupBy(s => (Database: s.DatabaseName ?? string.Empty,
                           Server: s.ServerDisplay ?? string.Empty))
            .ToList();

        if (identities.Count > 1)
            return [.. identities.SelectMany(g => ComputeRestorePointsForOneIdentity([.. g]))];

        return ComputeRestorePointsForOneIdentity(allSets);
    }

    private List<RestorePoint> ComputeRestorePointsForOneIdentity(List<BackupSet> allSets)
    {
        var fulls = allSets.Where(s => s.Type == BackupType.Full).OrderBy(s => s.Timestamp).ToList();
        var diffs = allSets.Where(s => s.Type == BackupType.Differential).OrderBy(s => s.Timestamp).ToList();
        var logs = allSets.Where(s => s.Type == BackupType.TransactionLog).OrderBy(s => s.Timestamp).ToList();

        var points = new List<RestorePoint>();

        foreach (var full in fulls)
        {
            points.Add(new RestorePoint
            {
                Timestamp = full.Timestamp,
                Type = BackupType.Full,
                PrimarySet = full,
                RequiredFullSet = full
            });
        }

        // A differential's base is the latest NON-copy-only full at or before it.
        //
        // A copy-only full does not reset the differential base: the base LSN stays with the last
        // regular full, and SQL Server rejects a differential applied on top of a copy-only full
        // with error 3136 ("the database has not been restored to the correct earlier state").
        // Ola writes copy-only backups into the same FULL folder with the same naming, so before
        // this an ad-hoc copy-only taken mid-week silently became the base for every subsequent
        // differential - and, via the log-chain code below, broke every restore point from that
        // differential onward until the next regular full.
        //
        // Copy-only sets remain first-class everywhere else: they are offered as Full restore
        // points above, and they anchor log chains below, both of which are perfectly valid.
        // When the sets carry LSNs - which they do when they were read from an instance's own msdb
        // rather than inferred from blob names - the pairing is not a rule of thumb at all. A
        // differential's DatabaseBackupLsn IS the CheckpointLsn of the full it was taken against;
        // that is what SQL Server itself checks before it raises 3136. So where the data is there,
        // ask it rather than reasoning about the timestamps (#130).
        //
        // This also removes the copy-only special case rather than merely handling it: a diff taken
        // against a regular full simply does not carry a copy-only full's checkpoint, however the
        // two are arranged in time.
        BackupSet? BaseFullFor(BackupSet diff)
        {
            if (diff.DatabaseBackupLsn is { } baseLsn)
            {
                var byLsn = fulls.LastOrDefault(f => f.CheckpointLsn == baseLsn);
                if (byLsn != null) return byLsn;

                // A diff whose base is not among the fulls in hand is genuinely unrestorable, and
                // saying so is the point. Falling back to the nearest by time here would hand back
                // a full that SQL Server will reject, which is worse than offering nothing - the
                // restore fails after WITH REPLACE has already dropped the target.
                if (fulls.Any(f => f.HasLsns)) return null;
            }

            return fulls.LastOrDefault(f => !f.IsCopyOnly && f.Timestamp <= diff.Timestamp);
        }

        // Each diff restore point: Full + that single diff only (differentials are cumulative
        // since the last full, so earlier diffs are never needed in the chain).
        foreach (var diff in diffs)
        {
            var baseFull = BaseFullFor(diff);
            if (baseFull == null) continue;

            points.Add(new RestorePoint
            {
                Timestamp = diff.Timestamp,
                Type = BackupType.Differential,
                PrimarySet = diff,
                RequiredFullSet = baseFull,
                RequiredDiffSets = [diff]
            });
        }

        // For transaction logs, build a chain from each full forward - one restore point per log.
        foreach (var full in fulls)
        {
            var nextFull = fulls.FirstOrDefault(f => f.Timestamp > full.Timestamp);
            var upperBound = nextFull?.Timestamp ?? DateTime.MaxValue;

            // Which logs carry this chain forward.
            //
            // By time, that means a log taken after the full - which is a good approximation and
            // is wrong in one real case: on a busy instance a log backup that STARTED before the
            // full can finish after it. By time it is excluded; by LSN it may hold transactions the
            // restore still needs, and dropping it breaks the chain at that point.
            //
            // So where the sets carry LSNs, ask them. LastLSN past the full's LastLSN is the same
            // test SQL Server applies when it decides whether a log is applicable at all (#130).
            var applicableLogs = logs
                .Where(l => CarriesTheChainPast(l, full, sameInstantCounts: false) && l.Timestamp < upperBound)
                .OrderBy(l => l.Timestamp)
                .ToList();

            if (applicableLogs.Count == 0) continue;

            // Only diffs whose ACTUAL base is this full may join a chain - being inside the range
            // is not enough. When the anchor is a copy-only full no diff ever qualifies, so the
            // chain becomes copy-only full + logs, which is valid.
            var applicableDiffs = diffs
                .Where(d => d.Timestamp > full.Timestamp && d.Timestamp < upperBound
                            && ReferenceEquals(BaseFullFor(d), full))
                .OrderBy(d => d.Timestamp)
                .ToList();

            // The differential is chosen PER LOG: the latest one taken at or before that log.
            //
            // Previously a single latestDiff was picked for the whole range and the logs were then
            // filtered to those at or after it - so every log between the full and the latest
            // differential produced NO restore point at all. Measured against a real 24-day
            // Ola-style set, that hid 73 of 94 log backups (78%), a 72-hour window in which no
            // point-in-time recovery was offered even though every backup needed for it existed
            // and was already listed in the browse view.
            //
            // Choosing per log restores the natural rule: use the shortest valid chain that
            // reaches this point. Before the first differential that means full + logs, which is
            // a chain SQL Server restores happily - it was simply never generated.
            foreach (var log in applicableLogs)
            {
                var diff = applicableDiffs.LastOrDefault(d => d.Timestamp <= log.Timestamp);

                // Where the restore has already reached once the full, and any differential, are
                // applied - and therefore which logs are still needed to get from there to here.
                var reached = diff ?? full;

                var chainLogs = applicableLogs
                    .Where(l => CarriesTheChainPast(l, reached, sameInstantCounts: true) && l.Timestamp <= log.Timestamp)
                    .ToList();

                points.Add(new RestorePoint
                {
                    Timestamp = log.Timestamp,
                    Type = BackupType.TransactionLog,
                    PrimarySet = log,
                    RequiredFullSet = full,
                    RequiredDiffSets = diff != null ? [diff] : [],
                    RequiredLogSets = chainLogs
                });
            }
        }

        return points.OrderBy(p => p.Timestamp).ToList();
    }

    /// <summary>
    /// Whether a log carries the chain PAST the point a backup restores to - so whether it still
    /// has anything the restore needs (#130).
    ///
    /// LSNs when both sets have them, timestamps otherwise. The two answer the same question with
    /// different accuracy: a timestamp says when a backup was taken, an LSN says what is IN it.
    /// They differ exactly when a log backup overlaps the backup it follows, which on a busy
    /// instance is routine - and getting it wrong there either drops a log the chain needs, or
    /// includes one SQL Server will reject as already applied.
    ///
    /// A set from a container listing has no LSNs, so this is the same timestamp rule it has always
    /// used.
    /// </summary>
    /// <param name="sameInstantCounts">
    /// What to do when the two readings are identical, which only arises without LSNs.
    ///
    /// The two callers genuinely differ here and it is not an oversight. A log sharing a FULL's
    /// exact timestamp is a collision - nothing says which came first, so it cannot be trusted to
    /// follow the full, and #46 exists because treating it as though it did produced restore points
    /// that could not be restored. A log sharing the DIFFERENTIAL's timestamp is a different case:
    /// the differential was picked as the latest one at or before this log, so equality there means
    /// the log is the one being restored to.
    ///
    /// With LSNs the question does not arise: two backups cannot both end at the same LSN and
    /// differ in what they contain.
    /// </param>
    private static bool CarriesTheChainPast(BackupSet log, BackupSet reached, bool sameInstantCounts)
    {
        if (log.LastLsn is { } logEnd && reached.LastLsn is { } reachedEnd)
            return logEnd > reachedEnd;

        return sameInstantCounts
            ? log.Timestamp >= reached.Timestamp
            : log.Timestamp > reached.Timestamp;
    }

    /// <summary>
    /// Builds a BackupChain from a selected RestorePoint.
    /// </summary>
    public BackupChain BuildChainFromRestorePoint(RestorePoint restorePoint)
    {
        return BackupChain.FromRestorePoint(restorePoint);
    }

    /// <summary>
    /// Returns the available restore window (earliest possible to latest possible).
    /// </summary>
    public (DateTime earliest, DateTime latest)? GetRestoreWindow(List<BackupSet> sets)
    {
        var fulls = sets.Where(s => s.Type == BackupType.Full).ToList();
        if (fulls.Count == 0) return null;

        var earliest = fulls.Min(f => f.Timestamp);
        var latest = sets.Max(s => s.Timestamp);

        return (earliest, latest);
    }

    // The file-level overload that used to sit here is gone (#42). It was labelled "backward
    // compatibility during migration", had no production caller, and mixed two time bases: it
    // compared BackupStartDate (the backup server's local clock) against the blob's LastModified
    // (UTC). Keeping it around was an invitation for a future caller to reintroduce the skew that
    // #47 went to some trouble to make visible.
}
