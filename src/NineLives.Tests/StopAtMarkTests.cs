using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// STOPATMARK/STOPBEFOREMARK (#243): restore to a NAMED transaction, planted before risky work,
/// instead of a clock time reconstructed afterwards from chat messages and guilt. A mark and a
/// time are the same mechanism aimed differently, so the mark rides in exactly STOPAT's position
/// and inherits its repeat-on-every-log rule.
/// </summary>
public class StopAtMarkTests
{
    private static readonly DateTime T0 = new(2026, 8, 10, 9, 0, 0);

    private static BackupChain Chain(int logs = 2)
    {
        var chain = new BackupChain
        {
            FullSet = new BackupSet
            {
                SetId = "full",
                DatabaseName = "MyDb",
                Type = BackupType.Full,
                Timestamp = T0,
                Files = [new BackupFileInfo { BlobName = "f.bak", LocalPath = @"D:\f.bak" }]
            }
        };

        for (var i = 0; i < logs; i++)
        {
            chain.LogSets.Add(new BackupSet
            {
                SetId = $"log{i}",
                DatabaseName = "MyDb",
                Type = BackupType.TransactionLog,
                Timestamp = T0.AddMinutes(30 * (i + 1)),
                Files = [new BackupFileInfo { BlobName = $"l{i}.trn", LocalPath = $@"D:\l{i}.trn" }]
            });
        }

        return chain;
    }

    private static string Generate(string? mark, bool before = true, DateTime? stopAt = null) =>
        new RestoreScriptGenerator().Generate(Chain(), new RestoreOptions
        {
            TargetDatabaseName = "MyDb",
            StopAtMark = mark,
            StopBeforeMark = before,
            StopAt = stopAt
        });

    /// <summary>STOPAT's own rule, inherited: the mark goes on EVERY log restore in the chain.</summary>
    [Fact]
    public void TheMarkRidesOnEveryLogRestore()
    {
        var script = Generate("deploy_v2");

        var count = script.Split("STOPBEFOREMARK = N'deploy_v2'").Length - 1;
        Assert.Equal(2, count);
        Assert.DoesNotContain("STOPAT =", script);
    }

    [Fact]
    public void AtInsteadOfBeforeWhenAskedFor()
    {
        var script = Generate("deploy_v2", before: false);

        Assert.Contains("STOPATMARK = N'deploy_v2'", script);
        Assert.DoesNotContain("STOPBEFOREMARK", script);
    }

    /// <summary>The mark wins over a stray clock time - it is the more deliberate of the two.</summary>
    [Fact]
    public void TheMarkWinsWhenBothAreSomehowSet()
    {
        var script = Generate("deploy_v2", stopAt: T0.AddMinutes(45));

        Assert.Contains("STOPBEFOREMARK", script);
        Assert.DoesNotContain("STOPAT =", script);
    }

    [Fact]
    public void AnApostropheInTheMarkNameCannotBreakOut()
    {
        var script = Generate("o'brien_deploy");

        Assert.Contains("N'o''brien_deploy'", script);
    }

    [Fact]
    public void NoMarkMeansTheScriptIsUntouched()
    {
        var script = Generate(null);

        Assert.DoesNotContain("MARK", script.Replace("-- ", ""));
    }

    /// <summary>The display line carries what the picker needs: name, when, and the description.</summary>
    [Fact]
    public void AMarkDisplaysItsNameTimeAndDescription()
    {
        var mark = new LogMark("deploy_v2", "the v2 schema deployment", T0);

        Assert.Contains("deploy_v2", mark.Display);
        Assert.Contains("2026-08-10 09:00:00", mark.Display);
        Assert.Contains("the v2 schema deployment", mark.Display);
    }

    // ── which catalogue gets asked (#268) ───────────────────────────────────────
    //
    // logmarkhistory is written where the marked transactions RAN. Ask the wrong instance and
    // the answer is "no marks" - indistinguishable from there genuinely being none.

    private static (RestoreViewModel vm, FakeSqlServerService sql,
        ServerConnection source, ServerConnection target) TwoServers()
    {
        var store = new FakeCredentialStore();
        var source = new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" };
        var target = new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV02", ServerName = "SRV02" };
        store.Config.Servers.Add(source);
        store.Config.Servers.Add(target);

        var sql = new FakeSqlServerService();
        sql.LogMarksByDatabase["MyDb"] = [new LogMark("deploy_v2", null, T0)];

        var vm = new RestoreViewModel(
            new FakeBlobStorageService(), sql, new BackupChainBuilder(),
            new RestoreScriptGenerator(), store,
            log: null, history: new FakeOperationHistoryStore(), auditStore: TestAuditStores.Temp());
        vm.RefreshContainers();

        return (vm, sql, source, target);
    }

    /// <summary>
    /// Restoring SRV01's backups onto SRV02: the marks live in SRV01's msdb, and SRV02's has
    /// never heard of them. The connected (target) server must NOT be the one asked.
    /// </summary>
    [Fact]
    public async Task SharedPathMarksComeFromTheSourceServersMsdb()
    {
        var (vm, sql, source, target) = TwoServers();
        vm.SelectedMedium = BackupMedium.SharedPath;
        vm.SourceServer = vm.SourceServers.Single(s => s.Id == source.Id);
        vm.ConnectedServer = target;
        vm.Inventory.SelectedDatabaseName = "MyDb";

        await vm.LoadLogMarksCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "SRV01" }, sql.LogMarkServersAsked);
        Assert.Single(vm.LogMarks);
    }

    /// <summary>
    /// The shared-path flow chooses its source in step 1 and never needed the Servers screen's
    /// connect - finding marks must not demand one.
    /// </summary>
    [Fact]
    public async Task ASharedPathFindsMarksWithoutAServersScreenConnection()
    {
        var (vm, sql, source, _) = TwoServers();
        vm.SelectedMedium = BackupMedium.SharedPath;
        vm.SourceServer = vm.SourceServers.Single(s => s.Id == source.Id);
        vm.Inventory.SelectedDatabaseName = "MyDb";

        await vm.LoadLogMarksCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "SRV01" }, sql.LogMarkServersAsked);
        Assert.Single(vm.LogMarks);
    }

    /// <summary>Blob has no source instance - the connected server is the only catalogue there is.</summary>
    [Fact]
    public async Task BlobMarksComeFromTheConnectedServer()
    {
        var (vm, sql, _, target) = TwoServers();
        vm.ConnectedServer = target;
        vm.Inventory.SelectedDatabaseName = "MyDb";

        await vm.LoadLogMarksCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "SRV02" }, sql.LogMarkServersAsked);
        Assert.Single(vm.LogMarks);
    }
}
