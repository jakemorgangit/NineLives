using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Status is transient, an error is sticky (#117 item 6).
///
/// The two used to share one line and one clearing rule, so the next thing that happened painted
/// over the last thing that went wrong. That was noticed once - "no valid restore points"
/// disappearing under "Loaded 1 files" - and fixed at that one call site, which left every other
/// pairing of a failure and a following success with the same hole.
/// </summary>
public class StatusAndErrorTests
{
    /// <summary>The protected surface, reachable. Every ViewModel inherits exactly this.</summary>
    private sealed class TestViewModel : ViewModelBase
    {
        public void Status(string message) => SetStatus(message);
        public void Error(string message) => SetError(message);
        public void Clear() => ClearStatus();
    }

    [Fact]
    public void AStatusMessageDoesNotEraseAnError()
    {
        var vm = new TestViewModel();

        vm.Error("No valid restore points for that database.");
        vm.Status("Loaded 1 files.");

        Assert.True(vm.HasError);
        Assert.Contains("No valid restore points", vm.ErrorMessage);
        Assert.Equal("Loaded 1 files.", vm.StatusMessage);
    }

    [Fact]
    public void ANewErrorSupersedesTheOldOne()
    {
        var vm = new TestViewModel();

        vm.Error("First failure.");
        vm.Error("Second failure.");

        Assert.True(vm.HasError);
        Assert.Equal("Second failure.", vm.ErrorMessage);
    }

    /// <summary>
    /// The escape hatch for the case the persistence rule would otherwise get wrong: a failure
    /// that gets fixed and retried successfully. Every long-running command calls this before it
    /// starts, so the stale error goes with it.
    /// </summary>
    [Fact]
    public void StartingTheNextOperationClearsBoth()
    {
        var vm = new TestViewModel();

        vm.Error("Login failed for user.");
        vm.Clear();

        Assert.False(vm.HasError);
        Assert.Equal(string.Empty, vm.ErrorMessage);
        Assert.Equal(string.Empty, vm.StatusMessage);
    }

    [Fact]
    public void DismissingAnErrorLeavesTheStatusLineAlone()
    {
        var vm = new TestViewModel();

        vm.Error("Could not check credential.");
        vm.Status("Connected to SRV01.");
        vm.DismissErrorCommand.Execute(null);

        Assert.False(vm.HasError);
        Assert.Equal(string.Empty, vm.ErrorMessage);
        Assert.Equal("Connected to SRV01.", vm.StatusMessage);
    }

    /// <summary>
    /// Not every screen surfaces both. The History screen binds only the status line, so an error
    /// that wrote only to the error surface would be invisible there.
    /// </summary>
    [Fact]
    public void AnErrorAlsoReachesTheStatusLineForScreensThatOnlyShowThat()
    {
        var vm = new TestViewModel();

        vm.Error("Could not read the saved log.");

        Assert.Equal("Could not read the saved log.", vm.StatusMessage);
    }
}
