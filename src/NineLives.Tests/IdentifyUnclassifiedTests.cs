using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Asking SQL Server what a file is, for the files whose NAME did not say (#130).
///
/// The header is what the RESTORE itself reads, so it is immune to every way the filename inference
/// has been wrong - a database whose name contains "diff" (#44), a full misread as a differential
/// leaving a container with no restore points at all (#45), a wrong path pattern, a file dropped in
/// the wrong folder.
///
/// It is scoped to the unplaceable files rather than run over everything because of cost: one
/// HEADERONLY per file is a network read, and across a container that is thousands. Across the
/// handful the pattern could not place it is seconds - and those are exactly the files worth
/// paying for, because they are the ones the app cannot offer at all.
/// </summary>
public class IdentifyUnclassifiedTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static BackupFileInfo Unplaceable(string name = "mystery.bak") => new()
    {
        BlobName = name,
        BlobUrl = $"https://acct.blob.core.windows.net/backups/{name}",
        Type = BackupType.Unknown,
        LastModified = new DateTimeOffset(T0, TimeSpan.Zero)
    };

    private static BackupFileInfo Header(
        BackupType type = BackupType.Full,
        string database = "MyDb",
        decimal? checkpoint = 100,
        decimal? lastLsn = 200) => new()
    {
        DatabaseName = database,
        Type = type,
        BackupTypeCode = type == BackupType.Full ? 1 : type == BackupType.Differential ? 5 : 2,
        BackupStartDate = T0.AddHours(-3),
        BackupFinishDate = T0.AddHours(-3).AddMinutes(4),
        CheckpointLsn = checkpoint,
        LastLsn = lastLsn
    };

    // ── which files get asked about ─────────────────────────────────────────────

    /// <summary>
    /// Both of these mean a file cannot reach a chain at all: an unknown type never enters the
    /// fulls, diffs or logs collections, and one with no database is filtered out of every working
    /// set. They are invisible, not merely unlabelled.
    /// </summary>
    [Fact]
    public void AFileWithNoTypeNeedsIdentifying()
        => Assert.True(BackupHeaderIdentifier.NeedsIdentifying(Unplaceable()));

    [Fact]
    public void AFileWithNoDatabaseNeedsIdentifying()
    {
        var file = Unplaceable();
        file.Type = BackupType.Full;

        Assert.True(BackupHeaderIdentifier.NeedsIdentifying(file));
    }

    [Fact]
    public void AFileTheFilenamePlacedIsLeftAlone()
    {
        var file = Unplaceable();
        file.Type = BackupType.Full;
        file.InferredDatabaseName = "MyDb";

        Assert.False(BackupHeaderIdentifier.NeedsIdentifying(file));
    }

    // ── what the header settles ─────────────────────────────────────────────────

    [Fact]
    public void TheHeaderSaysWhatTheFileIsAndWhichDatabaseItBelongsTo()
    {
        var file = Unplaceable();

        Assert.True(BackupHeaderIdentifier.Apply(file, Header(BackupType.Differential)));

        Assert.Equal(BackupType.Differential, file.Type);
        Assert.Equal("MyDb", file.InferredDatabaseName);
    }

    /// <summary>
    /// The header wins wherever the two disagree, because it is what the restore reads. #44 and #45
    /// are both cases where the filename was confidently wrong.
    /// </summary>
    [Fact]
    public void TheHeaderBeatsTheFilenameWhereTheyDisagree()
    {
        var file = Unplaceable("DiffusionDb_full.bak");
        file.Type = BackupType.Differential;
        file.InferredDatabaseName = "Diffusion";

        Assert.True(BackupHeaderIdentifier.Apply(file, Header(BackupType.Full, "DiffusionDb")));

        Assert.Equal(BackupType.Full, file.Type);
        Assert.Equal("DiffusionDb", file.InferredDatabaseName);
    }

    /// <summary>
    /// The LSNs are the second reason a header read earns its round trip: a set that knows them can
    /// be paired definitively rather than by proximity in time.
    /// </summary>
    [Fact]
    public void TheLsnsComeAcrossWhetherOrNotAnythingWasReclassified()
    {
        var file = Unplaceable();
        file.Type = BackupType.Full;
        file.InferredDatabaseName = "MyDb";

        // Nothing to settle - the header agrees with what was already known.
        Assert.False(BackupHeaderIdentifier.Apply(file, Header()));

        Assert.Equal(100, file.CheckpointLsn);
        Assert.Equal(200, file.LastLsn);
    }

    /// <summary>
    /// The instance's own record of when the backup ran. Stronger than a filename, and far stronger
    /// than the blob's LastModified - which is when the UPLOAD finished, in UTC, and is exactly what
    /// a file with an unreadable name falls back to.
    /// </summary>
    [Fact]
    public void TheHeaderSuppliesTheTimeTheBackupActuallyRan()
    {
        var file = Unplaceable();

        BackupHeaderIdentifier.Apply(file, Header());

        Assert.Equal(T0.AddHours(-3), file.BackupStartDate);
    }

    // ── through the whole thing ─────────────────────────────────────────────────

    [Fact]
    public async Task EveryUnplaceableFileIsAskedAbout()
    {
        var sql = new FakeSqlServerService { Header = Header() };
        var files = new List<BackupFileInfo> { Unplaceable("a.bak"), Unplaceable("b.bak") };

        var settled = await new BackupHeaderIdentifier(sql).IdentifyAsync(Server(), files);

        Assert.Equal(2, settled);
        Assert.Equal(2, sql.HeaderReads.Count);
    }

    /// <summary>
    /// A container legitimately holds things that are not backups. One that cannot be read leaves
    /// the app exactly where it started for that file, which is not a worse position - and must not
    /// stop the rest.
    /// </summary>
    [Fact]
    public async Task AFileTheServerCannotReadDoesNotStopTheOthers()
    {
        var sql = new FakeSqlServerService
        {
            Header = Header(),
            HeaderThrowsForUrlContaining = "notabackup"
        };

        var files = new List<BackupFileInfo>
        {
            Unplaceable("notabackup.txt"), Unplaceable("real.bak")
        };

        var settled = await new BackupHeaderIdentifier(sql).IdentifyAsync(Server(), files);

        Assert.Equal(1, settled);
        Assert.Equal(BackupType.Unknown, files[0].Type);
        Assert.Equal(BackupType.Full, files[1].Type);
    }

    /// <summary>
    /// A file discovered through an instance's msdb was never unplaceable - msdb recorded its
    /// database, its type and its LSNs - so this path is only ever reached by a container listing,
    /// and a HEADERONLY built out of URL clauses would be wrong for it anyway.
    /// </summary>
    [Fact]
    public async Task AFileOnDiskIsNotAskedAbout()
    {
        var sql = new FakeSqlServerService { Header = Header() };
        var onDisk = new BackupFileInfo { LocalPath = @"\\nas01\sql\full.bak", Type = BackupType.Unknown };

        await new BackupHeaderIdentifier(sql).IdentifyAsync(Server(), [onDisk]);

        Assert.Empty(sql.HeaderReads);
    }

    [Fact]
    public async Task ProgressIsReportedPerFile()
    {
        var sql = new FakeSqlServerService { Header = Header() };
        var seen = new List<int>();

        await new BackupHeaderIdentifier(sql).IdentifyAsync(
            Server(),
            [Unplaceable("a.bak"), Unplaceable("b.bak"), Unplaceable("c.bak")],
            new Progress<int>(seen.Add));

        // Progress<T> marshals through the synchronisation context, so what matters is that it
        // reaches the last count rather than exactly how the intermediate ones interleave.
        await Task.Delay(50);
        Assert.Contains(3, seen);
    }

    private static ServerConnection Server() =>
        new() { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" };
}
