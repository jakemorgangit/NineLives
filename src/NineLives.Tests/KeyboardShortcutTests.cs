using System.Globalization;
using System.Windows.Data;
using Blackcat.NineLives.Converters;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The keyboard shortcuts (#117 item 5).
///
/// The key gestures themselves live in MainWindow's InputBindings and cannot be pressed from a
/// test without a shown window. What can be tested is the part that would actually go wrong: what
/// each one decides to do, given that a window-level key reaches every screen.
/// </summary>
// In the WPF collection for the Application these ViewModels expect to exist, but nothing here
// needs to run on the dispatcher thread, so the fixture itself is never touched.
[Collection(WpfCollection.Name)]
public class KeyboardShortcutTests
{
    private static MainViewModel New() => new(new FakeCredentialStore());

    [Fact]
    public void TheSidebarStartsOnTheScreenTheAppOpens()
    {
        // The sidebar highlight is bound to this now, rather than a hardcoded IsChecked on the
        // first button - so if it did not start on a real view name, nothing would look selected.
        var vm = New();

        Assert.Equal(MainViewModel.Nav.BlobStorage, vm.CurrentViewName);
        Assert.Contains(vm.CurrentViewName, MainViewModel.Nav.Views);
    }

    /// <summary>
    /// F5 on a screen that lists nothing must do nothing. Bound straight to the Restore screen's
    /// loader it would have run a blob listing while somebody was reading About.
    /// </summary>
    [Fact]
    public void ReloadDoesNothingOnAScreenWithNothingToReload()
    {
        var vm = New();
        vm.NavigateToCommand.Execute(MainViewModel.Nav.About);

        vm.ReloadCommand.Execute(null);

        Assert.False(vm.Restore.IsBusy);
        Assert.False(vm.Restore.Inventory.BackupsLoaded);
    }

    [Fact]
    public void GenerateIsIgnoredAwayFromTheRestoreScreen()
    {
        var vm = New();
        vm.NavigateToCommand.Execute(MainViewModel.Nav.History);

        vm.GenerateScriptCommand.Execute(null);

        Assert.False(vm.Restore.HasScript);
    }

    /// <summary>
    /// Esc with nothing running is a no-op rather than an exception. It is not gated on the
    /// current screen - a restore keeps running while somebody navigates away, and a stop key that
    /// only works on one page is not a stop key.
    /// </summary>
    [Fact]
    public void EscapeWithNothingRunningIsHarmless()
    {
        var vm = New();
        vm.NavigateToCommand.Execute(MainViewModel.Nav.About);

        vm.CancelCurrentCommand.Execute(null);
        vm.CancelCurrentCommand.Execute(null);
    }

    /// <summary>
    /// The sidebar binds IsChecked through EnumToBoolConverter against a STRING view name, not an
    /// enum. ConvertBack has to refuse in that case: a RadioButton sets IsChecked locally when it
    /// is clicked, and anything other than Binding.DoNothing there would write back through the
    /// binding and leave the selection out of step with the screen actually showing.
    /// </summary>
    [Fact]
    public void ClickingASidebarButtonDoesNotWriteBackThroughTheBinding()
    {
        var converter = new EnumToBoolConverter();

        Assert.Equal(
            Binding.DoNothing,
            converter.ConvertBack(true, typeof(string), "Restore", CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("Restore", "Restore", true)]
    [InlineData("Restore", "History", false)]
    public void TheSidebarHighlightFollowsTheCurrentView(string current, string button, bool expected)
    {
        var converter = new EnumToBoolConverter();

        Assert.Equal(
            expected,
            converter.Convert(current, typeof(bool), button, CultureInfo.InvariantCulture));
    }
}
