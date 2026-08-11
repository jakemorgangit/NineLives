using System.IO;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.Cli;
using Blackcat.NineLives.Cli.Verbs;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The other half of the orchestrator (#300): 9lives backup, with the app's rules travelling -
/// COPY_ONLY by default and loud when disabled, destinations from the same layout rules every
/// browser reads, generate-only without --execute, receipts and webhooks like the other
/// execution verbs.
/// </summary>
public class CliBackupVerbTests
{
    private static (CliServices services, FakeSqlServerService sql,
                    FakeRestoreHistoryStore history, FakeRunNotifier notifier) Stage()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });

        var sql = new FakeSqlServerService();
        var history = new FakeRestoreHistoryStore();
        var notifier = new FakeRunNotifier();
        var services = new CliServices(
            store, sql, new FakeBlobStorageService(), history, notifier);
        return (services, sql, history, notifier);
    }

    private static async Task<(int exit, string output, string errors)> Run(
        string[] args, CliServices services)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var exit = await BackupVerb.RunAsync(
            CliArguments.Parse(args, BackupVerb.Spec), services, output, errors);
        return (exit, output.ToString(), errors.ToString());
    }

    [Fact]
    public async Task WithoutExecuteTheScriptPrintsAndNothingRuns()
    {
        var (services, sql, history, notifier) = Stage();

        var (exit, output, errors) = await Run(
            ["--container", "backups", "--server", "SRV01", "--database", "Sales"], services);

        Assert.Equal(0, exit);
        Assert.Contains("BACKUP DATABASE [Sales]", output);
        Assert.Contains("COPY_ONLY", output);
        Assert.Contains("Nothing was executed", errors);
        Assert.Empty(sql.ExecutedScripts);
        Assert.Empty(history.Entries);
        Assert.Empty(notifier.Sent);
    }

    /// <summary>The blob layout is the container's own pattern - what every browser reads.</summary>
    [Fact]
    public async Task TheBlobDestinationFollowsTheContainersLayout()
    {
        var (services, _, _, _) = Stage();

        var (_, output, _) = await Run(
            ["--container", "backups", "--server", "SRV01", "--database", "Sales"], services);

        Assert.Contains("FULL/SRV01/Sales/", output);
        Assert.Contains("_COPY_ONLY_", output);
        Assert.Contains(".bak", output);
    }

    [Fact]
    public async Task DisablingCopyOnlyIsLoudInBothPlaces()
    {
        var (services, _, _, _) = Stage();

        var (_, output, errors) = await Run(
            ["--container", "backups", "--server", "SRV01", "--database", "Sales",
             "--not-copy-only"], services);

        Assert.Contains("RESETS THE DIFFERENTIAL BASE", errors);
        Assert.Contains("RESETS THE DIFFERENTIAL", output);   // the script's own header warning
        Assert.DoesNotContain("COPY_ONLY", output.Split("WITH")[1].Split(';')[0]);
    }

    [Fact]
    public async Task ALogBackupSaysBackupLogAndLandsAsTrn()
    {
        var (services, _, _, _) = Stage();

        var (exit, output, _) = await Run(
            ["--container", "backups", "--server", "SRV01", "--database", "Sales",
             "--type", "log"], services);

        Assert.Equal(0, exit);
        Assert.Contains("BACKUP LOG [Sales]", output);
        Assert.Contains(".trn", output);
    }

    /// <summary>A differential has no copy-only form - the flag is ignored, not emitted.</summary>
    [Fact]
    public async Task ADifferentialIsDifferentialAndNeverCopyOnly()
    {
        var (services, _, _, _) = Stage();

        var (_, output, _) = await Run(
            ["--container", "backups", "--server", "SRV01", "--database", "Sales",
             "--type", "diff"], services);

        Assert.Contains("DIFFERENTIAL", output);
        Assert.DoesNotContain("COPY_ONLY", output.Split("WITH")[1].Split(';')[0]);
    }

    [Fact]
    public async Task StripesOutsideTheDeviceRangeAreRefused()
    {
        var (services, _, _, _) = Stage();

        var (exit, _, errors) = await Run(
            ["--container", "backups", "--server", "SRV01", "--database", "Sales",
             "--stripes", "640"], services);

        Assert.Equal(64, exit);
        Assert.Contains("1 to 64", errors);
    }

    [Fact]
    public async Task AnExecutedBackupLeavesTheReceiptsTheOtherVerbsLeave()
    {
        var (services, sql, history, notifier) = Stage();

        var (exit, _, _) = await Run(
            ["--path", @"\\nas01\sql", "--server", "SRV01", "--database", "Sales",
             "--execute"], services);

        Assert.Equal(0, exit);
        Assert.Contains(sql.ExecutedScripts, s => s.Contains("BACKUP DATABASE [Sales]"));

        var receipt = Assert.Single(history.Entries);
        Assert.Equal("Backup", receipt.Kind);
        Assert.Equal(RestoreOutcome.Succeeded, receipt.Outcome);

        Assert.Equal(RunPhase.Started, notifier.Sent[0].Phase);
        Assert.Equal(RunPhase.Succeeded, notifier.Sent[^1].Phase);
        Assert.True(notifier.DrainCalls > 0);
    }

    [Fact]
    public async Task AFailedBackupClosesTheChannelAndTheReceiptSaysFailed()
    {
        var (services, sql, history, notifier) = Stage();
        sql.FailOnExecuteNumber = 1;

        var (exit, _, errors) = await Run(
            ["--path", @"\\nas01\sql", "--server", "SRV01", "--database", "Sales",
             "--execute"], services);

        Assert.Equal(2, exit);
        Assert.Contains("FAILED", errors);
        Assert.Equal(RestoreOutcome.Failed, Assert.Single(history.Entries).Outcome);
        Assert.Equal(RunPhase.Problem, notifier.Sent[^1].Phase);
        Assert.NotNull(notifier.Sent[^1].Duration);
    }

    [Fact]
    public async Task ExactlyOneDestinationIsRequired()
    {
        var (services, _, _, _) = Stage();

        var (both, _, _) = await Run(
            ["--container", "backups", "--path", @"\\nas01\sql",
             "--server", "SRV01", "--database", "Sales"], services);
        var (neither, _, _) = await Run(
            ["--server", "SRV01", "--database", "Sales"], services);

        Assert.Equal(64, both);
        Assert.Equal(64, neither);
    }
}
