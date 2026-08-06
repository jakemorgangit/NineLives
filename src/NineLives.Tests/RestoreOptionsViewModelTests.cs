using System.Reflection;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The RESTORE options, extracted from RestoreViewModel in #115 seam 7.
///
/// The point of the seam is that #110 cannot happen again. Four options - both WITH MOVE paths,
/// the STANDBY undo file and STATS - reached a release with no change handler at all, so editing
/// them moved the box on screen and left the generated script alone. People read that script before
/// running it against production.
///
/// Both halves of that bug were "someone has to remember": remember to write a change handler, and
/// remember to copy the field into the options object three hundred lines away. The subscription
/// side is structural now - one handler covers every option. The copy side is what
/// <see cref="EveryOptionOnTheViewModelReachesTheGeneratedOptions"/> pins.
/// </summary>
public class RestoreOptionsViewModelTests
{
    /// <summary>The options a user can set - the ones Build has to copy.</summary>
    private static List<PropertyInfo> Settable() =>
        typeof(RestoreOptionsViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p is { CanRead: true, CanWrite: true })
            .ToList();

    /// <summary>Something this property is definitely not already set to.</summary>
    private static object DistinctFrom(PropertyInfo property, object? current) => current switch
    {
        bool b => !b,
        int i => i + 7,
        string => $"set-{property.Name}",
        RecoveryMode m => m == RecoveryMode.Recovery ? RecoveryMode.NoRecovery : RecoveryMode.Recovery,
        _ => throw new NotSupportedException(
            $"{property.Name} is a {property.PropertyType.Name}, which this test does not know how " +
            "to vary. Add a case above so the option is still covered.")
    };

    /// <summary>
    /// Every option the user can set has to arrive in the object the script generator reads.
    ///
    /// This is the #110 guard, and it is deliberately written by reflection rather than as a list:
    /// a hand-written list of options is one more thing to remember to update, which is the failure
    /// it is guarding against. An option added to the ViewModel and not to Build fails here.
    /// </summary>
    [Fact]
    public void EveryOptionOnTheViewModelReachesTheGeneratedOptions()
    {
        var vm = new RestoreOptionsViewModel();
        var options = Settable();

        Assert.NotEmpty(options);

        // Move every option off its default, so a field Build never copies shows up as the
        // RestoreOptions default rather than what was set.
        foreach (var p in options)
            p.SetValue(vm, DistinctFrom(p, p.GetValue(vm)));

        var built = vm.Build("MyDb_Restored", null, []);

        foreach (var p in options)
        {
            var onOptions = typeof(RestoreOptions).GetProperty(p.Name);
            Assert.True(onOptions != null,
                $"{p.Name} is set on the options screen but has no matching field on " +
                "RestoreOptions, so nothing can carry it into the generated script.");

            Assert.True(
                Equals(p.GetValue(vm), onOptions.GetValue(built)),
                $"{p.Name} never reaches the generated script - RestoreOptionsViewModel.Build " +
                "does not copy it. This is #110: the box on screen moves and the script does not.");
        }
    }

    /// <summary>
    /// The three things Build takes as arguments are the ones that are NOT options - each is worked
    /// out somewhere that knows about the chain or the server.
    /// </summary>
    [Fact]
    public void TheTargetTheStopAtAndTheFileMovesComeFromTheCaller()
    {
        var moves = new List<FileMoveOption>
        {
            new() { LogicalName = "MyDb", PhysicalName = @"C:\Data\MyDb.mdf", NewPhysicalName = @"E:\Data\MyDb.mdf" }
        };
        var stopAt = new DateTime(2026, 1, 10, 22, 10, 30);

        var built = new RestoreOptionsViewModel().Build("MyDb_Restored", stopAt, moves);

        Assert.Equal("MyDb_Restored", built.TargetDatabaseName);
        Assert.Equal(stopAt, built.StopAt);
        Assert.Same(moves, built.FileMoves);
    }

    // ── STANDBY ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Blank produced <c>STANDBY = ''</c>, which SQL Server rejects - as the LAST statement of the
    /// chain, so it failed after SET SINGLE_USER and WITH REPLACE had already dropped and
    /// overwritten the target.
    /// </summary>
    [Fact]
    public void StandbyWithNoUndoFileIsNotUsable()
    {
        var vm = new RestoreOptionsViewModel { RecoveryMode = RecoveryMode.Standby };

        Assert.True(vm.IsStandbyMode);
        Assert.False(vm.HasStandbyFileIfNeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WhitespaceIsNotAnUndoFile(string path)
    {
        var vm = new RestoreOptionsViewModel
        {
            RecoveryMode = RecoveryMode.Standby,
            StandbyFilePath = path
        };

        Assert.False(vm.HasStandbyFileIfNeeded);
        Assert.Null(vm.Build("MyDb", null, []).StandbyFilePath);
    }

    [Fact]
    public void StandbyWithAnUndoFileIsUsable()
    {
        var vm = new RestoreOptionsViewModel
        {
            RecoveryMode = RecoveryMode.Standby,
            StandbyFilePath = @"E:\Standby\MyDb.undo"
        };

        Assert.True(vm.HasStandbyFileIfNeeded);
        Assert.Equal(@"E:\Standby\MyDb.undo", vm.Build("MyDb", null, []).StandbyFilePath);
    }

    /// <summary>The undo file only applies to STANDBY, so the other modes are never blocked by it.</summary>
    [Theory]
    [InlineData(RecoveryMode.Recovery)]
    [InlineData(RecoveryMode.NoRecovery)]
    public void TheOtherRecoveryModesNeedNoUndoFile(RecoveryMode mode)
    {
        var vm = new RestoreOptionsViewModel { RecoveryMode = mode };

        Assert.False(vm.IsStandbyMode);
        Assert.True(vm.HasStandbyFileIfNeeded);
    }

    /// <summary>
    /// Both are computed, so the view needs telling when the things they are computed from move -
    /// otherwise the undo-file box does not appear when STANDBY is picked.
    /// </summary>
    [Fact]
    public void TheComputedStandbyStateIsAnnouncedWhenItChanges()
    {
        var vm = new RestoreOptionsViewModel();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.RecoveryMode = RecoveryMode.Standby;

        Assert.Contains(nameof(RestoreOptionsViewModel.IsStandbyMode), raised);
        Assert.Contains(nameof(RestoreOptionsViewModel.HasStandbyFileIfNeeded), raised);

        raised.Clear();
        vm.StandbyFilePath = @"E:\Standby\MyDb.undo";

        Assert.Contains(nameof(RestoreOptionsViewModel.HasStandbyFileIfNeeded), raised);
    }

    /// <summary>
    /// One subscription keeps the script in step with every option, so each one has to announce
    /// itself. An [ObservableProperty] does this for free - this is here so that an option added
    /// later as a plain property, which would not, is caught.
    /// </summary>
    [Fact]
    public void EveryOptionAnnouncesItsOwnChange()
    {
        foreach (var p in Settable())
        {
            var vm = new RestoreOptionsViewModel();
            var raised = new List<string?>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            p.SetValue(vm, DistinctFrom(p, p.GetValue(vm)));

            Assert.True(raised.Contains(p.Name),
                $"Changing {p.Name} raised no PropertyChanged for it, so the generated script " +
                "would not follow it.");
        }
    }
}
