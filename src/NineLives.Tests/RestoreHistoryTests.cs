using System.IO;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The record of what was restored, kept so it survives the app closing (#31).
///
/// Two properties matter more than the rest: it must never lose entries it already holds, and
/// recording must never be able to fail the restore it is recording.
/// </summary>
public class RestoreHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ninelives-history-tests", Guid.NewGuid().ToString("n"));

    private RestoreHistoryStore Store() => new(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static RestoreHistoryEntry Entry(string target = "MyDb", RestoreOutcome outcome = RestoreOutcome.Succeeded)
        => new()
        {
            StartedAt = new DateTime(2026, 1, 10, 22, 0, 0),
            CompletedAt = new DateTime(2026, 1, 10, 22, 4, 30),
            ServerName = "SRV01",
            TargetDatabase = target,
            ChainSummary = "1 Full + 2 Log(s)",
            Outcome = outcome,
            Script = "RESTORE DATABASE [MyDb] FROM URL = N'https://mystorageaccount.blob.core.windows.net/backups/x.bak'",
            Log = "Beginning restore execution..."
        };

    [Fact]
    public void NoHistoryYetReadsAsEmptyRatherThanFailing()
    {
        Assert.Empty(Store().Load());
    }

    [Fact]
    public void AnEntrySurvivesBeingWrittenAndReadBack()
    {
        Store().Append(Entry());

        var loaded = Assert.Single(Store().Load());
        Assert.Equal("MyDb", loaded.TargetDatabase);
        Assert.Equal("SRV01", loaded.ServerName);
        Assert.Equal(RestoreOutcome.Succeeded, loaded.Outcome);
        Assert.Contains("RESTORE DATABASE", loaded.Script);
    }

    [Fact]
    public void TheNewestIsFirst()
    {
        var store = Store();
        store.Append(Entry("First"));
        store.Append(Entry("Second"));
        store.Append(Entry("Third"));

        var loaded = store.Load();
        Assert.Equal(["Third", "Second", "First"], loaded.Select(e => e.TargetDatabase));
    }

    [Fact]
    public void AnUnreadableHistoryIsLeftAloneRatherThanOverwritten()
    {
        // The config-loss shape (#7): read fails, the caller carries on with an empty list, and the
        // next save writes that over everything. Here the file is left exactly as it was.
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "restore-history.json");
        File.WriteAllText(path, "{ this is not the history file }");

        var store = Store();
        Assert.Empty(store.Load());

        store.Append(Entry());

        Assert.Equal("{ this is not the history file }", File.ReadAllText(path));
    }

    [Fact]
    public void AFailureToRecordCannotFailTheRestore()
    {
        // A directory where the file should be: every write fails. Append must still return.
        Directory.CreateDirectory(Path.Combine(_dir, "restore-history.json"));

        var ex = Record.Exception(() => Store().Append(Entry()));

        Assert.Null(ex);
    }

    [Fact]
    public void SecretsAreStrippedOnTheWayIn()
    {
        // Redaction happens at the writing boundary, so a future caller cannot leak a token by
        // forgetting to redact - the same rule OperationLog follows.
        var entry = Entry();
        entry.Log = "Using URL https://mystorageaccount.blob.core.windows.net/backups/x.bak?sv=2024-01-01&sig=SECRETVALUE";

        Store().Append(entry);

        var loaded = Assert.Single(Store().Load());
        Assert.DoesNotContain("SECRETVALUE", loaded.Log);
    }

    [Fact]
    public void ClearingRemovesEverything()
    {
        var store = Store();
        store.Append(Entry());
        store.Clear();

        Assert.Empty(store.Load());
    }

    [Fact]
    public void OldEntriesFallOffTheEnd()
    {
        var store = Store();
        for (int i = 0; i < 205; i++) store.Append(Entry($"Db{i}"));

        var loaded = store.Load();
        Assert.Equal(200, loaded.Count);

        // The ones kept are the recent ones.
        Assert.Equal("Db204", loaded[0].TargetDatabase);
        Assert.DoesNotContain(loaded, e => e.TargetDatabase == "Db0");
    }

    // ── display ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "<1s")]
    [InlineData(45, "45s")]
    [InlineData(90, "1m 30s")]
    [InlineData(3700, "1h 1m")]
    public void DurationReadsAtHumanScale(int seconds, string expected)
    {
        var entry = Entry();
        entry.CompletedAt = entry.StartedAt.AddSeconds(seconds);

        Assert.Equal(expected, entry.DurationDisplay);
    }

    [Fact]
    public void ARecordFormatsAsASelfContainedDocument()
    {
        var text = HistoryViewModel.Format(Entry(outcome: RestoreOutcome.Failed));

        // Everything needed to read it a week later without the app open.
        Assert.Contains("SRV01", text);
        Assert.Contains("MyDb", text);
        Assert.Contains("Failed", text);
        Assert.Contains("1 Full + 2 Log(s)", text);
        Assert.Contains("RESTORE DATABASE", text);
        Assert.Contains("Beginning restore execution", text);
    }

    // ── the view model over it ──────────────────────────────────────────────────

    [Fact]
    public void TheViewSelectsTheMostRecentSoItIsWhatYouJustRan()
    {
        var store = Store();
        store.Append(Entry("Older"));
        store.Append(Entry("Newer"));

        var vm = new HistoryViewModel(store);

        Assert.True(vm.HasEntries);
        Assert.Equal("Newer", vm.SelectedEntry!.TargetDatabase);
    }

    [Fact]
    public void FilteringMatchesDatabaseServerAndOutcome()
    {
        var store = Store();
        store.Append(Entry("Sales"));
        store.Append(Entry("Payroll", RestoreOutcome.Failed));

        var vm = new HistoryViewModel(store);

        vm.FilterText = "payroll";
        Assert.Equal("Payroll", Assert.Single(vm.Entries).TargetDatabase);

        vm.FilterText = "failed";
        Assert.Equal("Payroll", Assert.Single(vm.Entries).TargetDatabase);

        vm.FilterText = "SRV01";
        Assert.Equal(2, vm.Entries.Count);

        vm.FilterText = "";
        Assert.Equal(2, vm.Entries.Count);
    }

    [Fact]
    public void ClearingTakesTwoPresses()
    {
        var store = Store();
        store.Append(Entry());
        var vm = new HistoryViewModel(store);

        vm.ClearHistoryCommand.Execute(null);
        Assert.True(vm.IsClearArmed);
        Assert.True(vm.HasEntries);            // still there after one press

        vm.ClearHistoryCommand.Execute(null);
        Assert.False(vm.HasEntries);
        Assert.Empty(store.Load());
    }

    [Fact]
    public void BackingOutOfAClearLeavesTheHistoryAlone()
    {
        var store = Store();
        store.Append(Entry());
        var vm = new HistoryViewModel(store);

        vm.ClearHistoryCommand.Execute(null);
        vm.CancelClearCommand.Execute(null);

        Assert.False(vm.IsClearArmed);
        Assert.Single(store.Load());
    }
}
