using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The script that brings missing backups into the container (#451).
///
/// It runs on the source machine, so it is handed over rather than executed here - which makes
/// what it contains a security question as much as a correctness one.
/// </summary>
public class MissingBackupCopyScriptTests
{
    private static readonly DateTime T0 = new(2026, 8, 14, 22, 0, 0);

    private static MissingLocation Location(string folder = @"E:\SQLLogs", int count = 3)
    {
        var backups = Enumerable.Range(1, count).Select(i =>
        {
            var at = T0.AddHours(i);
            return new MissingBackup(
                new BackupHistoryEntry
                {
                    DatabaseName = "Sales",
                    Type = BackupType.TransactionLog,
                    StartedAt = at,
                    Files = [$@"{folder}\Sales_{at:yyyyMMdd_HHmmss}.trn"],
                    BackupSizeBytes = 10 * 1024 * 1024
                },
                folder);
        }).ToList();

        return new MissingLocation(folder, backups);
    }

    private static BlobContainerConfig Azure() => new()
    {
        Id = "c1",
        Name = "sqlbackups",
        ContainerUrl = "https://acct.blob.core.windows.net/sqlbackups"
    };

    private static BlobContainerConfig Bucket() => new()
    {
        Id = "c2",
        Name = "dr-bucket",
        ContainerUrl = "s3://s3.eu-west-2.amazonaws.com/dr-bucket/sql",
        S3Region = "eu-west-2"
    };

    // ── the security property ───────────────────────────────────────────────────

    /// <summary>
    /// The one that must never regress. SECURITY.md states outright that a generated script
    /// contains no credential, and this script is the most tempting place to break that: it would
    /// be one string interpolation to embed the SAS, and the result would sit in a .ps1 on a
    /// production server.
    /// </summary>
    [Fact]
    public void TheAzureScriptTakesTheCredentialAsAParameterAndCarriesNone()
    {
        var script = MissingBackupCopyScript.Build(Location(), Azure());

        Assert.Contains("[Parameter(Mandatory = $true)]", script);
        Assert.Contains("$Sas", script);

        // Nothing that looks like a token, and no query string on the container URL.
        Assert.DoesNotContain("sig=", script);
        Assert.DoesNotContain("?sv=", script);
    }

    [Fact]
    public void TheS3ScriptTakesTheKeyPairAsParametersAndCarriesNone()
    {
        var script = MissingBackupCopyScript.Build(Location(), Bucket());

        Assert.Contains("$AccessKeyId", script);
        Assert.Contains("$SecretAccessKey", script);

        // Set for the process, not written anywhere.
        Assert.Contains("$env:AWS_ACCESS_KEY_ID = $AccessKeyId", script);
        Assert.DoesNotContain("aws configure", script);
    }

    // ── what it copies ──────────────────────────────────────────────────────────

    /// <summary>
    /// Named individually. A wildcard over the folder would take other databases' backups, an
    /// unrelated job's output, and whatever is half-written at that moment - a surprise, and on a
    /// metered egress link a bill.
    /// </summary>
    [Fact]
    public void EveryFileIsNamedAndNoWildcardIsUsed()
    {
        var script = MissingBackupCopyScript.Build(Location(count: 3), Azure());

        Assert.Contains(@"'E:\SQLLogs\Sales_20260814_230000.trn'", script);
        Assert.Contains(@"'E:\SQLLogs\Sales_20260815_000000.trn'", script);
        Assert.Contains(@"'E:\SQLLogs\Sales_20260815_010000.trn'", script);

        Assert.DoesNotContain("*.trn", script);
        Assert.DoesNotContain("*.bak", script);
    }

    [Fact]
    public void TheHeaderSaysWhatIsBeingCopiedAndFromWhere()
    {
        var script = MissingBackupCopyScript.Build(Location(count: 3), Azure());

        Assert.Contains("3 log backups", script);
        Assert.Contains(@"E:\SQLLogs", script);
        Assert.Contains("30.0 MB", script);
    }

    [Fact]
    public void TheS3ScriptTargetsTheBucketAndBasePathFromTheContainerUrl()
    {
        var script = MissingBackupCopyScript.Build(Location(), Bucket());

        Assert.Contains("$bucket = 'dr-bucket'", script);
        Assert.Contains("$prefix = 'sql/'", script);
        Assert.Contains("$endpoint = 'https://s3.eu-west-2.amazonaws.com'", script);
        Assert.Contains("$env:AWS_DEFAULT_REGION = 'eu-west-2'", script);
    }

    // ── what it does when things are not as expected ────────────────────────────

    /// <summary>
    /// A file that has been purged since the history was read is reported, not fatal. Retention
    /// running between the scan and the copy is entirely ordinary, and stopping dead on the first
    /// missing one would leave the rest uncopied for no reason.
    /// </summary>
    [Fact]
    public void AFileThatHasGoneIsReportedAndTheRestStillCopy()
    {
        var script = MissingBackupCopyScript.Build(Location(), Azure());

        Assert.Contains("Test-Path -LiteralPath $file", script);
        Assert.Contains("Not on disk any more", script);
        Assert.Contains("continue", script);
        Assert.Contains("exit 1", script);
    }

    [Fact]
    public void ItEndsByPointingBackAtTheRescan()
    {
        var script = MissingBackupCopyScript.Build(Location(), Azure());
        Assert.Contains("Rescan", script);
    }

    /// <summary>
    /// A path holding an apostrophe would otherwise close the PowerShell literal and turn the rest
    /// of the line into commands - the same class of hole as an unescaped SQL literal.
    /// </summary>
    [Fact]
    public void AnApostropheInAPathIsEscaped()
    {
        var folder = @"E:\Jake's Logs";
        var location = new MissingLocation(folder,
        [
            new MissingBackup(
                new BackupHistoryEntry
                {
                    DatabaseName = "Sales",
                    Type = BackupType.TransactionLog,
                    StartedAt = T0,
                    Files = [$@"{folder}\Sales.trn"]
                },
                folder)
        ]);

        var script = MissingBackupCopyScript.Build(location, Azure());

        Assert.Contains(@"'E:\Jake''s Logs\Sales.trn'", script);
    }
}
