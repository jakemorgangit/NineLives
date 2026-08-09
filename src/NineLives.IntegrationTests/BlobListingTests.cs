using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.IntegrationTests;

/// <summary>
/// What the app makes of a real container (#37).
///
/// The unit suite exercises the path-pattern parsing over hand-built objects. What it cannot show
/// is that the ENUMERATION hands the parser the shape it expects - a blob name carrying its full
/// virtual path, a size, a last-modified - and that is the join everything downstream rests on:
/// the chain, the timeline and the script are all built from what this decides each blob is.
///
/// The gap has already had a consequence. Microsoft.Data.SqlClient was auto-bumped two major
/// versions and CI stayed green because nothing exercised it. The Azure SDK is one dependency
/// bump away from the same position.
/// </summary>
[Collection("Azurite")]
public class BlobListingTests
{
    private static BlobStorageService Service() => new(new NoCredentialStore());

    // ── the standalone layout ───────────────────────────────────────────────────

    /// <summary>
    /// The default arrangement, and the one most containers this points at use: the folder says
    /// what kind of backup it is, and the two below it say which server and database.
    /// </summary>
    [AzuriteFact]
    public async Task ADefaultLayoutIsReadBackCorrectly()
    {
        var container = await Azurite.NewContainerAsync();
        await Azurite.PutAsync(container, "FULL/SRV01/MyDb/MyDb_20260801_220000.bak", 5_000_000);
        await Azurite.PutAsync(container, "DIFF/SRV01/MyDb/MyDb_20260802_060000.bak", 1_000_000);
        await Azurite.PutAsync(container, "LOG/SRV01/MyDb/MyDb_20260802_070000.trn", 100_000);

        var files = await Service().ListBackupFilesAsync(Azurite.ConfigFor(container));

        Assert.Equal(3, files.Count);
        Assert.All(files, f => Assert.Equal("MyDb", f.InferredDatabaseName));
        Assert.All(files, f => Assert.Equal("SRV01", f.InferredServerName));

        Assert.Equal(BackupType.Full, Find(files, "MyDb_20260801_220000.bak").Type);
        Assert.Equal(BackupType.Differential, Find(files, "MyDb_20260802_060000.bak").Type);
        Assert.Equal(BackupType.TransactionLog, Find(files, "MyDb_20260802_070000.trn").Type);
    }

    /// <summary>
    /// The size and last-modified come from the blob rather than from the name, and the app relies
    /// on both - the size for the disk-space preflight (#32) and the timestamp as the fallback
    /// whenever a filename carries none.
    /// </summary>
    [AzuriteFact]
    public async Task TheBlobsOwnMetadataComesBackWithIt()
    {
        var container = await Azurite.NewContainerAsync();
        await Azurite.PutAsync(container, "FULL/SRV01/MyDb/MyDb_20260801_220000.bak", 4_096);

        var file = Assert.Single(await Service().ListBackupFilesAsync(Azurite.ConfigFor(container)));

        Assert.Equal(4_096, file.SizeBytes);
        Assert.NotEqual(default, file.LastModified);

        // The ETag is what an audit result is cached against (#130). A listing that stopped
        // reporting it would silently make every audit re-read every header.
        Assert.False(string.IsNullOrWhiteSpace(file.ETag));
    }

    /// <summary>
    /// A named instance gets its own level, and the app has to keep the two apart - two instances
    /// of one host routinely hold same-named databases, and merging them interleaves their backups
    /// into a single timeline.
    /// </summary>
    [AzuriteFact]
    public async Task AnInstanceLevelIsReadWhenThePatternSaysThereIsOne()
    {
        var container = await Azurite.NewContainerAsync();
        await Azurite.PutAsync(container, "FULL/SRV01/PROD/MyDb/MyDb_20260801_220000.bak");
        await Azurite.PutAsync(container, "FULL/SRV01/TEST/MyDb/MyDb_20260801_230000.bak");

        var config = Azurite.ConfigFor(
            container, "{BackupType}/{ServerName}/{InstanceName}/{DatabaseName}/{FileName}");

        var files = await Service().ListBackupFilesAsync(config);

        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.InferredInstanceName == "PROD");
        Assert.Contains(files, f => f.InferredInstanceName == "TEST");
        Assert.All(files, f => Assert.Equal("SRV01", f.InferredServerName));
    }

    // ── striping ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A striped backup is several blobs and ONE restorable set. Restoring three of four stripes
    /// fails, so the grouping is not cosmetic.
    /// </summary>
    [AzuriteFact]
    public async Task AStripedBackupIsOneSetAcrossSeveralBlobs()
    {
        var container = await Azurite.NewContainerAsync();
        for (var i = 1; i <= 4; i++)
            await Azurite.PutAsync(container, $"FULL/SRV01/MyDb/MyDb_20260801_220000_{i}.bak", 1_000_000);

        var service = Service();
        var files = await service.ListBackupFilesAsync(Azurite.ConfigFor(container));
        var sets = service.GroupIntoBackupSets(files);

        Assert.Equal(4, files.Count);

        var set = Assert.Single(sets);
        Assert.Equal(4, set.FileCount);
        Assert.True(set.IsStriped);
        Assert.Equal(4_000_000, set.TotalSizeBytes);
    }

    /// <summary>
    /// Two servers writing a same-named database to one container in the same second must not
    /// collapse into a single "striped" set - that would generate one RESTORE spanning both and
    /// silently drop the second server's backup from its own timeline.
    /// </summary>
    [AzuriteFact]
    public async Task TwoServersBackingUpAtTheSameInstantStayApart()
    {
        var container = await Azurite.NewContainerAsync();
        await Azurite.PutAsync(container, "FULL/SRV01/Sales/Sales_20260801_220000.bak");
        await Azurite.PutAsync(container, "FULL/SRV02/Sales/Sales_20260801_220000.bak");

        var service = Service();
        var sets = service.GroupIntoBackupSets(
            await service.ListBackupFilesAsync(Azurite.ConfigFor(container)));

        Assert.Equal(2, sets.Count);
        Assert.All(sets, s => Assert.Equal(1, s.FileCount));
    }

    // ── availability groups ─────────────────────────────────────────────────────

    /// <summary>
    /// Ola's default AG naming is flat - no folders at all - and everything about the backup is in
    /// the filename. A container in that shape has no path structure for the pattern to read.
    /// </summary>
    [AzuriteFact]
    public async Task OlaFlatAgNamingIsReadFromTheFilename()
    {
        var container = await Azurite.NewContainerAsync();
        // The trailing _1 is the file number, and Ola always writes one - a name without it is not
        // this layout at all. Left off first time round, and the test failed for exactly that
        // reason, which is the sort of thing hand-built objects let you get away with.
        await Azurite.PutAsync(container, "CLUSTER01$AG01_MyDb_FULL_20260801_220000_1.bak");

        var config = Azurite.ConfigFor(container);
        config.BackupSourceType = BackupSourceType.AvailabilityGroup;

        var file = Assert.Single(await Service().ListBackupFilesAsync(config));

        Assert.Equal(BackupType.Full, file.Type);
        Assert.Equal("MyDb", file.InferredDatabaseName);
        Assert.True(file.IsAgDefaultNaming);
    }

    // ── what the discovery lists offer ──────────────────────────────────────────

    /// <summary>
    /// The server and database dropdowns are built from what the listing found, so a container the
    /// app cannot read produces a screen with nothing to choose.
    /// </summary>
    [AzuriteFact]
    public async Task TheDiscoveredServersAndDatabasesComeFromWhatIsActuallyThere()
    {
        var container = await Azurite.NewContainerAsync();
        await Azurite.PutAsync(container, "FULL/SRV01/Sales/Sales_20260801_220000.bak");
        await Azurite.PutAsync(container, "FULL/SRV01/Payroll/Payroll_20260801_220000.bak");
        await Azurite.PutAsync(container, "FULL/SRV02/Sales/Sales_20260801_220000.bak");

        var service = Service();
        var files = await service.ListBackupFilesAsync(Azurite.ConfigFor(container));

        Assert.Equal(["SRV01", "SRV02"], service.GetDiscoveredServers(files).Order());
        Assert.Equal(["Payroll", "Sales"], service.GetDiscoveredDatabases(files).Order());
    }

    /// <summary>
    /// A copy-only full is a valid restore point and can anchor a log chain, but can never be a
    /// differential's base - so recognising the marker in a real filename matters (#49).
    /// </summary>
    [AzuriteFact]
    public async Task ACopyOnlyMarkerInTheFilenameIsRecognised()
    {
        var container = await Azurite.NewContainerAsync();
        await Azurite.PutAsync(container, "FULL/SRV01/MyDb/MyDb_COPY_ONLY_20260801_220000.bak");
        await Azurite.PutAsync(container, "FULL/SRV01/MyDb/MyDb_20260801_230000.bak");

        var files = await Service().ListBackupFilesAsync(Azurite.ConfigFor(container));

        Assert.True(Find(files, "MyDb_COPY_ONLY_20260801_220000.bak").IsCopyOnly);
        Assert.False(Find(files, "MyDb_20260801_230000.bak").IsCopyOnly);
    }

    // ── an empty container is not an error ──────────────────────────────────────

    [AzuriteFact]
    public async Task AnEmptyContainerListsNothingRatherThanFailing()
    {
        var container = await Azurite.NewContainerAsync();

        Assert.Empty(await Service().ListBackupFilesAsync(Azurite.ConfigFor(container)));
    }

    /// <summary>
    /// A container holds things that are not backups. They must not stop the listing, and must not
    /// be offered as restore points.
    /// </summary>
    [AzuriteFact]
    public async Task FilesThatAreNotBackupsDoNotBreakTheListing()
    {
        var container = await Azurite.NewContainerAsync();
        await Azurite.PutAsync(container, "FULL/SRV01/MyDb/MyDb_20260801_220000.bak");
        await Azurite.PutAsync(container, "readme.txt");
        await Azurite.PutAsync(container, "FULL/SRV01/MyDb/notes.txt");

        var service = Service();
        var files = await service.ListBackupFilesAsync(Azurite.ConfigFor(container));
        var sets = service.GroupIntoBackupSets(files);

        Assert.Contains(files, f => f.BlobName.EndsWith(".bak", StringComparison.Ordinal));
        Assert.Contains(sets, s => s.Type == BackupType.Full);
    }

    private static BackupFileInfo Find(IEnumerable<BackupFileInfo> files, string endingWith)
        => files.Single(f => f.BlobName.EndsWith(endingWith, StringComparison.Ordinal));
}

/// <summary>
/// A credential store that holds nothing.
///
/// These tests hand the SAS token over on the config itself, so nothing should reach the real
/// Windows Credential Manager - a test that writes to the profile of whoever ran it is the side
/// effect #41 was about, and it has bitten this repo twice this week already.
/// </summary>
internal sealed class NoCredentialStore : ICredentialStore
{
    public AppConfig LoadConfig() => new();
    public void SaveConfig(AppConfig config) { }

    public void SaveSecret(string key, string username, string secret) { }
    public (string? username, string? secret) ReadSecret(string key) => (null, null);
    public bool DeleteSecret(string key) => false;
    public List<string> ListCredentialKeys(string prefix) => [];

    public void SaveSasToken(BlobContainerConfig config, string sasToken) { }
    public string? GetSasToken(BlobContainerConfig config) => config.UnsavedSasToken;
    public bool IsSasTokenExpired(BlobContainerConfig config) => false;
    public DateTime? GetSasTokenExpiry(BlobContainerConfig config) => null;
    public SasExpiryInfo ReadSasTokenExpiry(BlobContainerConfig config)
        => config.ReadSasExpiry(config.UnsavedSasToken);

    public void SaveSqlPassword(ServerConnection connection, string password) { }
    public string? GetSqlPassword(ServerConnection connection) => connection.UnsavedPassword;
}

/// <summary>
/// One container per test, but one emulator for the run - so the tests do not race each other over
/// a shared service that starts slowly.
/// </summary>
[CollectionDefinition("Azurite")]
public sealed class AzuriteCollection;
