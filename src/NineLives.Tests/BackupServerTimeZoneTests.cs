using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Putting both clocks on one clock (#102).
///
/// #47 made the app honest about the fact that a filename timestamp reads the backup server's
/// local clock while a blob's LastModified reads UTC - it labelled the second as approximate and
/// left it there. That is all it could do: nothing in a container says what zone the server is in.
///
/// Told the zone, the UTC reading converts and the two become comparable rather than merely
/// labelled. What no zone can fix is that LastModified is when the UPLOAD finished, not when the
/// backup was taken, so it stays marked approximate.
/// </summary>
public class BackupServerTimeZoneTests
{
    private readonly BlobStorageService _service = new(new FakeCredentialStore());

    /// <summary>UTC+10 in winter, UTC+11 in summer - so DST is actually exercised.</summary>
    private const string Sydney = "AUS Eastern Standard Time";

    private static BackupFileInfo File(string blobName, DateTimeOffset lastModified)
        => new()
        {
            BlobName = blobName,
            Type = BackupType.Full,
            InferredServerName = "SRV01",
            InferredDatabaseName = "MyDb",
            SizeBytes = 100,
            LastModified = lastModified
        };

    [Fact]
    public void WithoutAZoneTheReadingStaysOnTheBlobsClock()
    {
        var sets = _service.GroupIntoBackupSets(
            [File("FULL/SRV01/MyDb/adhoc.bak", new DateTimeOffset(2026, 6, 15, 22, 30, 0, TimeSpan.Zero))]);

        var set = Assert.Single(sets);
        Assert.Equal(BackupTimestampSource.BlobLastModified, set.TimestampSource);
        Assert.Equal(new DateTime(2026, 6, 15, 22, 30, 0), set.Timestamp);
        Assert.True(set.IsTimestampOnAnotherClock);
    }

    [Fact]
    public void WithAZoneTheReadingMovesOntoTheBackupServersClock()
    {
        // 22:30 UTC on 15 June is 08:30 the next morning in Sydney (UTC+10, no DST in June).
        var sets = _service.GroupIntoBackupSets(
            [File("FULL/SRV01/MyDb/adhoc.bak", new DateTimeOffset(2026, 6, 15, 22, 30, 0, TimeSpan.Zero))],
            Sydney);

        var set = Assert.Single(sets);
        Assert.Equal(BackupTimestampSource.BlobLastModifiedConverted, set.TimestampSource);
        Assert.Equal(new DateTime(2026, 6, 16, 8, 30, 0), set.Timestamp);

        // On the right clock now, so it sorts correctly - but still the upload time.
        Assert.False(set.IsTimestampOnAnotherClock);
        Assert.True(set.IsTimestampApproximate);
    }

    /// <summary>
    /// The reason a zone is stored rather than a fixed offset. A container spanning a DST
    /// transition needs the rule, not one number.
    /// </summary>
    [Fact]
    public void TheOffsetFollowsDaylightSaving()
    {
        var winter = _service.GroupIntoBackupSets(
            [File("FULL/SRV01/MyDb/winter.bak", new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero))],
            Sydney).Single();

        var summer = _service.GroupIntoBackupSets(
            [File("FULL/SRV01/MyDb/summer.bak", new DateTimeOffset(2026, 12, 15, 12, 0, 0, TimeSpan.Zero))],
            Sydney).Single();

        Assert.Equal(new DateTime(2026, 6, 15, 22, 0, 0), winter.Timestamp);   // UTC+10
        Assert.Equal(new DateTime(2026, 12, 15, 23, 0, 0), summer.Timestamp);  // UTC+11
    }

    /// <summary>
    /// The whole point: a database with both kinds of naming sorts in the right order once the
    /// zone is known. Unconverted, the ad-hoc backup lands ten hours out of position.
    /// </summary>
    [Fact]
    public void AMixedDatabaseSortsCorrectlyOnceTheZoneIsKnown()
    {
        // The scheduled backup ran at 09:00 Sydney time; the ad-hoc one an hour later, which is
        // 00:00 UTC.
        List<BackupFileInfo> files =
        [
            File("FULL/SRV01/MyDb/20260616_090000.bak", new DateTimeOffset(2026, 6, 15, 23, 0, 0, TimeSpan.Zero)),
            File("FULL/SRV01/MyDb/adhoc.bak", new DateTimeOffset(2026, 6, 16, 0, 0, 0, TimeSpan.Zero)),
        ];

        var unconverted = _service.GroupIntoBackupSets(files);
        var adhocUnconverted = unconverted.Single(s => s.SetId == "adhoc");
        var scheduled = unconverted.Single(s => s.SetId == "20260616_090000");

        // 00:00 against 09:00 - the ad-hoc one looks nine hours EARLIER than the backup it
        // actually followed.
        Assert.True(adhocUnconverted.Timestamp < scheduled.Timestamp);

        var converted = _service.GroupIntoBackupSets(files, Sydney);
        var adhocConverted = converted.Single(s => s.SetId == "adhoc");

        Assert.Equal(new DateTime(2026, 6, 16, 10, 0, 0), adhocConverted.Timestamp);
        Assert.True(adhocConverted.Timestamp > scheduled.Timestamp);
        Assert.Equal("adhoc", converted[^1].SetId);
    }

    [Fact]
    public void AFileNameTimestampIsNeverConverted()
    {
        // It is already on the backup server's clock. Converting it would move it by the offset
        // for no reason - the exact bug in reverse.
        var sets = _service.GroupIntoBackupSets(
            [File("FULL/SRV01/MyDb/20260615_220000.bak", new DateTimeOffset(2026, 6, 15, 22, 0, 0, TimeSpan.Zero))],
            Sydney);

        var set = Assert.Single(sets);
        Assert.Equal(BackupTimestampSource.FileName, set.TimestampSource);
        Assert.Equal(new DateTime(2026, 6, 15, 22, 0, 0), set.Timestamp);
    }

    [Theory]
    [InlineData("Not A Real Zone")]
    [InlineData("")]
    [InlineData("   ")]
    public void AZoneThisMachineDoesNotKnowFallsBackRatherThanThrowing(string id)
    {
        // A config carried from another machine, or hand-edited. Browsing a container must not be
        // stopped by a setting that only affects how a few timestamps are displayed.
        var sets = _service.GroupIntoBackupSets(
            [File("FULL/SRV01/MyDb/adhoc.bak", new DateTimeOffset(2026, 6, 15, 22, 30, 0, TimeSpan.Zero))],
            id);

        var set = Assert.Single(sets);
        Assert.Equal(BackupTimestampSource.BlobLastModified, set.TimestampSource);
        Assert.Equal(new DateTime(2026, 6, 15, 22, 30, 0), set.Timestamp);
    }

    [Fact]
    public void AConvertedReadingExplainsItselfDifferently()
    {
        var converted = new BackupSet { TimestampSource = BackupTimestampSource.BlobLastModifiedConverted };
        var unconverted = new BackupSet { TimestampSource = BackupTimestampSource.BlobLastModified };

        Assert.Contains("time zone", converted.TimestampNote);
        Assert.DoesNotContain("time zone", unconverted.TimestampNote);
        Assert.Contains("hours away", unconverted.TimestampNote);
    }

    // ── the setting itself ──────────────────────────────────────────────────────

    [Fact]
    public void TheZoneIsSavedAndReadBack()
    {
        var store = new FakeCredentialStore();
        var vm = new BlobConfigViewModel(store, new FakeBlobStorageService());

        vm.AddNewCommand.Execute(null);
        vm.EditName = "backups";
        vm.EditContainerUrl = "https://mystorageaccount.blob.core.windows.net/backups";
        vm.EditSasToken = "sv=2026-01-01&sig=x";
        vm.EditBackupServerTimeZone = BlobConfigViewModel.TimeZones.First(z => z.Id == Sydney);
        vm.SaveCommand.Execute(null);

        var saved = Assert.Single(store.Config.BlobContainers);
        Assert.Equal(Sydney, saved.BackupServerTimeZoneId);

        // And it comes back into the form when the container is edited again.
        var reopened = new BlobConfigViewModel(store, new FakeBlobStorageService())
        {
            SelectedContainer = null
        };
        reopened.SelectedContainer = reopened.Containers.Single();
        reopened.EditCommand.Execute(null);

        Assert.Equal(Sydney, reopened.EditBackupServerTimeZone?.Id);
    }

    [Fact]
    public void NotKnownIsTheDefaultAndStoresNothing()
    {
        var store = new FakeCredentialStore();
        var vm = new BlobConfigViewModel(store, new FakeBlobStorageService());

        vm.AddNewCommand.Execute(null);
        vm.EditName = "backups";
        vm.EditContainerUrl = "https://mystorageaccount.blob.core.windows.net/backups";
        vm.EditSasToken = "sv=2026-01-01&sig=x";
        vm.SaveCommand.Execute(null);

        Assert.Null(Assert.Single(store.Config.BlobContainers).BackupServerTimeZoneId);
    }

    [Fact]
    public void AStoredZoneThisMachineDoesNotKnowShowsAsNotKnown()
    {
        // Otherwise the picker claims a setting the app is not actually applying.
        var option = TimeZoneOption.For("Mars Standard Time", BlobConfigViewModel.TimeZones);

        Assert.Null(option.Id);
        Assert.Same(TimeZoneOption.Unknown, option);
    }

    [Fact]
    public void ChangingOnlyTheZoneCountsAsAnUnsavedChange()
    {
        var store = new FakeCredentialStore();
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = BlobContainerConfig.NewId(),
            Name = "backups",
            ContainerUrl = "https://mystorageaccount.blob.core.windows.net/backups"
        });

        var vm = new BlobConfigViewModel(store, new FakeBlobStorageService());
        vm.SelectedContainer = vm.Containers.Single();
        vm.EditCommand.Execute(null);

        Assert.False(vm.HasUnsavedChanges);

        vm.EditBackupServerTimeZone = BlobConfigViewModel.TimeZones.First(z => z.Id == Sydney);

        Assert.True(vm.HasUnsavedChanges);
    }
}
