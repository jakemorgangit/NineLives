using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The copy script puts files where this app can find them again (#491).
///
/// The whole feature is a loop: name the backups the container has not got, hand over a script to
/// copy them in, then rescan and say whether it worked. The script uploaded each file under its
/// bare name - Split-Path -Leaf - which puts it at the ROOT of the container.
///
/// Nothing about the upload fails. But the listing reads a blob's database and server back OUT of
/// its path, by the container's own pattern, and the default pattern is
/// {BackupType}/{ServerName}/{DatabaseName}/{FileName}. A blob at the root has no path to read:
/// the pattern cannot match it, and the structural fallback needs at least three segments to guess
/// a database from. So it is attributed to no database at all.
///
/// Every question this app asks is asked per database, so the copied files are stepped straight
/// over. The rescan compares what is held FOR THIS DATABASE, finds them absent, and reports "None
/// of the N arrived" - after a copy in which every byte transferred successfully. The listing is
/// also prefix-scoped once a database is chosen, so they may not even be enumerated.
///
/// The destination is worked out here now, from the container's pattern and what msdb recorded
/// about the backup, rather than left to Split-Path at run time on the source machine.
/// </summary>
public class CopiedBackupsLandWhereTheAppLooksTests
{
    private static readonly DateTime T0 = new(2026, 8, 18, 22, 0, 0);

    private static BlobContainerConfig Container(string? pattern = null) => new()
    {
        Id = "c1",
        Name = "sqlbackups",
        ContainerUrl = "https://acct.blob.core.windows.net/sqlbackups",
        PathPattern = pattern ?? BlobContainerConfig.DefaultPathPattern
    };

    private static MissingLocation OneMissingLog(string file = @"E:\SQLLogs\MyDb_20260818_220000.trn")
    {
        var entry = new BackupHistoryEntry
        {
            DatabaseName = "MyDb",
            ServerName = "SRV01",
            Type = BackupType.TransactionLog,
            StartedAt = T0,
            Files = [file],
            BackupSizeBytes = 10 * 1024 * 1024
        };

        return new MissingLocation(@"E:\SQLLogs", [new MissingBackup(entry, @"E:\SQLLogs")]);
    }

    /// <summary>
    /// The destination carries the container's layout, so the file lands beside the backups
    /// already there rather than in the root.
    /// </summary>
    [Fact]
    public void TheDestinationFollowsTheContainersOwnPattern()
    {
        var script = MissingBackupCopyScript.Build(OneMissingLog(), Container());

        Assert.Contains("Destination = 'LOG/SRV01/MyDb/MyDb_20260818_220000.trn'", script);
    }

    /// <summary>
    /// The proof that matters: the destination, put back through the REAL listing parser, is
    /// attributed to the database it belongs to. Without that the rescan cannot count it.
    /// </summary>
    [Fact]
    public void TheDestinationListsBackAsThisDatabase()
    {
        var container = Container();
        var script = MissingBackupCopyScript.Build(OneMissingLog(), container);

        var listed = ListBack(container, Destination(script));

        Assert.Equal("MyDb", listed.InferredDatabaseName);
        Assert.Equal("SRV01", listed.InferredServerName);
        Assert.Equal(BackupType.TransactionLog, listed.Type);
    }

    /// <summary>
    /// And what the old script produced does NOT, which is why the rescan reported nothing had
    /// arrived. Pinned so the bare-leaf destination cannot come back unnoticed.
    /// </summary>
    [Fact]
    public void ABareFilenameAtTheRootIsAttributedToNoDatabase()
    {
        var listed = ListBack(Container(), "MyDb_20260818_220000.trn");

        Assert.True(string.IsNullOrEmpty(listed.InferredDatabaseName));
    }

    /// <summary>A container laid out differently is followed, not overridden.</summary>
    [Fact]
    public void ADifferentPatternIsHonoured()
    {
        var script = MissingBackupCopyScript.Build(
            OneMissingLog(), Container("{DatabaseName}/{BackupType}/{FileName}"));

        Assert.Contains("Destination = 'MyDb/LOG/MyDb_20260818_220000.trn'", script);
    }

    /// <summary>
    /// The name is taken off a path the SOURCE machine wrote. A UNC share uses the same separator
    /// this app runs on, but the reverse is not guaranteed, so both are cut.
    /// </summary>
    [Fact]
    public void AUncSourcePathStillYieldsItsFileName()
    {
        var script = MissingBackupCopyScript.Build(
            OneMissingLog(@"\fileserver\logship\MyDb_20260818_220000.trn"), Container());

        Assert.Contains("Destination = 'LOG/SRV01/MyDb/MyDb_20260818_220000.trn'", script);
    }

    /// <summary>The source is still named in full - that is the file being picked up.</summary>
    [Fact]
    public void TheSourcePathIsStillTheFullPath()
    {
        var script = MissingBackupCopyScript.Build(OneMissingLog(), Container());

        Assert.Contains(@"Source = 'E:\SQLLogs\MyDb_20260818_220000.trn'", script);
    }

    /// <summary>Still no credential in the file, which every generator has to keep true.</summary>
    [Fact]
    public void TheScriptStillCarriesNoCredential()
    {
        var script = MissingBackupCopyScript.Build(OneMissingLog(), Container());

        Assert.Contains("Mandatory = $true", script);
        Assert.DoesNotContain("sig=", script);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static string Destination(string script)
    {
        var marker = "Destination = '";
        var start = script.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = script.IndexOf('\'', start);
        return script[start..end];
    }

    /// <summary>
    /// Runs a destination back through the REAL listing parser, so this breaks if either side of
    /// the round trip changes without the other.
    /// </summary>
    private static BackupFileInfo ListBack(BlobContainerConfig container, string blobName)
        => Assert.Single(new BlobStorageService(new FakeCredentialStore())
            .ParseListedBlobsForTests(container, [(blobName, 5_000_000L, new DateTimeOffset(T0, TimeSpan.Zero))]));
}
