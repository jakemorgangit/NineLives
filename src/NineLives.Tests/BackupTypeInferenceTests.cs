using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Filename-based backup type inference (#45, #44).
///
/// This is the last resort, used when the path structure did not say what a file is. Getting it
/// wrong is not a cosmetic mislabel:
///
///   - a full read as a differential never enters the fulls collection in BackupChainBuilder, so
///     if it is the only full in the container that database gets no restore points at all (#45)
///   - a log read as a full becomes a chain root, so the timeline offers a log file as a
///     restorable Full point and drops every earlier log from the chain (#44)
/// </summary>
public class BackupTypeInferenceTests
{
    // ── #45: the substring match, live on the default path ──────────────────────

    [Theory]
    [InlineData("DiffusionDb_FULL_20260804_220000.bak")]
    [InlineData("TrafficDiffusion_20260804_220000.bak")]
    [InlineData("DiffEngineDb_20260804_220000.bak")]
    [InlineData("diffusion_20260804.bak")]
    public void ADatabaseWhoseNameContainsDiffIsStillAFullBackup(string fileName)
    {
        Assert.Equal(BackupType.Full, BlobStorageService.InferBackupTypeFromExtension(fileName));
    }

    [Theory]
    [InlineData("MyDb_DIFF_20260804_220000.bak")]
    [InlineData("MyDb-diff-20260804.bak")]
    [InlineData("MyDb.DIFF.20260804.bak")]
    [InlineData("MyDb_DIFFERENTIAL_20260804.bak")]
    [InlineData("MyDb_20260804.diff")]
    public void ARealDifferentialMarkerIsStillDetected(string fileName)
    {
        Assert.Equal(BackupType.Differential, BlobStorageService.InferBackupTypeFromExtension(fileName));
    }

    // ── #44: a log written as .bak ──────────────────────────────────────────────

    [Theory]
    [InlineData("MyDb_LOG_20260804_120000.bak")]
    [InlineData("MyDb-log-20260804.bak")]
    [InlineData("MyDb_TLOG_20260804.bak")]
    [InlineData("MyDb_TRN_20260804.bak")]
    [InlineData("MyDb_TRANSACTIONLOG_20260804.bak")]
    public void ALogWrittenAsBakIsNotAFullBackup(string fileName)
    {
        Assert.Equal(BackupType.TransactionLog, BlobStorageService.InferBackupTypeFromExtension(fileName));
    }

    /// <summary>
    /// The trap the issue calls out. A bare "log" substring would retype all of these, which is
    /// exactly the mistake ContainsDiffIndicator was making with "diff".
    /// </summary>
    [Theory]
    [InlineData("CatalogDb_FULL_20260804_220000.bak")]
    [InlineData("BlogDb_20260804.bak")]
    [InlineData("DialogDb_FULL_20260804.bak")]
    [InlineData("AnalogData_20260804.bak")]
    [InlineData("Catalog_20260804.bak")]
    public void ADatabaseWhoseNameContainsLogIsStillAFullBackup(string fileName)
    {
        Assert.Equal(BackupType.Full, BlobStorageService.InferBackupTypeFromExtension(fileName));
    }

    // ── extensions still win where they are unambiguous ─────────────────────────

    [Theory]
    [InlineData("MyDb_20260804.trn", BackupType.TransactionLog)]
    [InlineData("MyDb_20260804.log", BackupType.TransactionLog)]
    [InlineData("CatalogDb_20260804.trn", BackupType.TransactionLog)]
    [InlineData("MyDb_20260804.bak", BackupType.Full)]
    [InlineData("MyDb_20260804.bkp", BackupType.Full)]
    [InlineData("MyDb_20260804.txt", BackupType.Unknown)]
    [InlineData("MyDb_20260804", BackupType.Unknown)]
    public void ExtensionDrivesTheAnswerWhenItIsUnambiguous(string fileName, BackupType expected)
    {
        Assert.Equal(expected, BlobStorageService.InferBackupTypeFromExtension(fileName));
    }

    [Fact]
    public void ADifferentialMarkerBeatsALogMarker()
    {
        // Ola-style names can carry both words; DIFF is the more specific claim.
        Assert.Equal(
            BackupType.Differential,
            BlobStorageService.InferBackupTypeFromExtension("MyDb_DIFF_LOG_20260804.bak"));
    }

    // ── folders must not retype files ───────────────────────────────────────────

    [Theory]
    [InlineData("logs/MyDb_FULL_20260804.bak")]
    [InlineData("log/MyDb_FULL_20260804.bak")]
    [InlineData("diff/MyDb_FULL_20260804.bak")]
    [InlineData("backups/LOG/CatalogDb_20260804.bak")]
    public void IndicatorsAreReadFromTheFilenameNotTheFoldersAboveIt(string blobName)
    {
        // Folder-based typing is the primary path's job and runs before this. If this method also
        // looked at the path, a container organised under a "logs/" prefix would have every file
        // in it retyped regardless of what the file actually is.
        Assert.Equal(BackupType.Full, BlobStorageService.InferBackupTypeFromExtension(blobName));
    }

    [Fact]
    public void CaseDoesNotMatter()
    {
        Assert.Equal(BackupType.TransactionLog, BlobStorageService.InferBackupTypeFromExtension("MyDb_log_20260804.BAK"));
        Assert.Equal(BackupType.Differential, BlobStorageService.InferBackupTypeFromExtension("MyDb_Diff_20260804.BAK"));
    }

    // ── the indicator helpers directly ──────────────────────────────────────────

    [Theory]
    [InlineData("MyDb_DIFF_1.bak", true)]
    [InlineData("DiffusionDb_FULL.bak", false)]
    [InlineData("TariffData_FULL.bak", false)]
    [InlineData("diff_20260804.bak", true)]
    public void DiffIndicatorIsDelimited(string fileName, bool expected)
        => Assert.Equal(expected, BlobStorageService.ContainsDiffIndicator(fileName));

    [Theory]
    [InlineData("MyDb_LOG_1.trn", true)]
    [InlineData("CatalogDb_FULL.bak", false)]
    [InlineData("BlogDb.bak", false)]
    [InlineData("MyDb.log.bak", true)]
    public void LogIndicatorIsDelimited(string fileName, bool expected)
        => Assert.Equal(expected, BlobStorageService.ContainsLogIndicator(fileName));
}
