using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// A device is what the RESTORE will name, and where the app learned it says nothing about
/// what it is (#375).
///
/// The inspection statements asked the device STRING; the restore generator asked which FIELD
/// of BackupFileInfo held it. Those agree for anything discovered by listing a container, and
/// disagree for a backup written to a bucket and read back from the instance's own msdb - that
/// arrives with its s3:// URL in LocalPath, so IsOnDisk is true for something that was never on
/// a disk, and the statement said DISK = N's3://...', which SQL Server cannot open.
/// </summary>
public class BackupDeviceTests
{
    [Theory]
    [InlineData(@"\\nas01\sql\MyDb.bak", true)]
    [InlineData(@"D:\backups\MyDb.bak", true)]
    [InlineData("https://acct.blob.core.windows.net/backups/MyDb.bak", false)]
    [InlineData("s3://s3.eu-west-2.amazonaws.com/backups/MyDb.bak", false)]
    [InlineData("", false)]
    public void APathIsAPathAndEverythingElseIsAUrl(string device, bool isPath)
    {
        Assert.Equal(isPath, BackupDevice.IsPath(device));
    }

    /// <summary>
    /// The case the bug was made of: an s3:// URL that arrived through msdb, so it sits in
    /// LocalPath and IsOnDisk answers true.
    /// </summary>
    [Fact]
    public void ABucketUrlRecordedAsAPathStillRestoresFromUrl()
    {
        var chain = new BackupChain
        {
            FullSet = new BackupSet
            {
                SetId = "20260801_220000",
                Type = BackupType.Full,
                Timestamp = new DateTime(2026, 8, 1, 22, 0, 0),
                Files =
                [
                    new BackupFileInfo
                    {
                        // How an instance's own history reports it.
                        LocalPath = "s3://s3.eu-west-2.amazonaws.com/backups/FULL/MyDb.bak",
                        BlobName = "MyDb.bak",
                        Type = BackupType.Full
                    }
                ]
            }
        };

        var script = new RestoreScriptGenerator().Generate(
            chain, new RestoreOptions { TargetDatabaseName = "MyDb", S3Region = "eu-west-2" });

        Assert.Contains("URL = N's3://s3.eu-west-2.amazonaws.com/backups/FULL/MyDb.bak'", script);
        Assert.DoesNotContain("DISK = N's3://", script);

        // And it is recognised as S3, so the region survives rather than being stripped.
        Assert.Contains("eu-west-2", script);
    }

    /// <summary>A genuine path is still FROM DISK, and carries no region.</summary>
    [Fact]
    public void ASharedPathIsStillRestoredFromDisk()
    {
        var chain = new BackupChain
        {
            FullSet = new BackupSet
            {
                SetId = "20260801_220000",
                Type = BackupType.Full,
                Timestamp = new DateTime(2026, 8, 1, 22, 0, 0),
                Files =
                [
                    new BackupFileInfo
                    {
                        LocalPath = @"\\nas01\sql\MyDb.bak",
                        BlobName = "MyDb.bak",
                        Type = BackupType.Full
                    }
                ]
            }
        };

        var script = new RestoreScriptGenerator().Generate(
            chain, new RestoreOptions { TargetDatabaseName = "MyDb", S3Region = "eu-west-2" });

        Assert.Contains(@"DISK = N'\\nas01\sql\MyDb.bak'", script);
        Assert.DoesNotContain("eu-west-2", script);
    }
}
