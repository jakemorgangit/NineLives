using System.IO;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The log must never contain a secret (#40).
///
/// Redaction happens at the logging boundary rather than at the call sites, so these tests are the
/// guarantee for every current and future caller. A log line that is over-redacted is a mild
/// annoyance; a log line with a working SAS token in it is a credential leak with a filename
/// attached, sitting in the folder people are told to attach to bug reports.
/// </summary>
public class LogRedactorTests
{
    private const string RealisticSas =
        "sv=2024-11-04&ss=b&srt=sco&sp=rl&se=2026-09-01T18:00:00Z&st=2026-08-01T10:00:00Z&spr=https&sig=aBcD1234%2FefGh%2BijKl%3D";

    [Fact]
    public void ASasSignatureIsRemoved()
    {
        var redacted = LogRedactor.Redact($"listing https://acct.blob.core.windows.net/backups?{RealisticSas}");

        Assert.DoesNotContain("aBcD1234", redacted);
        Assert.Contains("sig=[redacted]", redacted);
    }

    [Fact]
    public void EverySasParameterIsRemoved()
    {
        // Not just sig. The whole token is the credential - se and sp alone tell an attacker what
        // a leaked signature would be good for, and there is no diagnostic value in any of it.
        var redacted = LogRedactor.Redact(RealisticSas);

        foreach (var fragment in new[] { "2024-11-04", "sco", "2026-09-01T18:00:00Z", "https", "aBcD1234" })
            Assert.DoesNotContain(fragment, redacted);
    }

    [Fact]
    public void TheUrlItselfSurvivesSoTheLineStaysUseful()
    {
        var redacted = LogRedactor.Redact($"GET https://acct.blob.core.windows.net/backups/FULL/MyDb.bak?{RealisticSas}");

        Assert.Contains("https://acct.blob.core.windows.net/backups/FULL/MyDb.bak", redacted);
    }

    [Fact]
    public void ATsqlCredentialSecretIsRemoved()
    {
        const string sql =
            "CREATE CREDENTIAL [https://acct/backups] WITH IDENTITY = 'SHARED ACCESS SIGNATURE', SECRET = 'sv=2024&sig=verysecret'";

        var redacted = LogRedactor.Redact(sql);

        Assert.DoesNotContain("verysecret", redacted);
        Assert.Contains("SECRET = '[redacted]'", redacted);
        // The rest of the statement is what makes the line worth logging.
        Assert.Contains("CREATE CREDENTIAL", redacted);
        Assert.Contains("SHARED ACCESS SIGNATURE", redacted);
    }

    [Fact]
    public void AnAlterCredentialSecretIsRemovedToo()
    {
        var redacted = LogRedactor.Redact("ALTER CREDENTIAL [x] WITH IDENTITY = 'SHARED ACCESS SIGNATURE', SECRET = N'token'");

        Assert.DoesNotContain("token", redacted);
    }

    [Fact]
    public void ASecretContainingDoubledQuotesIsStillFullyRemoved()
    {
        // TSql.EscapeLiteral doubles single quotes, so a naive "up to the next quote" match would
        // stop early and leave the tail of the secret in the log.
        var redacted = LogRedactor.Redact("SECRET = 'abc''def''ghi' AND something_else = 1");

        Assert.DoesNotContain("abc", redacted);
        Assert.DoesNotContain("ghi", redacted);
        Assert.Contains("something_else", redacted);
    }

    [Theory]
    [InlineData("Server=x;User ID=sa;Password=hunter2;Encrypt=True")]
    [InlineData("Server=x;pwd=hunter2;Encrypt=True")]
    [InlineData("Password = hunter2")]
    public void AConnectionStringPasswordIsRemoved(string text)
    {
        var redacted = LogRedactor.Redact(text);

        Assert.DoesNotContain("hunter2", redacted);
    }

    [Fact]
    public void OrdinaryTextIsLeftAlone()
    {
        const string line = "Executing statement 3 of 12: RESTORE LOG [MyDb] FROM URL = N'https://acct/backups/x.trn'";

        Assert.Equal(line, LogRedactor.Redact(line));
    }

    [Fact]
    public void NullAndEmptyAreSafe()
    {
        Assert.Equal(string.Empty, LogRedactor.Redact(null));
        Assert.Equal(string.Empty, LogRedactor.Redact(""));
    }
}

/// <summary>The writer itself: it must record what it is given and must never throw.</summary>
public class OperationLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "ninelives-log-tests", Guid.NewGuid().ToString("n"));

    private OperationLog Log() => new(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string ReadLog() => File.ReadAllText(Log().CurrentFile);

    [Fact]
    public void WritingCreatesTheDirectoryAndTheFile()
    {
        var log = Log();
        log.Info("hello");

        Assert.True(File.Exists(log.CurrentFile));
        Assert.Contains("hello", ReadLog());
    }

    [Fact]
    public void EachLineCarriesATimestampAndLevel()
    {
        Log().Warn("something odd");

        var line = File.ReadAllLines(Log().CurrentFile).Last();
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} WARN  something odd$", line);
    }

    [Fact]
    public void EntriesAccumulateRatherThanOverwrite()
    {
        var log = Log();
        log.Info("first");
        log.Info("second");

        var text = ReadLog();
        Assert.Contains("first", text);
        Assert.Contains("second", text);
    }

    /// <summary>The reason redaction lives in the writer: a caller cannot opt out of it.</summary>
    [Fact]
    public void SecretsAreRedactedOnTheWayToDisk()
    {
        Log().Info("CREATE CREDENTIAL [x] WITH IDENTITY = 'SHARED ACCESS SIGNATURE', SECRET = 'sig=leaked'");

        var text = ReadLog();
        Assert.DoesNotContain("leaked", text);
        Assert.Contains("[redacted]", text);
    }

    [Fact]
    public void ServerChangesAreMarkedSoTheyCanBeFound()
    {
        Log().ServerChange("SRV01", "credential [https://acct/backups] updated");

        var text = ReadLog();
        Assert.Contains("CHANGE", text);
        Assert.Contains("[SRV01]", text);
    }

    [Fact]
    public void AnExceptionIsRecordedWithItsTypeAndMessage()
    {
        Log().Error("while restoring", new InvalidOperationException("chain broken"));

        var text = ReadLog();
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("chain broken", text);
    }

    /// <summary>
    /// A logging failure must never break the operation being logged. An unusable directory is the
    /// easiest way to prove the swallow works.
    /// </summary>
    [Fact]
    public void AnUnwritableLocationDoesNotThrow()
    {
        var log = new OperationLog("\0:\\definitely\\not\\a\\path");

        log.Info("this should vanish quietly");
        log.Error("so should this", new Exception("boom"));
        log.Prune();
    }

    [Fact]
    public void PruningAnAbsentDirectoryDoesNotThrow() => new OperationLog(_dir).Prune();

    [Fact]
    public void PruningRemovesOldFilesAndKeepsRecentOnes()
    {
        var log = Log();
        log.Info("today");

        Directory.CreateDirectory(_dir);
        var old = Path.Combine(_dir, "ninelives-20200101.log");
        File.WriteAllText(old, "ancient");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-60));

        log.Prune();

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(log.CurrentFile));
    }

    [Fact]
    public void ConcurrentWritesDoNotLoseLines()
    {
        // The execution log appends from a progress callback while the UI thread is also writing.
        var log = Log();

        Parallel.For(0, 200, i => log.Info($"line-{i}"));

        var lines = File.ReadAllLines(log.CurrentFile);
        Assert.Equal(200, lines.Length);
    }
}
