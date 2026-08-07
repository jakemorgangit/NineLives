using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// A backup's time can come from two places that are NOT on the same clock: the filename, which
/// the backup job wrote in the SERVER's local time, and the blob's LastModified, which is UTC.
/// Nothing in a container reconciles them.
///
/// The skew cannot be removed without knowing the backup server's zone, so what these tests pin
/// is that the app knows which clock each reading came from and says so, rather than presenting
/// a mixed set as one exact timeline.
/// </summary>
public class BackupTimestampSourceTests
{
    private readonly BlobStorageService _service = new(new CredentialStore());

    private static BackupFileInfo File(string blobName, DateTimeOffset? lastModified = null)
        => new()
        {
            BlobName = blobName,
            Type = BackupType.Full,
            InferredDatabaseName = "MyDb",
            InferredServerName = "SRV01",
            SizeBytes = 100,
            LastModified = lastModified ?? new DateTimeOffset(2026, 6, 15, 22, 30, 0, TimeSpan.Zero)
        };

    [Fact]
    public void ATimestampParsedFromTheFileNameIsRecordedAsSuch()
    {
        var sets = _service.GroupIntoBackupSets([File("FULL/SRV01/MyDb/20260615_220000.bak")]);

        var set = Assert.Single(sets);
        Assert.Equal(BackupTimestampSource.FileName, set.TimestampSource);
        Assert.False(set.IsTimestampApproximate);
        Assert.Null(set.TimestampNote);
        Assert.Equal(new DateTime(2026, 6, 15, 22, 0, 0), set.Timestamp);
    }

    [Fact]
    public void AFileNameWithNoTimestampFallsBackToTheBlobAndIsMarkedApproximate()
    {
        // Plausible before a change window: an ad-hoc backup dropped alongside the scheduled ones.
        var sets = _service.GroupIntoBackupSets([File("FULL/SRV01/MyDb/MyDb_preupgrade.bak")]);

        var set = Assert.Single(sets);
        Assert.Equal(BackupTimestampSource.BlobLastModified, set.TimestampSource);
        Assert.True(set.IsTimestampApproximate);
        Assert.NotNull(set.TimestampNote);
    }

    [Fact]
    public void AMaintenancePlanNameIsAlsoApproximate()
    {
        // Maintenance-plan naming carries a date, but not in a shape ParseTimestamp reads - so it
        // takes the fallback like any other unparsable name.
        var sets = _service.GroupIntoBackupSets(
            [File("FULL/SRV01/MyDb/MyDb_backup_2026_01_28_114441_1234567.bak")]);

        Assert.True(Assert.Single(sets).IsTimestampApproximate);
    }

    [Fact]
    public void OneDatabaseCanHoldBothKindsAtOnce()
    {
        // This is the case that actually bites: homogeneous naming is consistent either way, but a
        // mixed database renders one set hours out of position against the rest.
        var sets = _service.GroupIntoBackupSets(
        [
            File("FULL/SRV01/MyDb/20260615_220000.bak"),
            File("FULL/SRV01/MyDb/MyDb_preupgrade.bak"),
        ]);

        Assert.Equal(2, sets.Count);
        Assert.Single(sets, s => s.TimestampSource == BackupTimestampSource.FileName);
        Assert.Single(sets, s => s.TimestampSource == BackupTimestampSource.BlobLastModified);
    }

    [Fact]
    public void TheFallbackReadsTheBlobsUtcClockNotTheOffsetItArrivedWith()
    {
        // Azure sends +00:00, but DateTimeOffset.DateTime returns the wall clock OF THE OFFSET,
        // so anything that ever supplies a non-zero offset silently shifted the reading. Taking
        // UTC explicitly means the fallback is always on one known clock.
        var sets = _service.GroupIntoBackupSets(
        [
            File("FULL/SRV01/MyDb/adhoc.bak",
                 new DateTimeOffset(2026, 6, 15, 23, 30, 0, TimeSpan.FromHours(1))),
        ]);

        Assert.Equal(new DateTime(2026, 6, 15, 22, 30, 0), Assert.Single(sets).Timestamp);
    }

    [Fact]
    public void AWallClockCarriesNoKindSoNothingCanSilentlyConvertIt()
    {
        // Kind=Utc would invite ToLocalTime() and DateTimeOffset conversions that reinterpret the
        // value against the WORKSTATION's zone - which is neither clock in play.
        var sets = _service.GroupIntoBackupSets([File("FULL/SRV01/MyDb/adhoc.bak")]);

        Assert.Equal(DateTimeKind.Unspecified, Assert.Single(sets).Timestamp.Kind);
    }

    [Fact]
    public void AnApproximateTimeIsMarkedInWhatTheUserSees()
    {
        var approximate = new BackupSet
        {
            Timestamp = new DateTime(2026, 6, 15, 22, 30, 0),
            TimestampSource = BackupTimestampSource.BlobLastModified
        };
        var exact = new BackupSet
        {
            Timestamp = new DateTime(2026, 6, 15, 22, 30, 0),
            TimestampSource = BackupTimestampSource.FileName
        };

        Assert.Equal("~2026-06-15 22:30:00", approximate.TimestampDisplay);
        Assert.Equal("2026-06-15 22:30:00", exact.TimestampDisplay);
    }

    [Fact]
    public void ARestorePointInheritsTheMarkFromTheSetThatPlacesIt()
    {
        var set = new BackupSet
        {
            Timestamp = new DateTime(2026, 6, 15, 22, 30, 0),
            TimestampSource = BackupTimestampSource.BlobLastModified
        };
        var point = new RestorePoint
        {
            Timestamp = set.Timestamp,
            Type = BackupType.Full,
            PrimarySet = set,
            RequiredFullSet = set
        };

        Assert.True(point.IsTimestampApproximate);
        Assert.Equal("~2026-06-15 22:30:00", point.TimestampDisplay);
        Assert.NotNull(point.TimestampNote);
    }

    [Fact]
    public void AFilesEffectiveDatePrefersTheHeaderAndSaysWhenItDidNot()
    {
        var withHeader = File("FULL/SRV01/MyDb/20260615_220000.bak");
        withHeader.BackupStartDate = new DateTime(2026, 6, 15, 22, 0, 0);

        var withoutHeader = File("FULL/SRV01/MyDb/20260615_220000.bak");

        Assert.Equal(BackupTimestampSource.BackupHeader, withHeader.EffectiveDateSource);
        Assert.False(withHeader.IsEffectiveDateApproximate);
        Assert.Null(withHeader.EffectiveDateNote);
        Assert.Equal("2026-06-15 22:00:00", withHeader.EffectiveDateDisplay);

        Assert.Equal(BackupTimestampSource.BlobLastModified, withoutHeader.EffectiveDateSource);
        Assert.True(withoutHeader.IsEffectiveDateApproximate);
        Assert.NotNull(withoutHeader.EffectiveDateNote);
        Assert.Equal("~2026-06-15 22:30:00", withoutHeader.EffectiveDateDisplay);
    }

    [Fact]
    public void AFilesFallbackDateAlsoReadsUtc()
    {
        var file = File("FULL/SRV01/MyDb/adhoc.bak",
                        new DateTimeOffset(2026, 6, 15, 23, 30, 0, TimeSpan.FromHours(1)));

        Assert.Equal(new DateTime(2026, 6, 15, 22, 30, 0), file.EffectiveDate);
        Assert.Equal(DateTimeKind.Unspecified, file.EffectiveDate.Kind);
    }

    [Fact]
    public void TheSetSummaryCountsHowManyTimesItHadToGuess()
    {
        var sets = _service.GroupIntoBackupSets(
        [
            File("FULL/SRV01/MyDb/20260615_220000.bak"),
            File("FULL/SRV01/MyDb/adhoc.bak"),
            File("FULL/SRV01/MyDb/preupgrade.bak"),
        ]);

        Assert.Equal(2, _service.GetSetBasedSummary(sets).ApproximateSets);
    }
}
