using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// An s3:// container is just another endpoint (#51): the URL's own scheme picks the
/// provider, the key pair rides the SAS pipeline as one AccessKeyId:SecretKey string, the
/// credential statement branches to the S3 identity, and the generators emit the S3
/// connector's rules (FORMAT not INIT, optional region JSON).
/// </summary>
public class S3StatementTests
{
    // ── the container knows itself from its URL ─────────────────────────────────

    [Theory]
    [InlineData("s3://s3.eu-west-2.amazonaws.com/backups", true)]
    [InlineData("S3://bucket.s3.amazonaws.com/x", true)]
    [InlineData("https://acct.blob.core.windows.net/backups", false)]
    public void TheSchemeDecidesTheProvider(string url, bool isS3)
    {
        Assert.Equal(isS3, new BlobContainerConfig { ContainerUrl = url }.IsS3);
    }

    // ── the key pair validation (#51) ───────────────────────────────────────────

    [Theory]
    [InlineData("AKIAEXAMPLE:abc/def+secret", null)]
    [InlineData("", "AccessKeyId:SecretKey")]
    [InlineData("nokeys", "colon separates")]
    [InlineData(":secretonly", "AccessKeyId")]
    [InlineData("keyonly:", "secret key is missing")]
    [InlineData("key:sec:ret", "cannot contain a colon")]
    public void TheKeyPairShapeIsEnforced(string pair, string? expectFragment)
    {
        var result = S3Credentials.Validate(pair);
        if (expectFragment == null) Assert.Null(result);
        else Assert.Contains(expectFragment, result);
    }

    [Fact]
    public void SplitReturnsTheTwoHalvesOfAValidPair()
    {
        var split = S3Credentials.Split("AKIA123:the/secret+value");

        Assert.NotNull(split);
        Assert.Equal("AKIA123", split!.Value.KeyId);
        Assert.Equal("the/secret+value", split.Value.Secret);
        Assert.Null(S3Credentials.Split("nope"));
    }

    // ── the credential statement (#51) ──────────────────────────────────────────

    [Fact]
    public void TheS3CredentialUsesTheS3AccessKeyIdentityAndTheSecretAsIs()
    {
        var sql = BlobCredentialStatement.Build(
            "s3://s3.eu-west-2.amazonaws.com/backups",
            BlobCredentialIdentity.S3AccessKey, "AKIA123:se/cret+", exists: false);

        Assert.Contains("CREATE CREDENTIAL [s3://s3.eu-west-2.amazonaws.com/backups]", sql);
        Assert.Contains("IDENTITY = 'S3 Access Key'", sql);
        Assert.Contains("SECRET = 'AKIA123:se/cret+'", sql);   // no '?' trimming, unlike SAS
    }

    [Fact]
    public void AnExistingS3CredentialIsAltered()
    {
        var sql = BlobCredentialStatement.Build(
            "s3://s3.amazonaws.com/b", BlobCredentialIdentity.S3AccessKey, "a:b", exists: true);

        Assert.StartsWith("ALTER CREDENTIAL", sql);
    }

    [Fact]
    public void AnS3CredentialCanAuthenticateARestore()
    {
        Assert.True(new BlobCredentialStatus(BlobCredentialIdentity.S3AccessKey, "S3 Access Key")
            .CanRestoreFromUrl);
    }

    // ── the backup generator's S3 rules (#51) ───────────────────────────────────

    private static BackupOptions S3Backup(string? region = null) => new()
    {
        DatabaseName = "Sales",
        Medium = BackupMedium.AzureBlob,
        Destinations = ["s3://s3.eu-west-2.amazonaws.com/backups/Sales_FULL_20260811.bak"],
        Type = BackupType.Full,
        S3Region = region
    };

    [Fact]
    public void AnS3BackupOverwritesWithFormatBecauseAppendingDoesNotExist()
    {
        var script = new BackupScriptGenerator().Generate(S3Backup());

        Assert.Contains("FORMAT", script);
        // INIT would be rejected by the S3 connector - it must not be emitted.
        Assert.DoesNotContain("INIT", script.Split("WITH")[1]);
    }

    [Fact]
    public void AnAzureBackupStillUsesInit()
    {
        var script = new BackupScriptGenerator().Generate(new BackupOptions
        {
            DatabaseName = "Sales",
            Medium = BackupMedium.AzureBlob,
            Destinations = ["https://acct.blob.core.windows.net/backups/Sales.bak"],
            Type = BackupType.Full
        });

        Assert.Contains("INIT", script);
    }

    [Fact]
    public void TheBackupRegionRidesAsBackupOptionsJsonAndOnlyForS3()
    {
        var withRegion = new BackupScriptGenerator().Generate(S3Backup("eu-west-2"));
        Assert.Contains("BACKUP_OPTIONS = '{\"s3\": {\"region\":\"eu-west-2\"}}'", withRegion);

        // A region set on a non-S3 destination is cleared, never emitted.
        var azure = new BackupScriptGenerator().Generate(new BackupOptions
        {
            DatabaseName = "Sales",
            Destinations = ["https://acct.blob.core.windows.net/backups/Sales.bak"],
            S3Region = "eu-west-2"
        });
        Assert.DoesNotContain("BACKUP_OPTIONS", azure);
    }

    // ── the restore generator's region, on every statement (#51) ────────────────

    private static BackupChain S3Chain()
    {
        BackupFileInfo File(string name, BackupType type) => new()
        {
            BlobName = name,
            BlobUrl = $"s3://s3.eu-west-2.amazonaws.com/backups/{name}",
            Type = type
        };
        return new BackupChain
        {
            FullSet = new BackupSet
            {
                DatabaseName = "Sales", Type = BackupType.Full,
                Timestamp = new DateTime(2026, 8, 1, 22, 0, 0),
                Files = [File("Sales_FULL.bak", BackupType.Full)]
            },
            LogSets =
            [
                new BackupSet
                {
                    DatabaseName = "Sales", Type = BackupType.TransactionLog,
                    Timestamp = new DateTime(2026, 8, 1, 23, 0, 0),
                    Files = [File("Sales_LOG.trn", BackupType.TransactionLog)]
                }
            ]
        };
    }

    [Fact]
    public void TheRestoreRegionAppearsOnEveryStatementOfTheChain()
    {
        var script = new RestoreScriptGenerator().Generate(S3Chain(), new RestoreOptions
        {
            TargetDatabaseName = "Sales",
            S3Region = "eu-west-2"
        });

        // One on the FULL, one on the LOG.
        var count = script.Split("RESTORE_OPTIONS = '{\"s3\": {\"region\":\"eu-west-2\"}}'").Length - 1;
        Assert.Equal(2, count);
    }

    [Fact]
    public void ANonS3ChainNeverEmitsARegionEvenIfOneIsSet()
    {
        var azureChain = new BackupChain
        {
            FullSet = new BackupSet
            {
                DatabaseName = "Sales", Type = BackupType.Full,
                Timestamp = new DateTime(2026, 8, 1, 22, 0, 0),
                Files = [new BackupFileInfo
                {
                    BlobName = "Sales.bak",
                    BlobUrl = "https://acct.blob.core.windows.net/backups/Sales.bak",
                    Type = BackupType.Full
                }]
            }
        };

        var script = new RestoreScriptGenerator().Generate(azureChain, new RestoreOptions
        {
            TargetDatabaseName = "Sales",
            S3Region = "eu-west-2"
        });

        Assert.DoesNotContain("RESTORE_OPTIONS", script);
    }
}
