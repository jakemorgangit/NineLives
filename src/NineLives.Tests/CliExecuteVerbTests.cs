using System.IO;
using Blackcat.NineLives.Cli;
using Blackcat.NineLives.Cli.Verbs;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The CLI's execution verbs (#63 step 3), which exist to be hard to misuse: nothing runs
/// without --execute, WITH REPLACE is its own consent and --force cannot substitute for it,
/// the evidence-based preflights refuse before anything is dropped, and every executed run
/// leaves the same receipts the GUI leaves - history entries and webhook notifications - so
/// one estate has one story regardless of which front end acted.
/// </summary>
public class CliExecuteVerbTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static (CliServices services, FakeSqlServerService sql,
        FakeRestoreHistoryStore history, FakeRunNotifier notifier) Stage(
        params BackupHistoryEntry[] entries)
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV02", ServerName = "SRV02" });

        var sql = new FakeSqlServerService { BackupHistory = entries.ToList() };
        var history = new FakeRestoreHistoryStore();
        var notifier = new FakeRunNotifier();
        var services = new CliServices(
            store, sql, new FakeBlobStorageService(), history, notifier);
        return (services, sql, history, notifier);
    }

    private static BackupHistoryEntry Full(decimal checkpoint, DateTime at) => new()
    {
        DatabaseName = "MyDb",
        Type = BackupType.Full,
        StartedAt = at,
        FinishedAt = at.AddMinutes(5),
        CheckpointLsn = checkpoint,
        Position = 1,
        Files = [@"X:\backups\full.bak"]
    };

    private static async Task<(int exit, string output, string errors)> Run(
        Func<CliArguments, CliServices, TextWriter, TextWriter, CancellationToken, Task<int>> verb,
        VerbSpec spec, string[] args, CliServices services, CancellationToken ct = default)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var exit = await verb(CliArguments.Parse(args, spec), services, output, errors, ct);
        return (exit, output.ToString(), errors.ToString());
    }

    private static string[] RestoreArgs(params string[] extra) =>
        [.. new[] { "--server", "SRV01", "--database", "MyDb", "--target", "SRV02" }, .. extra];

    // ── nothing without --execute ───────────────────────────────────────────────

    [Fact]
    public async Task WithoutExecuteTheScriptPrintsAndNothingRuns()
    {
        var (services, sql, history, notifier) = Stage(Full(100, T0));

        var (exit, output, errors) = await Run(
            RestoreVerb.RunAsync, RestoreVerb.Spec, RestoreArgs(), services);

        Assert.Equal(0, exit);
        Assert.Contains("RESTORE DATABASE", output);
        Assert.Contains("Nothing was executed", errors);
        Assert.Empty(sql.ExecutedScripts);
        Assert.Empty(history.Entries);
        Assert.Empty(notifier.Sent);
    }

    /// <summary>
    /// The generate-only invocation still runs the preflights and carries their verdict in the
    /// exit code - a pipeline rehearses its own DR step without touching anything.
    /// </summary>
    [Fact]
    public async Task WithoutExecuteARefusalStillShowsInTheExitCode()
    {
        var (services, sql, _, _) = Stage(Full(100, T0));
        sql.DatabaseList = ["MyDb"];

        var (exit, output, errors) = await Run(
            RestoreVerb.RunAsync, RestoreVerb.Spec, RestoreArgs(), services);

        Assert.Equal(2, exit);
        Assert.Contains("REFUSED", errors);
        Assert.Contains("--execute would be refused", errors);
        Assert.Contains("RESTORE DATABASE", output);
        Assert.Empty(sql.ExecutedScripts);
    }

    // ── WITH REPLACE is its own consent ─────────────────────────────────────────

    [Fact]
    public async Task OverwritingAnExistingDatabaseNeedsWithReplace()
    {
        var (services, sql, history, _) = Stage(Full(100, T0));
        sql.DatabaseList = ["MyDb"];

        var (exit, _, errors) = await Run(
            RestoreVerb.RunAsync, RestoreVerb.Spec, RestoreArgs("--execute"), services);

        Assert.Equal(2, exit);
        Assert.Contains("--with-replace", errors);
        Assert.Empty(sql.ExecutedScripts);
        Assert.Empty(history.Entries);
    }

    /// <summary>--force overrides evidence, never consent: an existing database stays refused.</summary>
    [Fact]
    public async Task ForceDoesNotSubstituteForWithReplace()
    {
        var (services, sql, _, _) = Stage(Full(100, T0));
        sql.DatabaseList = ["MyDb"];

        var (exit, _, errors) = await Run(
            RestoreVerb.RunAsync, RestoreVerb.Spec,
            RestoreArgs("--execute", "--force"), services);

        Assert.Equal(2, exit);
        Assert.Contains("--with-replace", errors);
        Assert.Empty(sql.ExecutedScripts);
    }

    [Fact]
    public async Task WithReplaceSaidExplicitlyTheRestoreRuns()
    {
        var (services, sql, history, _) = Stage(Full(100, T0));
        sql.DatabaseList = ["MyDb"];

        var (exit, _, _) = await Run(
            RestoreVerb.RunAsync, RestoreVerb.Spec,
            RestoreArgs("--execute", "--with-replace"), services);

        Assert.Equal(0, exit);
        var script = Assert.Single(sql.ExecutedScripts);
        Assert.Contains("REPLACE", script);
        Assert.Single(history.Entries);
    }

    // ── the evidence-based preflights ───────────────────────────────────────────

    [Fact]
    public async Task ANewerBackupAimedAtAnOlderServerIsRefusedByName()
    {
        var (services, sql, _, _) = Stage(Full(100, T0));
        sql.Header = new BackupFileInfo { SoftwareVersionMajor = 17 };
        sql.MajorVersionByServer["SRV02"] = 16;

        var (exit, _, errors) = await Run(
            RestoreVerb.RunAsync, RestoreVerb.Spec, RestoreArgs("--execute"), services);

        Assert.Equal(2, exit);
        Assert.Contains("3169", errors);
        Assert.Empty(sql.ExecutedScripts);
    }

    [Fact]
    public async Task AMissingTdeCertificateIsRefusedWithItsThumbprint()
    {
        var (services, sql, _, _) = Stage(Full(100, T0));
        sql.Header = new BackupFileInfo { TdeThumbprint = [0xAA, 0xBB] };

        var (exit, _, errors) = await Run(
            RestoreVerb.RunAsync, RestoreVerb.Spec, RestoreArgs("--execute"), services);

        Assert.Equal(2, exit);
        Assert.Contains("0xAABB", errors);
        Assert.Contains("33111", errors);
        Assert.Empty(sql.ExecutedScripts);
    }

    /// <summary>--force is the deliberate override for what the evidence says - loudly.</summary>
    [Fact]
    public async Task ForceDowngradesAnEvidenceRefusalToAWarningAndRuns()
    {
        var (services, sql, _, _) = Stage(Full(100, T0));
        sql.Header = new BackupFileInfo { SoftwareVersionMajor = 17 };
        sql.MajorVersionByServer["SRV02"] = 16;

        var (exit, _, errors) = await Run(
            RestoreVerb.RunAsync, RestoreVerb.Spec,
            RestoreArgs("--execute", "--force"), services);

        Assert.Equal(0, exit);
        Assert.Contains("WARNING", errors);
        Assert.Contains("--force", errors);
        Assert.Single(sql.ExecutedScripts);
    }

    // ── receipts ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnExecutedRestoreLeavesTheSameReceiptsTheAppLeaves()
    {
        var (services, sql, history, notifier) = Stage(Full(100, T0));

        var (exit, _, _) = await Run(
            RestoreVerb.RunAsync, RestoreVerb.Spec, RestoreArgs("--execute"), services);

        Assert.Equal(0, exit);
        Assert.Contains("RESTORE DATABASE", Assert.Single(sql.ExecutedScripts));

        var entry = Assert.Single(history.Entries);
        Assert.Equal("Restore", entry.Kind);
        Assert.Equal("MyDb", entry.TargetDatabase);
        Assert.Equal("SRV02", entry.ServerName);
        Assert.Equal(RestoreOutcome.Succeeded, entry.Outcome);
        Assert.Contains("RESTORE DATABASE", entry.Script);

        Assert.Equal(2, notifier.Sent.Count);
        Assert.Equal(RunPhase.Started, notifier.Sent[0].Phase);
        Assert.Equal(RunPhase.Succeeded, notifier.Sent[1].Phase);
        Assert.Equal("MyDb", notifier.Sent[1].Subject);
    }

    [Fact]
    public async Task AFailedRestoreRecordsTheFailureAndNotifiesTheProblem()
    {
        var (services, sql, history, notifier) = Stage(Full(100, T0));
        sql.FailOnExecuteNumber = 1;

        var (exit, _, errors) = await Run(
            RestoreVerb.RunAsync, RestoreVerb.Spec, RestoreArgs("--execute"), services);

        Assert.Equal(2, exit);
        Assert.Contains("FAILED", errors);

        var entry = Assert.Single(history.Entries);
        Assert.Equal(RestoreOutcome.Failed, entry.Outcome);
        Assert.NotNull(entry.ErrorMessage);

        Assert.Equal(RunPhase.Problem, notifier.Sent.Last().Phase);
    }

    // ── rehearse ────────────────────────────────────────────────────────────────

    private static string[] RehearseArgs(params string[] extra) =>
        [.. new[] { "--server", "SRV01", "--database", "MyDb", "--target", "SRV02" }, .. extra];

    private static void GiveFileList(FakeSqlServerService sql) =>
        sql.FileList =
        [
            new FileMoveOption
            {
                LogicalName = "MyDb",
                PhysicalName = @"C:\SQL\MyDb.mdf",
                NewPhysicalName = @"C:\SQL\MyDb.mdf",
                SizeBytes = 1024
            }
        ];

    [Fact]
    public async Task ARehearsalProvesRestoresChecksAndDrops()
    {
        var (services, sql, history, notifier) = Stage(Full(100, T0));
        GiveFileList(sql);

        var (exit, _, errors) = await Run(
            RehearseVerb.RunAsync, RehearseVerb.Spec, RehearseArgs("--execute"), services);

        Assert.Equal(0, exit);
        var script = Assert.Single(sql.ExecutedScripts);
        Assert.Contains("RESTORE DATABASE", script);
        Assert.Contains("DBCC CHECKDB", script);
        Assert.Contains("DROP DATABASE", script);
        Assert.DoesNotContain("REPLACE", script);

        var entry = Assert.Single(history.Entries);
        Assert.Equal("Rehearsal", entry.Kind);
        Assert.Equal("MyDb", entry.SourceDatabase);
        Assert.StartsWith("MyDb_rehearsal_", entry.TargetDatabase);

        // The notification names the database being PROVEN, not the scratch copy.
        Assert.Equal("MyDb", notifier.Sent.Last().Subject);
        Assert.Contains("PROVEN", errors);
    }

    [Fact]
    public async Task WithoutExecuteTheRehearsalOnlyPrints()
    {
        var (services, sql, history, _) = Stage(Full(100, T0));
        GiveFileList(sql);

        var (exit, output, _) = await Run(
            RehearseVerb.RunAsync, RehearseVerb.Spec, RehearseArgs(), services);

        Assert.Equal(0, exit);
        Assert.Contains("DBCC CHECKDB", output);
        Assert.Empty(sql.ExecutedScripts);
        Assert.Empty(history.Entries);
    }

    [Fact]
    public async Task AFailedRehearsalSaysTheScratchCopyIsRetained()
    {
        var (services, sql, history, _) = Stage(Full(100, T0));
        GiveFileList(sql);
        sql.FailOnExecuteNumber = 1;

        var (exit, _, errors) = await Run(
            RehearseVerb.RunAsync, RehearseVerb.Spec, RehearseArgs("--execute"), services);

        Assert.Equal(2, exit);
        Assert.Contains("NOT PROVEN", errors);
        Assert.Contains("retained", errors);
        Assert.Equal("Rehearsal", Assert.Single(history.Entries).Kind);
    }

    // ── the restore must physically fit (#297) ──────────────────────────────────

    private static FileMoveOption DataFile(string path, long size) => new()
    {
        LogicalName = "MyDb",
        PhysicalName = path,
        NewPhysicalName = path,
        Type = "D",
        SizeBytes = size
    };

    [Fact]
    public async Task ARestoreThatCannotFitIsRefusedWithTheShortfallNamed()
    {
        var (services, sql, history, _) = Stage(Full(100, T0));
        sql.FileList = [DataFile(@"D:\Data\MyDb.mdf", 100L * 1024 * 1024 * 1024)];
        sql.VolumeFreeSpace = new() { [@"D:\"] = 10L * 1024 * 1024 * 1024 };

        var (exit, _, errors) = await Run(
            RestoreVerb.RunAsync, RestoreVerb.Spec, RestoreArgs("--execute"), services);

        Assert.Equal(2, exit);
        Assert.Contains("short by", errors);
        Assert.Empty(sql.ExecutedScripts);
        Assert.Empty(history.Entries);
    }

    /// <summary>Space is evidence, not consent - --force may override it, loudly.</summary>
    [Fact]
    public async Task ForceDowngradesTheSpaceRefusalToAWarningAndProceeds()
    {
        var (services, sql, history, _) = Stage(Full(100, T0));
        sql.FileList = [DataFile(@"D:\Data\MyDb.mdf", 100L * 1024 * 1024 * 1024)];
        sql.VolumeFreeSpace = new() { [@"D:\"] = 10L * 1024 * 1024 * 1024 };

        var (exit, _, errors) = await Run(
            RestoreVerb.RunAsync, RestoreVerb.Spec, RestoreArgs("--execute", "--force"), services);

        Assert.Equal(0, exit);
        Assert.Contains("--force: proceeding anyway", errors);
        Assert.NotEmpty(sql.ExecutedScripts);
        Assert.Single(history.Entries);
    }

    /// <summary>
    /// dm_os_volume_stats only reports volumes that host database files, so an absent volume
    /// usually means the drive does not exist there - a different fix than freeing space.
    /// </summary>
    [Fact]
    public async Task AMissingVolumeIsRefusedAsAbsentNotAsFull()
    {
        var (services, sql, _, _) = Stage(Full(100, T0));
        sql.FileList = [DataFile(@"D:\Data\MyDb.mdf", 1024)];
        sql.VolumeFreeSpace = new() { [@"C:\"] = 500L * 1024 * 1024 * 1024 };

        var (exit, _, errors) = await Run(
            RestoreVerb.RunAsync, RestoreVerb.Spec, RestoreArgs("--execute"), services);

        Assert.Equal(2, exit);
        Assert.Contains(@"no D:\ volume", errors);
        Assert.Empty(sql.ExecutedScripts);
    }

    /// <summary>Not being able to check is not the same as not fitting.</summary>
    [Fact]
    public async Task AnUnanswerableSpaceQuestionRefusesNothing()
    {
        var (services, sql, history, _) = Stage(Full(100, T0));
        sql.FileList = [DataFile(@"D:\Data\MyDb.mdf", 100L * 1024 * 1024 * 1024)];
        sql.VolumeCheckThrows = new InvalidOperationException("VIEW SERVER STATE denied");

        var (exit, _, _) = await Run(
            RestoreVerb.RunAsync, RestoreVerb.Spec, RestoreArgs("--execute"), services);

        Assert.Equal(0, exit);
        Assert.Single(history.Entries);
    }
}
