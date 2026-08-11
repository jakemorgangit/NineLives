using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Half a backup, reported as a whole one (#363, #365).
///
/// Both checks bailed exactly where the damage was worst. The stripe check returned early on a
/// single-file set, so a set holding only <c>..._2.bak</c> - stripe 1 purged or never uploaded -
/// passed clean, while the SAME set as stripes 2 and 3 was correctly flagged. And a set with no
/// files at all passed every check there was, then generated a RESTORE naming no device, which is
/// the valid recovery-only form: against a target sitting in RESTORING it ends the restore
/// sequence permanently.
/// </summary>
public class PartialMediaSetTests
{
    private readonly BackupChainValidator _validator = new();
    private readonly RestoreScriptGenerator _generator = new();

    private static readonly DateTime T0 = new(2026, 8, 4, 22, 0, 0);

    /// <summary>A set whose files are named exactly as given, under an optional folder.</summary>
    private static BackupSet Set(
        BackupType type, DateTime timestamp, string[] fileNames,
        string folder = "backups", string database = "Sales", string server = "SRV01") => new()
        {
            SetId = timestamp.ToString("yyyyMMdd_HHmmss"),
            Type = type,
            Timestamp = timestamp,
            DatabaseName = database,
            ServerName = server,
            Files = fileNames.Select(n => new BackupFileInfo
            {
                BlobName = folder.Length == 0 ? n : $"{folder}/{n}",
                BlobUrl = $"https://acct.blob.core.windows.net/c/{folder}/{n}",
                Type = type,
                SizeBytes = 1024
            }).ToList()
        };

    private static BackupChain Chain(BackupSet full, BackupSet[]? logs = null) => new()
    {
        FullSet = full,
        LogSets = logs?.ToList() ?? []
    };

    private static ChainIssue? ErrorAbout(IEnumerable<ChainIssue> issues, string fragment) =>
        issues.FirstOrDefault(i => i.IsError && (i.Title.Contains(fragment) || i.Detail.Contains(fragment)));

    // ── a lone stripe is a missing stripe ───────────────────────────────────────

    /// <summary>
    /// The case the old guard was blind to: one file, numbered 2. There is no stripe 1, and the
    /// number says so on its own - no file count needed.
    /// </summary>
    [Fact]
    public void ASetHoldingOnlyStripeTwoIsMissingStripeOne()
    {
        var issues = _validator.Validate(
            Chain(Set(BackupType.Full, T0, ["20260804_220000_2.bak"])));

        var issue = ErrorAbout(issues, "missing stripe");
        Assert.NotNull(issue);
        Assert.Contains("1", issue!.Title);
        Assert.Contains("3132", issue.Detail);
    }

    /// <summary>The same set as stripes 2 and 3 was always caught; it still is.</summary>
    [Fact]
    public void ASetHoldingStripesTwoAndThreeIsStillCaught()
    {
        var issues = _validator.Validate(Chain(
            Set(BackupType.Full, T0, ["20260804_220000_2.bak", "20260804_220000_3.bak"])));

        Assert.NotNull(ErrorAbout(issues, "missing stripe"));
    }

    /// <summary>
    /// An unnumbered file beside a numbered one. The old check bailed because the counts
    /// disagreed - which is the signal, not a reason to stop looking.
    /// </summary>
    [Fact]
    public void ASetMixingAnUnnumberedFileWithStripeThreeIsRefusedAndNamesIt()
    {
        var issues = _validator.Validate(Chain(
            Set(BackupType.Full, T0, ["20260804_220000.bak", "20260804_220000_3.bak"])));

        var issue = ErrorAbout(issues, "missing stripe");
        Assert.NotNull(issue);
        Assert.Contains("20260804_220000.bak", issue!.Detail);
        Assert.Contains("no stripe number", issue.Detail);
    }

    // ── and the shapes that must stay quiet ─────────────────────────────────────

    /// <summary>An ordinary unstriped backup. No number, nothing to conclude.</summary>
    [Fact]
    public void AnOrdinaryUnstripedBackupRaisesNothing()
    {
        var issues = _validator.Validate(Chain(Set(BackupType.Full, T0, ["20260804_220000.bak"])));
        Assert.Null(ErrorAbout(issues, "missing stripe"));
    }

    /// <summary>
    /// A single file numbered 1. Some writers number even a one-file set, and 1..1 has no hole -
    /// the check must not read the number itself as evidence of a sibling.
    /// </summary>
    [Fact]
    public void ASingleFileNumberedOneRaisesNothing()
    {
        var issues = _validator.Validate(Chain(Set(BackupType.Full, T0, ["20260804_220000_1.bak"])));
        Assert.Null(ErrorAbout(issues, "missing stripe"));
    }

    [Fact]
    public void ACompleteStripedSetRaisesNothing()
    {
        var issues = _validator.Validate(Chain(Set(BackupType.Full, T0,
            ["20260804_220000_1.bak", "20260804_220000_2.bak", "20260804_220000_3.bak"])));

        Assert.Null(ErrorAbout(issues, "missing stripe"));
    }

    // ── one media set, two folders ──────────────────────────────────────────────

    /// <summary>
    /// Multi-directory striping, which is how a large backup gets spread over two volumes. Sets
    /// group by parent folder, so this arrives as two sets at the same instant, each holding half
    /// a media set and each looking complete on its own.
    /// </summary>
    [Fact]
    public void StripesSplitAcrossTwoFoldersAreOneSetAndRefused()
    {
        var issues = _validator.ValidateInventory(
        [
            Set(BackupType.Full, T0, ["20260804_220000_1.bak"], folder: "vol-a"),
            Set(BackupType.Full, T0, ["20260804_220000_2.bak"], folder: "vol-b")
        ]);

        var issue = ErrorAbout(issues, "split across");
        Assert.NotNull(issue);
        Assert.Contains("vol-a", issue!.Detail);
        Assert.Contains("vol-b", issue.Detail);
    }

    /// <summary>
    /// Two complete backups that happen to share a second are NOT one split set - each contains a
    /// stripe 1, so neither is half of anything, and each restores. Overlapping numbers are what
    /// tells the two cases apart, and calling this an Error would disable a restore that works.
    /// </summary>
    [Fact]
    public void TwoCompleteBackupsSharingATimestampAreAWarningNotAnError()
    {
        var issues = _validator.ValidateInventory(
        [
            Set(BackupType.Full, T0, ["20260804_220000_1.bak"], folder: "vol-a"),
            Set(BackupType.Full, T0, ["20260804_220000_1.bak"], folder: "vol-b")
        ]);

        Assert.Null(ErrorAbout(issues, "split across"));

        var warning = issues.FirstOrDefault(i => !i.IsError && i.Title.Contains("same timestamp"));
        Assert.NotNull(warning);
        Assert.Contains("halves of one striped set", warning!.Detail);
    }

    /// <summary>Different databases at the same instant are just two backup jobs.</summary>
    [Fact]
    public void TwoDatabasesBackedUpInTheSameSecondRaiseNothing()
    {
        var issues = _validator.ValidateInventory(
        [
            Set(BackupType.Full, T0, ["20260804_220000_1.bak"], folder: "a", database: "Sales"),
            Set(BackupType.Full, T0, ["20260804_220000_2.bak"], folder: "b", database: "Payroll")
        ]);

        Assert.Null(ErrorAbout(issues, "split across"));
        Assert.DoesNotContain(issues, i => i.Title.Contains("same timestamp"));
    }

    // ── a set with no files at all ──────────────────────────────────────────────

    [Fact]
    public void ASetWithNoFilesIsAnError()
    {
        var empty = Set(BackupType.Full, T0, []);
        var issue = ErrorAbout(_validator.Validate(Chain(empty)), "has no files");

        Assert.NotNull(issue);
        Assert.Contains("recovery-only", issue!.Detail);
    }

    /// <summary>
    /// The one that matters. A statement with no FROM is not a syntax error - it is the valid
    /// recovery-only form, so nothing would have failed at run time. It would have recovered a
    /// database mid-chain and ended the restore sequence for good.
    /// </summary>
    [Fact]
    public void GeneratingFromASetWithNoFilesIsRefusedRatherThanEmitted()
    {
        var chain = Chain(Set(BackupType.Full, T0, []));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _generator.Generate(chain, new RestoreOptions
            {
                TargetDatabaseName = "Sales",
                RecoveryMode = RecoveryMode.Recovery
            }));

        Assert.Contains("no files", ex.Message);
        Assert.Contains("recovery-only", ex.Message);
    }

    /// <summary>An empty set anywhere in the chain, not only the full.</summary>
    [Fact]
    public void AnEmptyLogSetIsCaughtToo()
    {
        var chain = Chain(
            Set(BackupType.Full, T0, ["20260804_220000.bak"]),
            [Set(BackupType.TransactionLog, T0.AddHours(1), [])]);

        Assert.NotNull(RestoreScriptGenerator.DescribeSetWithNoFiles(chain));
        Assert.NotNull(ErrorAbout(_validator.Validate(chain), "has no files"));
    }

    [Fact]
    public void AChainWhoseSetsAllHaveFilesIsNotRefused()
    {
        var chain = Chain(
            Set(BackupType.Full, T0, ["20260804_220000.bak"]),
            [Set(BackupType.TransactionLog, T0.AddHours(1), ["20260804_230000.trn"])]);

        Assert.Null(RestoreScriptGenerator.DescribeSetWithNoFiles(chain));

        var script = _generator.Generate(chain, new RestoreOptions
        {
            TargetDatabaseName = "Sales",
            RecoveryMode = RecoveryMode.Recovery
        });

        Assert.Contains("RESTORE DATABASE", script);
        Assert.Contains("RESTORE LOG", script);
    }
}
