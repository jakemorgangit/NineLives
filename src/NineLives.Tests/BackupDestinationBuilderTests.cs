using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// A backup this app writes has to be one this app can then FIND (#165).
///
/// The blob path infers a backup's type, server and database from where it sits in the container,
/// using the container's own configured pattern. A backup written to a layout of its own would land
/// in the container and be invisible to the screen that would restore it - which is the worst shape
/// a backup can take, because it looks like it worked.
///
/// So the tests that matter here are round trips: write a destination, list it back through the
/// real parser, and check the app recognises what it wrote.
/// </summary>
public class BackupDestinationBuilderTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 30, 15);

    private static BlobContainerConfig Container(string? pattern = null) => new()
    {
        Id = "c1",
        Name = "backups",
        ContainerUrl = "https://acct.blob.core.windows.net/backups",
        PathPattern = pattern ?? "{BackupType}/{ServerName}/{DatabaseName}/{FileName}"
    };

    // ── the round trip ──────────────────────────────────────────────────────────

    /// <summary>
    /// The one that matters. Everything else here is a detail of this.
    /// </summary>
    [Theory]
    [InlineData(BackupType.Full)]
    [InlineData(BackupType.Differential)]
    [InlineData(BackupType.TransactionLog)]
    public void ABackupWrittenByThisAppIsFoundByThisApp(BackupType type)
    {
        var container = Container();
        var url = BackupDestinationBuilder
            .ForContainer(container, "SRV01", "MyDb", type, T0, copyOnly: false)
            .Single();

        var found = ListBack(container, url);

        Assert.Equal(type, found.Type);
        Assert.Equal("SRV01", found.InferredServerName);
        Assert.Equal("MyDb", found.InferredDatabaseName);
    }

    [Fact]
    public void ACopyOnlyBackupIsRecognisedAsCopyOnlyWhenItIsListedBack()
    {
        var container = Container();
        var url = BackupDestinationBuilder
            .ForContainer(container, "SRV01", "MyDb", BackupType.Full, T0, copyOnly: true)
            .Single();

        Assert.True(ListBack(container, url).IsCopyOnly);
    }

    /// <summary>
    /// The timestamp is what groups stripes into one set and orders sets on the timeline, and the
    /// parser looks for exactly yyyyMMdd_HHmmss - so the format is a requirement, not a preference.
    /// </summary>
    [Fact]
    public void TheTimestampInTheNameIsTheOneTheParserReads()
    {
        var name = BackupDestinationBuilder.FileName("MyDb", BackupType.Full, T0);

        Assert.Equal(T0, BackupSet.ParseTimestamp(name));
    }

    [Fact]
    public void AStripedBackupIsWrittenAsOneFilePerStripe()
    {
        var urls = BackupDestinationBuilder
            .ForContainer(Container(), "SRV01", "MyDb", BackupType.Full, T0, stripes: 3);

        Assert.Equal(3, urls.Count);
        Assert.EndsWith("_1.bak", urls[0]);
        Assert.EndsWith("_2.bak", urls[1]);
        Assert.EndsWith("_3.bak", urls[2]);
    }

    /// <summary>A single-file backup carries no stripe number, because it is not one of several.</summary>
    [Fact]
    public void AnUnstripedBackupHasNoStripeNumber()
    {
        var url = BackupDestinationBuilder
            .ForContainer(Container(), "SRV01", "MyDb", BackupType.Full, T0)
            .Single();

        Assert.EndsWith("_20260801_223015.bak", url);
    }

    // ── the container's own pattern, not one of ours ────────────────────────────

    [Fact]
    public void ThePatternTheContainerIsConfiguredWithIsTheOneUsed()
    {
        var container = Container("{ServerName}/{DatabaseName}/{BackupType}/{FileName}");

        var url = BackupDestinationBuilder
            .ForContainer(container, "SRV01", "MyDb", BackupType.Full, T0)
            .Single();

        Assert.Contains("/SRV01/MyDb/FULL/", url);
        Assert.Equal(BackupType.Full, ListBack(container, url).Type);
    }

    [Fact]
    public void ANamedInstanceIsSplitTheWayThePathParserExpects()
    {
        var container = Container("{BackupType}/{ServerName}/{InstanceName}/{DatabaseName}/{FileName}");

        var url = BackupDestinationBuilder
            .ForContainer(container, @"SRV01\PROD", "MyDb", BackupType.Full, T0)
            .Single();

        Assert.Contains("/FULL/SRV01/PROD/MyDb/", url);
    }

    /// <summary>
    /// A pattern with a token this app cannot fill would otherwise leave an empty segment, putting
    /// the backup one folder shallower than the listing expects to find it.
    /// </summary>
    [Fact]
    public void AnUnfillableTokenDoesNotLeaveAHoleInThePath()
    {
        var container = Container("{BackupType}/{ServerName}/{InstanceName}/{DatabaseName}/{FileName}");

        var url = BackupDestinationBuilder
            .ForContainer(container, "SRV01", "MyDb", BackupType.Full, T0)
            .Single();

        Assert.DoesNotContain("//backups//", url);
        Assert.Contains("/FULL/SRV01/MyDb/", url);
    }

    [Fact]
    public void ATrailingSlashOnTheContainerUrlDoesNotDoubleUp()
    {
        var container = Container();
        container.ContainerUrl = "https://acct.blob.core.windows.net/backups/";

        var url = BackupDestinationBuilder
            .ForContainer(container, "SRV01", "MyDb", BackupType.Full, T0)
            .Single();

        Assert.DoesNotContain("backups//FULL", url);
    }

    // ── names that are legal on SQL Server and not in a path ────────────────────

    /// <summary>
    /// A database name is far freer than a filename. <c>My/Db</c> is legal on SQL Server and would
    /// silently become a FOLDER in a blob path, putting the backup somewhere the listing reads as a
    /// different database entirely.
    /// </summary>
    [Fact]
    public void ASlashInADatabaseNameDoesNotBecomeAFolder()
    {
        var container = Container();

        var url = BackupDestinationBuilder
            .ForContainer(container, "SRV01", "My/Db", BackupType.Full, T0)
            .Single();

        Assert.Contains("/My_Db/", url);
        Assert.Equal("My_Db", ListBack(container, url).InferredDatabaseName);
    }

    [Theory]
    [InlineData("My:Db")]
    [InlineData("My*Db")]
    [InlineData(@"My\Db")]
    public void CharactersAFileNameCannotHoldAreReplaced(string databaseName)
    {
        var name = BackupDestinationBuilder.FileName(databaseName, BackupType.Full, T0);

        Assert.DoesNotContain(":", name);
        Assert.DoesNotContain("*", name);
        Assert.DoesNotContain(@"\", name);
    }

    // ── a share ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A share needs no pattern - backups there are found through the source instance's msdb, which
    /// records the path. So the layout only has to be sane for a person looking at the folder.
    /// </summary>
    [Fact]
    public void ABackupToAShareGoesInADirectoryPerDatabase()
    {
        var path = BackupDestinationBuilder
            .ForSharedPath(@"\\nas01\sql", "MyDb", BackupType.Full, T0)
            .Single();

        Assert.Equal(@"\\nas01\sql\MyDb\MyDb_FULL_COPY_ONLY_20260801_223015.bak", path);
    }

    [Fact]
    public void ATrailingSeparatorOnTheShareRootDoesNotDoubleUp()
    {
        var path = BackupDestinationBuilder
            .ForSharedPath(@"\\nas01\sql\", "MyDb", BackupType.Full, T0)
            .Single();

        Assert.DoesNotContain(@"sql\\MyDb", path);
    }

    [Fact]
    public void ALogBackupToAShareIsWrittenAsTrn()
    {
        var path = BackupDestinationBuilder
            .ForSharedPath(@"\\nas01\sql", "MyDb", BackupType.TransactionLog, T0)
            .Single();

        Assert.EndsWith(".trn", path);
    }

    // ── helper ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a written destination back through the REAL listing parser, so these tests break if
    /// either side of the round trip changes without the other.
    /// </summary>
    private static BackupFileInfo ListBack(BlobContainerConfig container, string url)
    {
        var blobName = url[(container.ContainerUrl.TrimEnd('/').Length + 1)..];

        var files = new BlobStorageService(new FakeCredentialStore())
            .ParseListedBlobsForTests(container, [(blobName, 5_000_000L, new DateTimeOffset(T0, TimeSpan.Zero))]);

        return Assert.Single(files);
    }
}
