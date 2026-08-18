using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// "This container is N behind" is measured from times that are actually backup times (#489).
///
/// The same fault as #487, in the number printed above the list rather than in the list itself, and
/// with a worse failure mode: #487 could name the wrong backup, this can suppress the warning
/// altogether.
///
/// RecoveryTimeNotInContainer takes the newest held set's Timestamp and subtracts it from the
/// newest backup msdb recorded. It never asked where that timestamp came from. When it came from
/// the blob rather than the filename it is two things it should not be:
///
///   The wrong EVENT - when the upload finished, not when the backup started. Always later, so it
///   always makes the container look more current than it is.
///
///   Possibly the wrong CLOCK - a raw BlobLastModified reading is UTC, while msdb's dates are the
///   instance's local time. BackupSet's own comment says this "can sort it hours out of position".
///
/// Both shrink the measured gap, and the method returns null when the difference is not positive.
/// So a single blob-stamped set whose reading lands after the newest recorded backup takes the
/// maximum, makes the subtraction negative, and the banner does not appear at all - on a container
/// that is genuinely hours behind.
///
/// Which way the clock error runs depends on where the server is. West of UTC the blob reading is
/// AHEAD of local time, which is the direction that suppresses the warning, and by four or five
/// hours rather than by minutes.
/// </summary>
public class BehindByIsMeasuredFromRealBackupTimesTests
{
    private static readonly DateTime T0 = new(2026, 8, 18, 22, 0, 0);

    private static BackupHistoryEntry Recorded(DateTime at) => new()
    {
        DatabaseName = "MyDb",
        Type = BackupType.TransactionLog,
        StartedAt = at,
        Files = [$@"E:\SQLLogs\MyDb_{at:yyyyMMdd_HHmmss}.trn"]
    };

    private static BackupSet Held(DateTime at, BackupTimestampSource source) => new()
    {
        SetId = at.ToString("yyyyMMdd_HHmmss"),
        DatabaseName = "MyDb",
        Type = BackupType.TransactionLog,
        Timestamp = at,
        TimestampSource = source,
        Files = [new BackupFileInfo { BlobName = "x.trn", Type = BackupType.TransactionLog }]
    };

    private static TimeSpan? Behind(params BackupSet[] held)
        => BackupGapAnalyser.RecoveryTimeNotInContainer([Recorded(T0)], held, "MyDb");

    /// <summary>
    /// The suppression. The container's real newest backup is sixteen hours old; one set carries
    /// only an upload time, read on a clock that puts it after the newest recorded backup. That
    /// reading took the maximum and the gap came out negative, so nothing was said.
    /// </summary>
    [Fact]
    public void AnUploadTimeCannotHideTheGap()
    {
        var behind = Behind(
            Held(T0.AddHours(-16), BackupTimestampSource.FileName),
            Held(T0.AddHours(1), BackupTimestampSource.BlobLastModified));

        Assert.Equal(TimeSpan.FromHours(16), behind);
    }

    /// <summary>
    /// And cannot shrink it either. Converting the reading into the server's zone fixes the clock
    /// and leaves the event wrong - an upload finishes after its backup started, so it always
    /// flatters the container.
    /// </summary>
    [Fact]
    public void AConvertedUploadTimeCannotShrinkTheGap()
    {
        var behind = Behind(
            Held(T0.AddHours(-16), BackupTimestampSource.FileName),
            Held(T0.AddHours(-2), BackupTimestampSource.BlobLastModifiedConverted));

        Assert.Equal(TimeSpan.FromHours(16), behind);
    }

    /// <summary>
    /// With nothing but upload times there is no interval anybody can stand behind, and the panel
    /// already has a shape for that: it says the container is missing backups without quoting a
    /// figure. A wrong number is worse than no number.
    /// </summary>
    [Fact]
    public void WithNoRealBackupTimesThereIsNothingToQuote()
        => Assert.Null(Behind(Held(T0.AddHours(-16), BackupTimestampSource.BlobLastModified)));

    // ── what must keep working ──────────────────────────────────────────────────

    [Fact]
    public void AFilenameTimestampStillMeasures()
        => Assert.Equal(TimeSpan.FromHours(16),
            Behind(Held(T0.AddHours(-16), BackupTimestampSource.FileName)));

    /// <summary>A header time is SQL Server's own account of the backup, on the right clock.</summary>
    [Fact]
    public void AHeaderTimestampStillMeasures()
        => Assert.Equal(TimeSpan.FromHours(16),
            Behind(Held(T0.AddHours(-16), BackupTimestampSource.BackupHeader)));

    /// <summary>A container that is up to date still reports no gap.</summary>
    [Fact]
    public void AContainerThatIsCurrentStillSaysNothing()
        => Assert.Null(Behind(Held(T0, BackupTimestampSource.FileName)));
}
