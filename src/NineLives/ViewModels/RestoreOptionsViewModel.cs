using CommunityToolkit.Mvvm.ComponentModel;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.ViewModels;

/// <summary>
/// The RESTORE options a user can set, and the one place they are turned into a
/// <see cref="RestoreOptions"/>.
///
/// Seventh seam out of RestoreViewModel (#115), and the one that pays for itself. Each option used
/// to be a property on a 2,500-line class with its own one-line change handler, and was copied by
/// hand into the options object in a method three hundred lines away. Forgetting either half is
/// invisible: the box on screen moves, and the script does not follow.
///
/// That is #110 - WITH MOVE's two paths, the STANDBY undo file and STATS all reached a release with
/// no change handler at all, so editing them changed the display and nothing else. People read the
/// generated T-SQL before running it against production, so a script that quietly disagrees with
/// the boxes above it is worse than no script: it looks authoritative and is not.
///
/// Both halves are structural now. One <see cref="ObservableObject.PropertyChanged"/> subscription
/// covers every option, whatever gets added later, and <see cref="Build"/> sits next to the fields
/// it copies rather than in another file.
/// </summary>
public partial class RestoreOptionsViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _withReplace = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStandbyMode))]
    [NotifyPropertyChangedFor(nameof(HasStandbyFileIfNeeded))]
    private RecoveryMode _recoveryMode = RecoveryMode.Recovery;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStandbyFileIfNeeded))]
    private string _standbyFilePath = string.Empty;

    [ObservableProperty]
    private bool _disconnectSessions = true;

    [ObservableProperty]
    private int _statsPercent = 10;

    [ObservableProperty]
    private bool _keepReplication;

    [ObservableProperty]
    private bool _enableBroker;

    [ObservableProperty]
    private bool _newBroker;

    /// <summary>
    /// Verify backup checksums as each page is read. Only meaningful if the backup was taken
    /// WITH CHECKSUM - against one that was not, SQL Server fails the restore rather than skipping
    /// the check, which is why this is off by default.
    /// </summary>
    [ObservableProperty]
    private bool _withChecksum;

    /// <summary>
    /// Carry on restoring after a checksum or page error instead of stopping. This produces a
    /// database SQL Server has already said is damaged; it exists for the case where a partial
    /// recovery beats none.
    /// </summary>
    [ObservableProperty]
    private bool _continueAfterError;

    /// <summary>Whether the undo-file box applies, for the view.</summary>
    public bool IsStandbyMode => RecoveryMode == RecoveryMode.Standby;

    /// <summary>
    /// STANDBY needs somewhere to put its undo file. Blank produced <c>STANDBY = ''</c>, which SQL
    /// Server rejects - as the LAST statement of the chain, so it failed after SET SINGLE_USER and
    /// WITH REPLACE had already dropped and overwritten the target, leaving it in RESTORING and
    /// single-user with nothing to show for it.
    /// </summary>
    public bool HasStandbyFileIfNeeded =>
        RecoveryMode != RecoveryMode.Standby || !string.IsNullOrWhiteSpace(StandbyFilePath);

    /// <summary>
    /// Everything on this type, copied into the object the script generator reads.
    ///
    /// The three arguments are the things that are NOT options: which database is being restored,
    /// where the point-in-time target ended up, and where the files are being moved to - each of
    /// which is worked out somewhere that knows about the chain or the server.
    ///
    /// Adding an option means adding a field above and a line here, and nothing else. When the copy
    /// lived in another file it was possible to do one and not the other, which is what #110 was.
    /// </summary>
    public RestoreOptions Build(
        string targetDatabaseName, DateTime? stopAt, List<FileMoveOption> fileMoves) => new()
    {
        TargetDatabaseName = targetDatabaseName,
        WithReplace = WithReplace,
        RecoveryMode = RecoveryMode,
        StandbyFilePath = string.IsNullOrWhiteSpace(StandbyFilePath) ? null : StandbyFilePath,
        DisconnectSessions = DisconnectSessions,
        StatsPercent = StatsPercent,
        StopAt = stopAt,
        KeepReplication = KeepReplication,
        EnableBroker = EnableBroker,
        NewBroker = NewBroker,
        WithChecksum = WithChecksum,
        ContinueAfterError = ContinueAfterError,
        FileMoves = fileMoves
    };
}
