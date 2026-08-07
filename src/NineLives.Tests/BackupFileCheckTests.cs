using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Telling apart the ways a target instance fails to read a backup file (#149).
///
/// SQL Server reports every one of them as 3201 "Cannot open backup device", so the operating
/// system number inside the message is the only thing that distinguishes "it is not there" from
/// "you are not allowed to read it" - and those two send somebody to completely different places.
/// </summary>
public class BackupFileCheckTests
{
    private const string Path = @"\\nas01\sql\MyDb_full.bak";

    /// <summary>
    /// Operating system error 5. The one this check exists for: a SQL Server running as a local
    /// account, or as NT SERVICE\MSSQLSERVER, has no identity on the network and cannot read any
    /// share - which from outside looks exactly like a missing file.
    /// </summary>
    [Theory]
    [InlineData(@"Cannot open backup device '\\nas01\sql\MyDb_full.bak'. Operating system error 5(Access is denied.).")]
    [InlineData("Operating system error 5(Access is denied.)")]
    [InlineData("access is denied")]
    public void AccessDeniedIsRecognisedForWhatItIs(string message)
    {
        var check = BackupFileCheck.From(Path, message);

        Assert.Equal(BackupFileProblem.AccessDenied, check.Problem);
        Assert.False(check.CanBeRestored);
    }

    [Theory]
    [InlineData(@"Cannot open backup device. Operating system error 2(The system cannot find the file specified.).")]
    [InlineData(@"Operating system error 3(The system cannot find the path specified.)")]
    public void AMissingFileOrPathIsRecognised(string message)
        => Assert.Equal(BackupFileProblem.NotFound, BackupFileCheck.From(Path, message).Problem);

    [Theory]
    [InlineData("Msg 3241: The media family on device is incorrectly formed.")]
    [InlineData("The media family on device '...' is incorrectly formed.")]
    public void SomethingThatIsNotABackupIsRecognised(string message)
        => Assert.Equal(BackupFileProblem.NotAValidBackup, BackupFileCheck.From(Path, message).Problem);

    /// <summary>
    /// A UNC path whose HOST does not resolve never reports a missing file: the statement hangs
    /// while Windows looks for the machine, and the command timeout expires first. Probing a real
    /// instance is what turned this up - it had been surfacing as a bare "Execution Timeout
    /// Expired" after a 30-second wait, which says nothing about what is wrong.
    /// </summary>
    [Theory]
    [InlineData("Execution Timeout Expired.  The timeout period elapsed prior to completion of the operation or the server is not responding.")]
    [InlineData("Timeout expired")]
    public void AHostThatCannotBeReachedIsRecognisedRatherThanLeftAsATimeout(string message)
    {
        var check = BackupFileCheck.From(Path, message);

        Assert.Equal(BackupFileProblem.Unreachable, check.Problem);
        Assert.Contains("HOST", check.Explain("SRV02"));
    }

    /// <summary>
    /// An unrecognised failure is reported as unrecognised, with the server's own words. Guessing
    /// would send somebody to fix the wrong thing.
    /// </summary>
    [Fact]
    public void AnythingElseKeepsTheServersOwnWords()
    {
        var check = BackupFileCheck.From(Path, "A transport-level error occurred.");

        Assert.Equal(BackupFileProblem.Other, check.Problem);
        Assert.Contains("transport-level", check.ServerMessage);
    }

    /// <summary>The message is kept whatever the classification - the numbered error is what
    /// somebody searches for when the explanation does not match their situation.</summary>
    [Fact]
    public void TheServerMessageSurvivesClassification()
    {
        var check = BackupFileCheck.From(Path, "Operating system error 5(Access is denied.).");

        Assert.Contains("Operating system error 5", check.ServerMessage);
    }

    /// <summary>
    /// Access denied is explained in terms of the service account, not the file. The error itself
    /// sends people to check the file, and the file is almost never the problem.
    /// </summary>
    [Fact]
    public void AccessDeniedIsExplainedAsTheServiceAccountNotTheFile()
    {
        var explanation = BackupFileCheck
            .From(Path, "Operating system error 5(Access is denied.).")
            .Explain("SRV02");

        Assert.Contains("SRV02", explanation);
        Assert.Contains("service account", explanation);
        Assert.Contains("NT SERVICE", explanation);
    }

    [Fact]
    public void ANotFoundIsExplainedAsAHostThatCannotSeeThePath()
    {
        var explanation = BackupFileCheck
            .From(Path, "Operating system error 2(The system cannot find the file specified.).")
            .Explain("SRV02");

        Assert.Contains("cannot find", explanation);
        Assert.Contains("share", explanation);
    }

    [Fact]
    public void AReadableFileSaysSo()
    {
        var check = BackupFileCheck.Ok(Path);

        Assert.True(check.CanBeRestored);
        Assert.Equal(BackupFileProblem.None, check.Problem);
    }

    // ── against a real instance ─────────────────────────────────────────────────

    /// <summary>
    /// A path the instance genuinely cannot reach, checked against a real server: the statement
    /// runs, the failure comes back as a message rather than an exception escaping, and it is
    /// classified as missing rather than as something unrecognised.
    ///
    /// Read-only, and deliberately points at a UNC path that does not exist - nothing is created,
    /// read or written anywhere. Skipped unless NINELIVES_TEST_SQL is set.
    /// </summary>
    [RequiresSqlFact]
    public async Task AnUnreachablePathIsReportedRatherThanThrown()
    {
        var service = new SqlServerService(new FakeCredentialStore());
        var server = new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "test",
            ServerName = SqlExecutionFailureTests.TestServerName!,
            AuthMode = AuthMode.WindowsAuth,
            TrustServerCertificate = true
        };

        var check = await service.CheckBackupFileAsync(
            server, @"\\ninelives-no-such-host\no-such-share\no-such-file.bak");

        Assert.False(check.CanBeRestored);
        Assert.False(string.IsNullOrWhiteSpace(check.ServerMessage));

        // It must not land in "Other" - that is the bucket that puts a raw SQL error in front of
        // somebody mid-incident. An unresolvable host times out rather than reporting a missing
        // file, which is exactly why Unreachable exists.
        Assert.NotEqual(BackupFileProblem.None, check.Problem);
        Assert.NotEqual(BackupFileProblem.Other, check.Problem);
    }
}
