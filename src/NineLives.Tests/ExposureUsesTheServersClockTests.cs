using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Exposure is judged on the clock the dates came from (#414).
///
/// msdb records `backup_finish_date` in the INSTANCE's local time, and both callers judged it
/// against `DateTime.Now` on the machine running the app. So every age was out by the offset
/// between the two - and with a one-hour warning threshold, the offset decided the verdict.
///
/// The dangerous direction is a server ahead of the app: backups look NEWER than they are, so an
/// alarm is downgraded to a warning, or - when the offset exceeds the real age - the arithmetic
/// goes negative and the row comes out green. This screen's own summary says a dashboard that
/// hides the bad row "reads as all clear at the exact moment it should not".
/// </summary>
public class ExposureUsesTheServersClockTests
{
    /// <summary>A machine in New York looking at a server in London: the server is 5h ahead.</summary>
    private static readonly DateTime AppNow = new(2026, 8, 12, 09, 00, 00);
    private static readonly DateTime ServerNow = new(2026, 8, 12, 14, 00, 00);

    private static ExposureRow Row(DateTime lastLog, DateTime? serverNow) => new()
    {
        ServerName = "SRV01",
        DatabaseName = "Sales",
        RecoveryModel = "FULL",
        StateDescription = "ONLINE",
        ServerNow = serverNow,
        LastFull = lastLog.AddDays(-1),
        LastLog = lastLog
    };

    /// <summary>
    /// 28 hours without a log, on a server five hours ahead. Judged on this machine's clock it
    /// computes as 23 hours and lands under the 24-hour alarm; judged on the server's own clock
    /// it is what it is.
    /// </summary>
    [Fact]
    public void AnAlarmIsNotDowngradedByTheOffset()
    {
        var row = Row(ServerNow.AddHours(-28), ServerNow);

        ExposureAdvisor.Judge(row, AppNow);

        Assert.Equal(ExposureLevel.Alarm, row.Level);
    }

    /// <summary>
    /// The extreme of the same error: when the offset exceeds the age the subtraction goes
    /// negative, which is below every threshold, and a database that has not been backed up for
    /// hours comes out green.
    /// </summary>
    [Fact]
    public void TheArithmeticDoesNotGoNegative()
    {
        var row = Row(ServerNow.AddHours(-3), ServerNow);

        ExposureAdvisor.Judge(row, AppNow);

        // Three hours without a log is past the one-hour warning, and must say so.
        Assert.Equal(ExposureLevel.Warning, row.Level);
    }

    /// <summary>
    /// And the other direction: a server BEHIND the app made healthy databases look old. Ten
    /// minutes since the last log is fine, and must not be reported as a warning because the app
    /// machine's clock is five hours ahead of the instance's.
    /// </summary>
    [Fact]
    public void AHealthyDatabaseIsNotFalselyWarnedAbout()
    {
        var serverNow = AppNow.AddHours(-5);
        var row = Row(serverNow.AddMinutes(-10), serverNow);

        ExposureAdvisor.Judge(row, AppNow);

        Assert.Equal(ExposureLevel.Ok, row.Level);
    }

    /// <summary>
    /// One clock, no offset: the answer is the same either way. This is the case that passed
    /// before and must keep passing - the whole estate on one machine's time zone.
    /// </summary>
    [Fact]
    public void NothingChangesWhenTheClocksAgree()
    {
        var row = Row(AppNow.AddHours(-28), AppNow);

        ExposureAdvisor.Judge(row, AppNow);

        Assert.Equal(ExposureLevel.Alarm, row.Level);
    }

    /// <summary>
    /// A row the app fabricated because the server would not answer has no server clock, and
    /// falls back to the caller's rather than throwing.
    /// </summary>
    [Fact]
    public void ARowWithNoServerClockStillJudges()
    {
        var row = Row(AppNow.AddHours(-28), serverNow: null);

        ExposureAdvisor.Judge(row, AppNow);

        Assert.Equal(ExposureLevel.Alarm, row.Level);
    }
}
