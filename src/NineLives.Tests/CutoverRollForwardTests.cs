using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Copying for a cutover rather than a refresh (#451).
///
/// A copy normally brings the target online, which is right for a test refresh and wrong for a
/// migration: online means no more logs can be applied, so the copy is frozen at the moment it was
/// taken. Left in RESTORING, the long part - the full - happens in advance, and the switch-over
/// costs only the logs that accumulated since.
///
/// The gap check could not simply be ported from the Restore screen: this screen reads no container
/// chain at all, it writes its own full, so "what is this container missing" means nothing here.
/// The question that does mean something is which of the SOURCE's logs would carry that full
/// forward.
/// </summary>
public class CutoverRollForwardTests
{
    private static readonly DateTime T0 = new(2026, 8, 17, 14, 0, 0);

    private static BackupHistoryEntry Log(DateTime at, string folder = @"E:\SQLLogs", int stripes = 1)
    {
        var files = Enumerable.Range(1, stripes)
            .Select(n => stripes == 1
                ? $@"{folder}\Sales_{at:yyyyMMdd_HHmmss}.trn"
                : $@"{folder}\Sales_{at:yyyyMMdd_HHmmss}_{n}.trn")
            .ToList();

        return new BackupHistoryEntry
        {
            DatabaseName = "Sales",
            Type = BackupType.TransactionLog,
            StartedAt = at,
            Files = files,
            BackupSizeBytes = 1024
        };
    }

    // ── which logs are the right ones ───────────────────────────────────────────

    /// <summary>
    /// Strictly after the full, by the log's own start. A log that began before the full was taken
    /// is already inside it, and applying it would be applying something twice.
    /// </summary>
    [Fact]
    public void OnlyLogsTakenAfterTheCopyCount()
    {
        var history = new List<BackupHistoryEntry>
        {
            Log(T0.AddHours(-1)),   // before the full
            Log(T0),                // exactly at it
            Log(T0.AddHours(1)),
            Log(T0.AddHours(2))
        };

        var locations = BackupGapAnalyser.LogsTakenAfter(history, "Sales", T0);

        var only = Assert.Single(locations);
        Assert.Equal(2, only.Backups.Count);
        Assert.Equal(T0.AddHours(1), only.Earliest);
    }

    [Fact]
    public void FullsAndDifferentialsAreNotLogsToRollForwardWith()
    {
        var history = new List<BackupHistoryEntry>
        {
            new()
            {
                DatabaseName = "Sales", Type = BackupType.Full, StartedAt = T0.AddHours(1),
                Files = [@"E:\Backups\Sales.bak"]
            },
            Log(T0.AddHours(2))
        };

        var only = Assert.Single(BackupGapAnalyser.LogsTakenAfter(history, "Sales", T0));
        Assert.Single(only.Backups);
    }

    [Fact]
    public void ASourceThatHasTakenNoLogsSinceReportsNothing()
        => Assert.Empty(BackupGapAnalyser.LogsTakenAfter([Log(T0.AddHours(-2))], "Sales", T0));

    // ── the script ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Applied oldest first, and only the last statement recovers - a log applied out of order
    /// fails with 4305, and recovering early ends the sequence with logs still to go.
    /// </summary>
    [Fact]
    public void TheLogsAreAppliedInOrderAndOnlyTheLastRecovers()
    {
        var sql = LogRollForwardScript.Build("Sales_Migrated",
            [Log(T0.AddHours(3)), Log(T0.AddHours(1)), Log(T0.AddHours(2))]);

        var first = sql.IndexOf("Sales_20260817_150000.trn", StringComparison.Ordinal);
        var second = sql.IndexOf("Sales_20260817_160000.trn", StringComparison.Ordinal);
        var third = sql.IndexOf("Sales_20260817_170000.trn", StringComparison.Ordinal);

        Assert.True(first < second && second < third, "The logs are out of order.");

        Assert.Equal(2, CountOf(sql, "NORECOVERY,"));
        Assert.Equal(1, CountOf(sql, "RECOVERY,") - CountOf(sql, "NORECOVERY,"));
    }

    /// <summary>
    /// Left restorable, for somebody applying logs repeatedly up to the switch rather than
    /// finishing in one go.
    /// </summary>
    [Fact]
    public void ItCanLeaveTheTargetReadyForAnotherBatch()
    {
        var sql = LogRollForwardScript.Build(
            "Sales_Migrated", [Log(T0.AddHours(1))], bringOnline: false);

        Assert.Contains("NORECOVERY,", sql);
        Assert.Contains("WITH RECOVERY;", sql);          // the how-to-finish line
        Assert.Contains("ready for the next batch", sql);
    }

    /// <summary>
    /// The precondition said rather than assumed. Run against a database already online, every
    /// statement fails with 3101 - safe, but it reads as a broken script rather than as something
    /// nobody met the condition for.
    /// </summary>
    [Fact]
    public void ItStatesThatTheTargetMustStillBeRestoring()
    {
        var sql = LogRollForwardScript.Build("Sales_Migrated", [Log(T0.AddHours(1))]);

        Assert.Contains("must still be in RESTORING", sql);
        Assert.Contains("Leave the target ready for more log backups", sql);
    }

    /// <summary>A striped log is one media set - naming half of it fails with 3132.</summary>
    [Fact]
    public void AStripedLogIsOneStatementNamingEveryFile()
    {
        var sql = LogRollForwardScript.Build(
            "Sales_Migrated", [Log(T0.AddHours(1), stripes: 3)]);

        Assert.Equal(1, CountOf(sql, "RESTORE LOG"));
        Assert.Equal(3, CountOf(sql, "DISK = N'"));
    }

    [Fact]
    public void NoLogsAtAllSaysSoRatherThanEmittingNothing()
    {
        var sql = LogRollForwardScript.Build("Sales_Migrated", []);

        Assert.Contains("No log backups to apply", sql);
        Assert.DoesNotContain("RESTORE LOG", sql);
    }

    /// <summary>The target name is an identifier, and one holding a bracket must not break out.</summary>
    [Fact]
    public void TheTargetNameIsQuotedAsAnIdentifier()
    {
        var sql = LogRollForwardScript.Build("odd]name", [Log(T0.AddHours(1))]);
        Assert.Contains("[odd]]name]", sql);
    }

    // ── the screen ──────────────────────────────────────────────────────────────

    private static (CopyDatabaseViewModel vm, FakeSqlServerService sql) Ready()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection { Id = "s1", Name = "SRV01", ServerName = "SRV01" });
        store.Config.Servers.Add(new ServerConnection { Id = "s2", Name = "SRV02", ServerName = "SRV02" });
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1", Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });

        var sql = new FakeSqlServerService { DatabaseList = ["Sales"] };
        var vm = new CopyDatabaseViewModel(store, sql, TestLogs.Temp(),
            history: new FakeOperationHistoryStore());

        vm.Refresh();
        vm.SourceServer = vm.Servers.First(s => s.Id == "s1");
        vm.LoadSourceDatabasesCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        vm.SourceDatabase = "Sales";
        vm.Container = vm.Containers[0];
        vm.TargetServer = vm.Servers.First(s => s.Id == "s2");
        vm.TargetDatabaseName = "Sales_Migrated";
        return (vm, sql);
    }

    [Fact]
    public void ARefreshBringsTheTargetOnlineAndACutoverDoesNot()
    {
        var (vm, _) = Ready();

        vm.LeaveRestoringForMoreLogs = false;
        vm.GenerateCommand.Execute(null);
        Assert.Contains("RECOVERY,", vm.RestoreScript);
        Assert.DoesNotContain("NORECOVERY,", vm.RestoreScript);

        vm.LeaveRestoringForMoreLogs = true;
        vm.GenerateCommand.Execute(null);
        Assert.Contains("NORECOVERY,", vm.RestoreScript);
    }

    /// <summary>
    /// End to end on the screen: ask the source, then write the script for what came back.
    /// </summary>
    [Fact]
    public async Task TheScreenFindsTheLogsAndWritesTheScriptForThem()
    {
        var (vm, sql) = Ready();

        vm.LeaveRestoringForMoreLogs = true;
        vm.GenerateCommand.Execute(null);

        // Taken after the copy, so they count; the fake's clock is this machine's.
        sql.BackupHistory.Add(Log(DateTime.Now.AddMinutes(5)));
        sql.BackupHistory.Add(Log(DateTime.Now.AddMinutes(65)));

        await vm.CheckSourceLogsCommand.ExecuteAsync(null);
        Assert.True(vm.Gap.HasGap);

        vm.BuildRollForwardCommand.Execute(null);

        Assert.True(vm.HasRollForwardScript);
        Assert.Contains("RESTORE LOG [Sales_Migrated]", vm.RollForwardScript);
        Assert.Contains(@"E:\SQLLogs", vm.RollForwardScript);
    }

    /// <summary>
    /// Asked before the scripts exist there is no "since when", and inventing one would report the
    /// wrong logs - which on a cutover means a target that is short of where somebody thinks it is.
    /// </summary>
    [Fact]
    public async Task AskingBeforeGeneratingSaysWhyRatherThanGuessing()
    {
        var (vm, _) = Ready();
        vm.LeaveRestoringForMoreLogs = true;

        await vm.CheckSourceLogsCommand.ExecuteAsync(null);

        Assert.Contains("Generate the scripts first", vm.StatusMessage);
        Assert.False(vm.Gap.HasChecked);
    }

    private static int CountOf(string haystack, string needle)
    {
        int count = 0, at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }
}
