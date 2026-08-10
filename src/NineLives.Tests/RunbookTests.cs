using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The DR runbook (#240): one self-contained document with everything the worst day needs, in
/// the order the worst day needs it. Pure and offline by design - it states what to verify
/// rather than reaching out to verify it, because it may be generated on a quiet Tuesday and
/// executed the night the source server no longer answers.
/// </summary>
public class RunbookTests
{
    private static readonly DateTime T0 = new(2026, 8, 10, 14, 0, 0);

    private static BackupFileInfo File_(string device, long size = 0, byte[]? tde = null) => new()
    {
        BlobName = device,
        BlobUrl = device.StartsWith("http") ? device : "",
        LocalPath = device.StartsWith("http") ? null : device,
        SizeBytes = size,
        TdeThumbprint = tde
    };

    private static RunbookInputs Inputs(byte[]? tde = null, bool onDisk = false) => new()
    {
        Chain = new BackupChain
        {
            FullSet = new BackupSet
            {
                SetId = "full",
                Type = BackupType.Full,
                Timestamp = T0.AddHours(-10),
                Files = [File_(onDisk
                    ? @"\\nas01\backups\MyDb_full.bak"
                    : "https://acct.blob.core.windows.net/backups/MyDb_full.bak", 1024 * 1024, tde)]
            },
            LogSets =
            [
                new BackupSet
                {
                    SetId = "log1",
                    Type = BackupType.TransactionLog,
                    Timestamp = T0.AddHours(-2),
                    Files = [File_(onDisk
                        ? @"\\nas01\backups\MyDb_log1.trn"
                        : "https://acct.blob.core.windows.net/backups/MyDb_log1.trn")]
                }
            ]
        },
        Script = "RESTORE DATABASE [MyDb] FROM URL = N'https://...' WITH REPLACE, NORECOVERY;",
        TargetDatabase = "MyDb",
        ServerName = "SRV01",
        ContainerName = "backups",
        SourceDatabase = "MyDb",
        RestorePoint = T0.AddHours(-1),
        GeneratedAt = T0
    };

    [Fact]
    public void TheSectionsArriveInTheOrderTheWorstDayNeeds()
    {
        var runbook = RunbookBuilder.Build(Inputs());

        var files = runbook.IndexOf("## 1. The backups", StringComparison.Ordinal);
        var prereq = runbook.IndexOf("## 2. Before the restore", StringComparison.Ordinal);
        var script = runbook.IndexOf("## 3. The restore script", StringComparison.Ordinal);
        var failure = runbook.IndexOf("## 4. If it stops", StringComparison.Ordinal);
        var finish = runbook.IndexOf("## 5. Finishing the job", StringComparison.Ordinal);

        Assert.True(0 < files && files < prereq && prereq < script && script < failure && failure < finish,
            runbook[..200]);
    }

    [Fact]
    public void EveryFileIsListedAndTheScriptIsVerbatim()
    {
        var runbook = RunbookBuilder.Build(Inputs());

        Assert.Contains("MyDb_full.bak", runbook);
        Assert.Contains("MyDb_log1.trn", runbook);
        Assert.Contains("Log 1 of 1", runbook);
        Assert.Contains("RESTORE DATABASE [MyDb] FROM URL", runbook);
    }

    /// <summary>Blob chains lead with the credential; disk chains with service-account readability.</summary>
    [Fact]
    public void ThePrerequisitesMatchTheMedium()
    {
        Assert.Contains("server-side credential", RunbookBuilder.Build(Inputs()));
        Assert.Contains("SERVICE ACCOUNT", RunbookBuilder.Build(Inputs(onDisk: true)));
    }

    /// <summary>
    /// When the chain knows it is TDE, the certificate is a numbered prerequisite with the
    /// thumbprint spelled out - the 33111 that ends DR attempts, preempted on paper.
    /// </summary>
    [Fact]
    public void ATdeChainMakesTheCertificateAPrerequisite()
    {
        var runbook = RunbookBuilder.Build(Inputs(tde: Convert.FromHexString("AABBCC")));

        Assert.Contains("TDE-encrypted", runbook);
        Assert.Contains("0xAABBCC", runbook);
        Assert.Contains("BACKUP CERTIFICATE", runbook);
        // The password never travels in a document that lives in tickets and repos.
        Assert.Contains("belong in the DR kit, not in this document", runbook);
    }

    [Fact]
    public void TheFailurePathAndTheFinishAreWrittenDown()
    {
        var runbook = RunbookBuilder.Build(Inputs());

        Assert.Contains("WITH RECOVERY;", runbook);
        Assert.Contains("SET MULTI_USER;", runbook);
        Assert.Contains("DBCC CHECKDB", runbook);
        Assert.Contains("ALTER USER", runbook);
        Assert.Contains("stale runbook is a wrong runbook", runbook);
    }
}
