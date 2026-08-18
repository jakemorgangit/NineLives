using Blackcat.NineLives.Models;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// "Check its history" becomes live when a source instance is chosen (#483).
///
/// Reported from a real run: the button sat disabled next to a populated dropdown, and the only
/// way to wake it was to navigate off the Restore screen and back - which rebuilds the visual tree
/// and re-reads every binding from scratch.
///
/// The button binds Command AND IsEnabled, and an explicit IsEnabled wins over the command's own
/// answer. So the panel's NotifyCanExecuteChanged updated something nothing was looking at, while
/// CanCheck - what the button actually reads - was never raised anywhere in the file.
///
/// This is #130 again, and the same subtlety applies: asking CanCheck in a test always gives the
/// right answer, because it is computed on the spot. A CONTROL does not ask. It caches what it was
/// last told and re-reads only when PropertyChanged names the property - so what was broken was
/// the notification, not the answer, and only a test watching PropertyChanged can see it.
///
/// A disabled control is a silent failure: it looks deliberately unavailable, so it reads as "not
/// allowed yet" rather than "broken", and there is nothing to click to find out otherwise.
/// </summary>
public class GapCheckButtonComesAliveTests
{
    private static (BackupGapViewModel vm, List<string> told) Watched()
    {
        var vm = new BackupGapViewModel(new FakeSqlServerService());
        vm.Servers.Add(new ServerConnection { Id = "s1", Name = "SRV01", ServerName = "SRV01" });
        vm.Servers.Add(new ServerConnection { Id = "s2", Name = "SRV02", ServerName = "SRV02" });

        var told = new List<string>();
        vm.PropertyChanged += (_, e) => told.Add(e.PropertyName ?? string.Empty);
        return (vm, told);
    }

    /// <summary>The reported sequence: pick an instance, watch the button stay dead.</summary>
    [Fact]
    public void ChoosingASourceInstanceTellsTheButtonToReadAgain()
    {
        var (vm, told) = Watched();

        vm.SourceServer = vm.Servers[0];

        Assert.True(vm.CanCheck);

        // The part that was missing. Without it the button keeps the answer it was given while
        // the dropdown was still empty, and stays disabled beside a chosen instance.
        Assert.Contains(nameof(vm.CanCheck), told);
    }

    /// <summary>
    /// And when the instance is taken away again, so the button does not stay live against a
    /// panel that has nothing to ask.
    /// </summary>
    [Fact]
    public void ClearingTheSourceInstanceSaysSoToo()
    {
        var (vm, told) = Watched();
        vm.SourceServer = vm.Servers[0];
        told.Clear();

        vm.SourceServer = null;

        Assert.False(vm.CanCheck);
        Assert.Contains(nameof(vm.CanCheck), told);
    }

    /// <summary>
    /// The check itself moves the button both ways. It reads the instance's whole backup history,
    /// which is a round trip somebody can sit through - a button that stays live through it
    /// invites a second press against a check already running.
    /// </summary>
    [Fact]
    public void RunningTheCheckGreysTheButtonAndBringsItBack()
    {
        var (vm, told) = Watched();
        vm.SourceServer = vm.Servers[0];
        told.Clear();

        vm.IsChecking = true;
        Assert.False(vm.CanCheck);
        Assert.Contains(nameof(vm.CanCheck), told);

        told.Clear();
        vm.IsChecking = false;
        Assert.True(vm.CanCheck);
        Assert.Contains(nameof(vm.CanCheck), told);
    }

    /// <summary>
    /// Switching instance keeps saying it, because the answer stays true and a control that was
    /// told "false" by an earlier state has to hear otherwise.
    /// </summary>
    [Fact]
    public void SwitchingBetweenInstancesKeepsTheButtonLive()
    {
        var (vm, told) = Watched();
        vm.SourceServer = vm.Servers[0];
        told.Clear();

        vm.SourceServer = vm.Servers[1];

        Assert.True(vm.CanCheck);
        Assert.Contains(nameof(vm.CanCheck), told);
    }
}
