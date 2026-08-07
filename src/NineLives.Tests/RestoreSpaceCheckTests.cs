using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Whether a restore can physically fit, asked before it starts (#32).
///
/// A restore that runs out of disk fails part-way - and by then WITH REPLACE has already dropped
/// the database it was replacing, so the target is gone and the replacement is incomplete. That is
/// the worst outcome this app can produce, and it is entirely predictable: FILELISTONLY already
/// reports how big each file will be, and SQL Server already knows how much room each volume has.
/// The app was reading the first number and throwing it away.
/// </summary>
public class RestoreSpaceCheckTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static FileMoveOption File_(string to, long sizeBytes) => new()
    {
        LogicalName = "MyDb",
        PhysicalName = @"E:\Original\MyDb.mdf",
        Type = "D",
        NewPhysicalName = to,
        SizeBytes = sizeBytes
    };

    // ── the arithmetic that matters ─────────────────────────────────────────────

    /// <summary>
    /// Per VOLUME, not per file. Four 10 GB files on one drive is 40 GB, and checking them
    /// individually would pass every one of them while the restore still fails.
    /// </summary>
    [Fact]
    public void FilesLandingOnTheSameVolumeAreAddedUp()
    {
        var volumes = RestoreSpaceCheck.Check(
        [
            File_(@"D:\Data\a.mdf", 10 * Gb),
            File_(@"D:\Data\b.ndf", 10 * Gb),
            File_(@"D:\Data\c.ndf", 10 * Gb),
            File_(@"D:\Data\d.ndf", 10 * Gb)
        ],
        new Dictionary<string, long> { [@"D:\"] = 25 * Gb });

        var volume = Assert.Single(volumes);

        Assert.Equal(40 * Gb, volume.RequiredBytes);
        Assert.False(volume.Fits);
        Assert.Equal(15 * Gb, volume.ShortfallBytes);
    }

    /// <summary>Data and log on different drives is the normal arrangement, and each is its own question.</summary>
    [Fact]
    public void EachVolumeIsAnsweredSeparately()
    {
        var volumes = RestoreSpaceCheck.Check(
        [
            File_(@"D:\Data\MyDb.mdf", 100 * Gb),
            File_(@"L:\Logs\MyDb_log.ldf", 10 * Gb)
        ],
        new Dictionary<string, long> { [@"D:\"] = 200 * Gb, [@"L:\"] = 5 * Gb });

        Assert.Equal(2, volumes.Count);
        Assert.True(volumes.Single(v => v.Volume == @"D:\").Fits);
        Assert.False(volumes.Single(v => v.Volume == @"L:\").Fits);
    }

    [Fact]
    public void ARestoreThatFitsSaysNothing()
    {
        var volumes = RestoreSpaceCheck.Check(
            [File_(@"D:\Data\MyDb.mdf", 10 * Gb)],
            new Dictionary<string, long> { [@"D:\"] = 500 * Gb });

        Assert.True(Assert.Single(volumes).Fits);
        Assert.Equal(string.Empty, RestoreSpaceCheck.Warn(volumes));
    }

    /// <summary>
    /// The warning says what would happen, not just that something is tight. WITH REPLACE having
    /// already dropped the target is the part somebody needs to weigh.
    /// </summary>
    [Fact]
    public void TheWarningSaysWhatWouldActuallyHappen()
    {
        var volumes = RestoreSpaceCheck.Check(
            [File_(@"D:\Data\MyDb.mdf", 100 * Gb)],
            new Dictionary<string, long> { [@"D:\"] = 10 * Gb });

        var warning = RestoreSpaceCheck.Warn(volumes);

        Assert.Contains("may not fit", warning);
        Assert.Contains("short by", warning);
        Assert.Contains("dropped before that happens", warning);
    }

    // ── where the files are actually going ──────────────────────────────────────

    /// <summary>
    /// The path the restore will WRITE to, not the one recorded in the backup. Checking the source's
    /// path would measure a drive on a different machine - and with WITH MOVE in use, the whole
    /// point is that the two differ.
    /// </summary>
    [Fact]
    public void TheVolumeCheckedIsWhereTheFileIsGoingNotWhereItCameFrom()
    {
        var file = File_(@"D:\Data\MyDb.mdf", 10 * Gb);
        file.PhysicalName = @"Z:\SomewhereElse\MyDb.mdf";

        var volume = Assert.Single(RestoreSpaceCheck.Check(
            [file], new Dictionary<string, long> { [@"D:\"] = 500 * Gb, [@"Z:\"] = 0 }));

        Assert.Equal(@"D:\", volume.Volume);
        Assert.True(volume.Fits);
    }

    [Theory]
    [InlineData(@"D:\Data\MyDb.mdf", @"D:\")]
    [InlineData(@"d:\data\mydb.mdf", @"D:\")]
    [InlineData(@"E:\MyDb.mdf", @"E:\")]
    public void AVolumeIsTheDriveAPathLandsOn(string path, string expected)
        => Assert.Equal(expected, RestoreSpaceCheck.VolumeOf(path));

    /// <summary>
    /// A UNC path has no drive letter, and dm_os_volume_stats does not describe a share - so this
    /// check simply does not cover it, and says so by not inventing an answer.
    /// </summary>
    [Theory]
    [InlineData(@"\\nas01\sql\MyDb.mdf")]
    [InlineData("")]
    [InlineData(null)]
    public void APathWithNoDriveLetterIsNotGuessedAt(string? path)
        => Assert.Null(RestoreSpaceCheck.VolumeOf(path));

    // ── not knowing is not the same as fitting ──────────────────────────────────

    /// <summary>
    /// A volume the target never reported on comes back as having nothing, so it is raised rather
    /// than silently passing. sys.dm_os_volume_stats only sees volumes the instance already has a
    /// database file on, so a brand-new empty drive is exactly this case.
    /// </summary>
    [Fact]
    public void AVolumeTheServerSaidNothingAboutIsReportedRatherThanAssumedFine()
    {
        var volumes = RestoreSpaceCheck.Check(
            [File_(@"X:\Data\MyDb.mdf", 10 * Gb)],
            new Dictionary<string, long> { [@"D:\"] = 500 * Gb });

        var volume = Assert.Single(volumes);

        Assert.Equal(@"X:\", volume.Volume);
        Assert.False(volume.Fits);
    }

    /// <summary>
    /// A mount point may or may not carry its trailing separator depending on how it was mounted,
    /// so a miss on the exact string is not yet a miss.
    /// </summary>
    [Fact]
    public void AMountPointReportedWithoutItsSeparatorStillMatches()
    {
        var volumes = RestoreSpaceCheck.Check(
            [File_(@"D:\Data\MyDb.mdf", 10 * Gb)],
            new Dictionary<string, long> { ["D:"] = 500 * Gb });

        Assert.True(Assert.Single(volumes).Fits);
    }

    [Fact]
    public void FilesOnAShareAreLeftOutRatherThanCountedAgainstNothing()
    {
        var volumes = RestoreSpaceCheck.Check(
            [File_(@"\\nas01\sql\MyDb.mdf", 10 * Gb)],
            new Dictionary<string, long> { [@"D:\"] = 500 * Gb });

        Assert.Empty(volumes);
    }
}
