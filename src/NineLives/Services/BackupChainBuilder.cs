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

        // Each diff restore point: Full + that single diff only (differentials are cumulative
        // since the last full, so earlier diffs are never needed in the chain).
        foreach (var diff in diffs)
        {
            var baseFull = fulls.LastOrDefault(f => f.Timestamp <= diff.Timestamp);
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

        // For transaction logs, build chains from each full forward.
        // Includes all diffs in the full's range, then logs after the last diff.
        foreach (var full in fulls)
        {
            var nextFull = fulls.FirstOrDefault(f => f.Timestamp > full.Timestamp);
            var upperBound = nextFull?.Timestamp ?? DateTime.MaxValue;

            var applicableLogs = logs
                .Where(l => l.Timestamp > full.Timestamp && l.Timestamp < upperBound)
                .OrderBy(l => l.Timestamp)
                .ToList();

            if (applicableLogs.Count == 0) continue;

            // At most one diff: the latest one before the log chain (differentials are cumulative since the last full).
            var diffsInRange = diffs
                .Where(d => d.Timestamp > full.Timestamp && d.Timestamp < upperBound)
                .OrderBy(d => d.Timestamp)
                .ToList();
            var latestDiff = diffsInRange.Count > 0 ? diffsInRange.Last() : null;
            var baseTimestamp = latestDiff != null ? latestDiff.Timestamp : full.Timestamp;

            var chainLogs = applicableLogs
                .Where(l => l.Timestamp >= baseTimestamp)
                .OrderBy(l => l.Timestamp)
                .ToList();

            var logChainSoFar = new List<BackupSet>();
            for (int i = 0; i < chainLogs.Count; i++)
            {
                logChainSoFar.Add(chainLogs[i]);

                points.Add(new RestorePoint
                {
                    Timestamp = chainLogs[i].Timestamp,
                    Type = BackupType.TransactionLog,
                    PrimarySet = chainLogs[i],
                    RequiredFullSet = full,
                    RequiredDiffSets = latestDiff != null ? [latestDiff] : [],
                    RequiredLogSets = [.. logChainSoFar]
                });
            }
        }

        return points.OrderBy(p => p.Timestamp).ToList();
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

    // Keep backward compatibility for existing callers during migration
    public (DateTime earliest, DateTime latest)? GetRestoreWindow(List<BackupFileInfo> allBackups)
    {
        var fulls = allBackups.Where(b => b.Type == BackupType.Full).ToList();
        if (fulls.Count == 0) return null;

        var earliest = fulls.Min(f => f.EffectiveDate);
        var latest = allBackups.Max(b => b.EffectiveDate);

        return (earliest, latest);
    }
}
