using System.IO;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Things that destroyed something without saying so (#370).
///
/// The pattern in each: an action whose cost is invisible at the moment it is taken. Shortening
/// log retention deletes files the same screen calls evidence for a change ticket. An unreadable
/// history renders as an empty one, and Clear sits beside it. Neither asked, and neither could
/// be undone.
/// </summary>
public class QuietDestructionTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ninelives-quiet-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── the count that makes the question worth asking ──────────────────────────

    /// <summary>
    /// Counting is not deleting. The confirmation needs a number before it can be honest about
    /// what is at stake, and asking that question must not itself destroy anything.
    /// </summary>
    [Fact]
    public void CountingWhatARetentionWouldDeleteDeletesNothing()
    {
        Directory.CreateDirectory(_dir);

        var old = Path.Combine(_dir, "ninelives-20260101.log");
        var fresh = Path.Combine(_dir, "ninelives-20260810.log");
        File.WriteAllText(old, "old");
        File.WriteAllText(fresh, "fresh");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-40));
        File.SetLastWriteTime(fresh, DateTime.Now.AddDays(-1));

        var log = new OperationLog(_dir);

        Assert.Equal(1, log.CountPrunable(30));
        Assert.Equal(0, log.CountPrunable(90));

        // Both files still there - the question was asked, not acted on.
        Assert.True(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    /// <summary>A retention that loses nothing should not interrupt anybody.</summary>
    [Fact]
    public void ARetentionThatDestroysNothingCountsZero()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "ninelives-20260810.log"), "fresh");

        Assert.Equal(0, new OperationLog(_dir).CountPrunable(30));
    }

    // ── an unreadable history is not an empty one ───────────────────────────────

    [Fact]
    public void AnUnreadableHistorySaysSoRatherThanReportingNothingRecorded()
    {
        var store = new FakeRestoreHistoryStore { CouldNotRead = true };
        var vm = new HistoryViewModel(store);

        vm.Refresh();

        Assert.True(vm.HasError);
        Assert.Contains("could not be read", vm.ErrorMessage);
        Assert.DoesNotContain("No restores recorded yet", vm.StatusMessage);
    }

    /// <summary>
    /// The one that matters: Clear would write an empty file over one that may still hold every
    /// receipt, and the screen was telling the user there was nothing in it.
    /// </summary>
    [Fact]
    public void ClearRefusesOverAHistoryItCouldNotRead()
    {
        var store = new FakeRestoreHistoryStore { CouldNotRead = true };
        store.Entries.Add(new Models.RestoreHistoryEntry { TargetDatabase = "Sales" });

        var vm = new HistoryViewModel(store);

        vm.ClearHistoryCommand.Execute(null);
        vm.ClearHistoryCommand.Execute(null);

        Assert.False(vm.IsClearArmed);
        Assert.NotEmpty(store.Entries);
        Assert.Contains("could not be read", vm.ErrorMessage);
    }

    /// <summary>A readable history still clears, on the second press, exactly as before.</summary>
    [Fact]
    public void AReadableHistoryStillClearsOnTheSecondPress()
    {
        var store = new FakeRestoreHistoryStore();
        store.Entries.Add(new Models.RestoreHistoryEntry { TargetDatabase = "Sales" });

        var vm = new HistoryViewModel(store);

        vm.ClearHistoryCommand.Execute(null);
        Assert.True(vm.IsClearArmed);
        Assert.NotEmpty(store.Entries);

        vm.ClearHistoryCommand.Execute(null);
        Assert.Empty(store.Entries);
    }
}
