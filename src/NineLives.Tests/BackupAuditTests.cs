using System.IO;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Checking a database's backups against their own headers (#130).
///
/// Belt and braces. Path-and-filename inference is right most of the time and wrong in ways that
/// stay invisible until a restore is needed - a log filed as a full becomes a chain root and drops
/// every earlier log (#44); a full filed as a differential never enters the fulls collection at all
/// (#45). The header is what a RESTORE reads, so it settles both.
///
/// The cache is not an optimisation here. A header read is about 1.7 seconds, measured, so a
/// hundred sets is a few minutes - and nobody runs a few minutes twice. Cached, the second run is
/// instant, which is the difference between a diagnostic and a habit.
/// </summary>
public class BackupAuditTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ninelives-tests", Guid.NewGuid().ToString("n"));

    private BackupAuditStore Store() => new(_directory);

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch { /* a temp directory that will not delete is not a test failure */ }
    }

    private static BackupSet Set(
        BackupType type = BackupType.Full,
        string database = "MyDb",
        string name = "MyDb_FULL_20260801_220000.bak",
        string etag = "\"aaa\"") => new()
    {
        SetId = "MyDb_20260801_220000",
        DatabaseName = database,
        Type = type,
        Timestamp = T0,
        Files =
        [
            new BackupFileInfo
            {
                BlobName = name,
                BlobUrl = $"https://acct.blob.core.windows.net/backups/{name}",
                ETag = etag,
                Type = type,
                InferredDatabaseName = database
            }
        ]
    };

    private static BackupFileInfo Header(BackupType type = BackupType.Full, string database = "MyDb") => new()
    {
        DatabaseName = database,
        Type = type,
        BackupTypeCode = type == BackupType.Full ? 1 : type == BackupType.Differential ? 5 : 2
    };

    private static ServerConnection Server() =>
        new() { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" };

    // ── what it finds ───────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenTheHeaderAgreesNothingIsReported()
    {
        var sql = new FakeSqlServerService { Header = Header() };

        var findings = await new BackupAuditor(sql, Store()).AuditAsync(Server(), [Set()]);

        Assert.Empty(findings);
    }

    /// <summary>
    /// The disagreement that breaks a chain outright rather than misfiling it - #44 and #45 are
    /// both this.
    /// </summary>
    [Fact]
    public async Task ABackupFiledAsTheWrongTypeIsReported()
    {
        var sql = new FakeSqlServerService { Header = Header(BackupType.TransactionLog) };

        var finding = Assert.Single(
            await new BackupAuditor(sql, Store()).AuditAsync(Server(), [Set(BackupType.Full)]));

        Assert.Equal(BackupAuditVerdict.WrongType, finding.Verdict);
        Assert.Contains("Full", finding.PathSaid);
        Assert.Contains("TransactionLog", finding.HeaderSaid);
    }

    /// <summary>
    /// Quieter and arguably worse: the backup is offered on a timeline it does not belong to, and
    /// missing from the one it does.
    /// </summary>
    [Fact]
    public async Task ABackupFiledUnderTheWrongDatabaseIsReported()
    {
        var sql = new FakeSqlServerService { Header = Header(database: "SomethingElse") };

        var finding = Assert.Single(
            await new BackupAuditor(sql, Store()).AuditAsync(Server(), [Set(database: "MyDb")]));

        Assert.Equal(BackupAuditVerdict.WrongDatabase, finding.Verdict);
        Assert.Contains("SomethingElse", finding.Description);
    }

    [Fact]
    public async Task ABackupThatCannotBeReadIsReported()
    {
        var sql = new FakeSqlServerService { Header = null };

        var finding = Assert.Single(
            await new BackupAuditor(sql, Store()).AuditAsync(Server(), [Set()]));

        Assert.Equal(BackupAuditVerdict.Unreadable, finding.Verdict);
    }

    /// <summary>
    /// Findings are REPORTED, not applied. A database full of them almost always means the path
    /// pattern is wrong, and correcting each symptom quietly would hide the one thing worth knowing.
    /// </summary>
    [Fact]
    public async Task AMismatchIsNotSilentlyCorrected()
    {
        var sql = new FakeSqlServerService { Header = Header(BackupType.TransactionLog) };
        var set = Set(BackupType.Full);

        await new BackupAuditor(sql, Store()).AuditAsync(Server(), [set]);

        Assert.Equal(BackupType.Full, set.Type);
    }

    // ── the pill ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFileThatPassedIsMarkedAsAudited()
    {
        var sql = new FakeSqlServerService { Header = Header() };
        var set = Set();

        await new BackupAuditor(sql, Store()).AuditAsync(Server(), [set]);

        Assert.True(set.Files[0].AuditPassed);
    }

    [Fact]
    public async Task AFileThatFailedIsMarkedAsMismatched()
    {
        var sql = new FakeSqlServerService { Header = Header(BackupType.TransactionLog) };
        var set = Set(BackupType.Full);

        await new BackupAuditor(sql, Store()).AuditAsync(Server(), [set]);

        Assert.True(set.Files[0].AuditFailed);
    }

    // ── the cache, which is what makes it usable twice ──────────────────────────

    [Fact]
    public async Task ASecondAuditOfTheSameBackupsReadsNothing()
    {
        var sql = new FakeSqlServerService { Header = Header() };
        var store = Store();

        await new BackupAuditor(sql, store).AuditAsync(Server(), [Set()]);
        Assert.Single(sql.HeaderBatches);

        await new BackupAuditor(sql, store).AuditAsync(Server(), [Set()]);

        Assert.Single(sql.HeaderBatches);
    }

    /// <summary>The pill survives the cache, or a re-audit would be needed just to see it again.</summary>
    [Fact]
    public async Task TheCachedAnswerStillMarksTheFile()
    {
        var sql = new FakeSqlServerService { Header = Header() };
        var store = Store();

        await new BackupAuditor(sql, store).AuditAsync(Server(), [Set()]);

        var second = Set();
        await new BackupAuditor(sql, store).AuditAsync(Server(), [second]);

        Assert.True(second.Files[0].AuditPassed);
    }

    /// <summary>
    /// The reason the key is the ETag. A backup header never changes, so the only reason to read one
    /// twice is that the blob is a different blob - which is exactly when Azure changes the ETag.
    /// </summary>
    [Fact]
    public async Task AFileReplacedUnderTheSameNameIsReadAgain()
    {
        var sql = new FakeSqlServerService { Header = Header() };
        var store = Store();

        await new BackupAuditor(sql, store).AuditAsync(Server(), [Set(etag: "\"aaa\"")]);
        await new BackupAuditor(sql, store).AuditAsync(Server(), [Set(etag: "\"bbb\"")]);

        Assert.Equal(2, sql.HeaderBatches.Count);
    }

    /// <summary>
    /// A failure is cached too. The answer will not have changed, and finding that out costs the
    /// same round trip as the first time.
    /// </summary>
    [Fact]
    public async Task AMismatchIsNotReReadEitherAndIsStillReported()
    {
        var sql = new FakeSqlServerService { Header = Header(BackupType.TransactionLog) };
        var store = Store();

        await new BackupAuditor(sql, store).AuditAsync(Server(), [Set(BackupType.Full)]);

        var findings = await new BackupAuditor(sql, store).AuditAsync(Server(), [Set(BackupType.Full)]);

        Assert.Single(sql.HeaderBatches);
        Assert.Single(findings);
    }

    [Fact]
    public void WithoutAnETagTheKeyStillDistinguishesADifferentFile()
    {
        var one = new BackupFileInfo { BlobName = "a.bak", SizeBytes = 100, LastModified = new DateTimeOffset(T0, TimeSpan.Zero) };
        var two = new BackupFileInfo { BlobName = "a.bak", SizeBytes = 200, LastModified = new DateTimeOffset(T0, TimeSpan.Zero) };

        Assert.NotEqual(BackupAuditStore.KeyFor(one), BackupAuditStore.KeyFor(two));
    }

    /// <summary>An unreadable cache is a slow audit, not an error - and must not be overwritten blindly (#7).</summary>
    [Fact]
    public void AnUnreadableCacheComesBackEmpty()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "audit-cache.json"), "{ this is not json");

        Assert.Empty(Store().Load());
    }

    // ── the estimate ────────────────────────────────────────────────────────────

    /// <summary>
    /// Said BEFORE it starts: a three-minute operation nobody expected reads as a hang.
    /// </summary>
    [Fact]
    public void TheEstimateIsGivenInSecondsForASmallDatabase()
    {
        var text = BackupAuditor.DescribeEstimate(10);

        Assert.Contains("10 backup header(s)", text);
        Assert.Contains("seconds", text);
    }

    [Fact]
    public void TheEstimateIsGivenInMinutesForALargeOne()
    {
        var text = BackupAuditor.DescribeEstimate(200);

        Assert.Contains("minute(s)", text);
    }

    /// <summary>Re-running after a fix quotes what is left, not the full price again.</summary>
    [Fact]
    public void WithEverythingCachedTheEstimateSaysItWillBeInstant()
        => Assert.Contains("instant", BackupAuditor.DescribeEstimate(0));

    [Fact]
    public void OnlyTheSetsNotAlreadyKnownAreCounted()
    {
        var known = Set(name: "known.bak", etag: "\"aaa\"");
        var unknown = Set(name: "unknown.bak", etag: "\"bbb\"");

        var cached = new Dictionary<string, AuditRecord>
        {
            [BackupAuditStore.KeyFor(known.Files[0])] = new("k", true, "MyDb", 1, T0)
        };

        Assert.Same(unknown, Assert.Single(BackupAuditor.NotYetAudited([known, unknown], cached)));
    }

    // ── progress ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProgressReachesTheTotalSoTheBarFills()
    {
        var sql = new FakeSqlServerService { Header = Header() };
        var seen = new List<int>();

        var sets = new[]
        {
            Set(name: "a.bak", etag: "\"a\""),
            Set(name: "b.bak", etag: "\"b\""),
            Set(name: "c.bak", etag: "\"c\"")
        };

        await new BackupAuditor(sql, Store()).AuditAsync(
            Server(), sets, new Progress<int>(seen.Add));

        await Task.Delay(50);
        Assert.Contains(3, seen);
    }
}
