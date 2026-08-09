using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The operation log, readable inside the app that wrote it (#214). A reader, not a log
/// framework: today's file, a filter, a refresh - the shape of the actual support conversation,
/// which is "what does the log say about X?".
/// </summary>
public class LogViewerTests
{
    private static (LogViewerViewModel vm, OperationLog log) New()
    {
        var log = TestLogs.Temp();
        log.Info("connected to SRV01");
        log.Warn("[space] restore may not fit");
        log.Info("restore finished");

        return (new LogViewerViewModel(log), log);
    }

    [Fact]
    public void TodayFileIsShownWhole()
    {
        var (vm, log) = New();

        Assert.Equal(log.CurrentFile, vm.CurrentFile);
        Assert.Contains("connected to SRV01", vm.VisibleText);
        Assert.Contains("restore finished", vm.VisibleText);
        Assert.Contains("3 lines", vm.LineSummary);
    }

    [Fact]
    public void TheFilterNarrowsAndCountsHonestly()
    {
        var (vm, _) = New();

        vm.FilterText = "space";

        Assert.Contains("may not fit", vm.VisibleText);
        Assert.DoesNotContain("connected", vm.VisibleText);
        Assert.Contains("1 of 3 lines match", vm.LineSummary);
    }

    [Fact]
    public void TheFilterIsCaseInsensitive()
    {
        var (vm, _) = New();

        vm.FilterText = "SRV01";
        var upper = vm.VisibleText;
        vm.FilterText = "srv01";

        Assert.Equal(upper, vm.VisibleText);
    }

    /// <summary>New lines written after opening appear on refresh - the file is read shared.</summary>
    [Fact]
    public void RefreshPicksUpWhatWasWrittenSinceOpening()
    {
        var (vm, log) = New();

        log.Info("a later line");
        Assert.DoesNotContain("a later line", vm.VisibleText);

        vm.RefreshCommand.Execute(null);

        Assert.Contains("a later line", vm.VisibleText);
    }

    [Fact]
    public void AnEmptyDaySaysSoInsteadOfShowingNothing()
    {
        var vm = new LogViewerViewModel(TestLogs.Temp());

        Assert.Contains("Nothing logged today", vm.VisibleText);
    }

    [Fact]
    public void ClearingTheFilterBringsEverythingBack()
    {
        var (vm, _) = New();
        vm.FilterText = "space";
        vm.FilterText = "";

        Assert.Contains("connected to SRV01", vm.VisibleText);
        Assert.Contains("3 lines", vm.LineSummary);
    }
}
