using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The retention referee (#241): what a keep-N-days rule would actually do - what goes, what must
/// stay DESPITE its age because kept restores depend on it, and what is already broken. The
/// classic blind spot it exists for: a lifecycle rule deletes the base full of a differential
/// somebody still needs, and the first sign is error 3136 mid-restore.
/// </summary>
public class RetentionAdvisorTests
{
    private static readonly DateTime Now = new(2026, 8, 10, 12, 0, 0);
    private const int KeepDays = 30;

    private static int _seq;

    private static BackupSet Set(
        BackupType type, int daysAgo, string db = "MyDb", long size = 100,
        decimal? checkpoint = null, decimal? baseLsn = null) => new()
    {
        SetId = $"s{++_seq}",
        DatabaseName = db,
        Type = type,
        Timestamp = Now.AddDays(-daysAgo),
        CheckpointLsn = checkpoint,
        DatabaseBackupLsn = baseLsn,
        Files = [new BackupFileInfo { BlobName = $"f{_seq}", SizeBytes = size }]
    };

    private static RetentionVerdict VerdictOf(List<RetentionFinding> findings, BackupSet set) =>
        findings.Single(f => ReferenceEquals(f.Set, set)).Verdict;

    /// <summary>
    /// The heart of it: the newest full OUTSIDE the window is what restores INSIDE the window
    /// start from, so the rule's own promise depends on a set the rule wants to delete.
    /// </summary>
    [Fact]
    public void TheBaseFullOutsideTheWindowIsKeptDespiteItsAge()
    {
        var oldFull = Set(BackupType.Full, daysAgo: 60);
        var baseFull = Set(BackupType.Full, daysAgo: 35);
        var keptLog = Set(BackupType.TransactionLog, daysAgo: 10);

        var findings = RetentionAdvisor.Advise([oldFull, baseFull, keptLog], KeepDays, Now);

        Assert.Equal(RetentionVerdict.Deletable, VerdictOf(findings, oldFull));
        Assert.Equal(RetentionVerdict.KeepDespiteAge, VerdictOf(findings, baseFull));
        Assert.Equal(RetentionVerdict.Keep, VerdictOf(findings, keptLog));
    }

    /// <summary>
    /// The subtle one: logs BETWEEN the base full and the window's edge are the bridge - without
    /// them point-in-time stops working at the window's own boundary.
    /// </summary>
    [Fact]
    public void BridgeLogsAreKeptAndOlderLogsAreNot()
    {
        var baseFull = Set(BackupType.Full, daysAgo: 40);
        var preBaseLog = Set(BackupType.TransactionLog, daysAgo: 45);
        var bridgeLog = Set(BackupType.TransactionLog, daysAgo: 33);

        var findings = RetentionAdvisor.Advise([baseFull, preBaseLog, bridgeLog], KeepDays, Now);

        Assert.Equal(RetentionVerdict.Deletable, VerdictOf(findings, preBaseLog));
        Assert.Equal(RetentionVerdict.KeepDespiteAge, VerdictOf(findings, bridgeLog));
        Assert.Contains("bridge", findings.Single(f => ReferenceEquals(f.Set, bridgeLog)).Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A differential under the window shortens the bridge, and is kept for it.</summary>
    [Fact]
    public void TheNewestDiffUnderTheWindowShortensTheBridge()
    {
        var baseFull = Set(BackupType.Full, daysAgo: 40, checkpoint: 100m);
        var baseDiff = Set(BackupType.Differential, daysAgo: 34, baseLsn: 100m);
        var preDiffLog = Set(BackupType.TransactionLog, daysAgo: 37);
        var bridgeLog = Set(BackupType.TransactionLog, daysAgo: 32);

        var findings = RetentionAdvisor.Advise([baseFull, baseDiff, preDiffLog, bridgeLog], KeepDays, Now);

        Assert.Equal(RetentionVerdict.KeepDespiteAge, VerdictOf(findings, baseDiff));
        Assert.Equal(RetentionVerdict.Deletable, VerdictOf(findings, preDiffLog));
        Assert.Equal(RetentionVerdict.KeepDespiteAge, VerdictOf(findings, bridgeLog));
    }

    /// <summary>A kept differential whose base full is GONE is not kept - it is already lost.</summary>
    [Fact]
    public void ADiffWhoseBaseIsGoneIsBrokenNotKept()
    {
        var wrongFull = Set(BackupType.Full, daysAgo: 20, checkpoint: 999m);
        var orphanDiff = Set(BackupType.Differential, daysAgo: 10, baseLsn: 100m);

        var findings = RetentionAdvisor.Advise([wrongFull, orphanDiff], KeepDays, Now);

        Assert.Equal(RetentionVerdict.Broken, VerdictOf(findings, orphanDiff));
    }

    [Fact]
    public void DatabasesAreJudgedIndependently()
    {
        var salesBase = Set(BackupType.Full, daysAgo: 35, db: "Sales");
        var payrollOld = Set(BackupType.Full, daysAgo: 35, db: "Payroll");
        var salesLog = Set(BackupType.TransactionLog, daysAgo: 5, db: "Sales");
        var payrollFull = Set(BackupType.Full, daysAgo: 5, db: "Payroll");

        var findings = RetentionAdvisor.Advise(
            [salesBase, payrollOld, salesLog, payrollFull], KeepDays, Now);

        // Sales still needs its out-of-window base; Payroll's old full carries nothing kept...
        Assert.Equal(RetentionVerdict.KeepDespiteAge, VerdictOf(findings, salesBase));
        // ...except being Payroll's base full for the window edge - which it IS, having no newer
        // full before the window. It stays too: same rule, honestly applied.
        Assert.Equal(RetentionVerdict.KeepDespiteAge, VerdictOf(findings, payrollOld));
    }

    [Fact]
    public void TheSummaryCountsAndCountsBytes()
    {
        var oldFull = Set(BackupType.Full, daysAgo: 60, size: 4_000);
        var baseFull = Set(BackupType.Full, daysAgo: 35, size: 5_000);
        var keptFull = Set(BackupType.Full, daysAgo: 5, size: 6_000);

        var findings = RetentionAdvisor.Advise([oldFull, baseFull, keptFull], KeepDays, Now);
        var summary = RetentionAdvisor.Summarise(findings, KeepDays);

        Assert.Contains("keeps 1 set(s)", summary);
        Assert.Contains("ALSO keep 1 older", summary);
        Assert.Contains("delete 1 set(s)", summary);
        Assert.Contains("3.9 KB", summary);   // 4,000 bytes
    }
}
