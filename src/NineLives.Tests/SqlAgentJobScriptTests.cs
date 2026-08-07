using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Handing a restore over as an Agent job (#32).
///
/// For the people who cannot run one interactively: a maintenance window at 3am, a change process
/// that takes submitted scripts rather than somebody at a keyboard, an operator who runs what a DBA
/// hands them. The job is the deliverable, and it has to be reviewable before it exists.
/// </summary>
public class SqlAgentJobScriptTests
{
    private const string Restore = """
        RESTORE DATABASE [MyDb_Restored]
            FROM URL = N'https://acct.blob.core.windows.net/backups/full.bak'
            WITH NORECOVERY;
        GO
        RESTORE LOG [MyDb_Restored]
            FROM URL = N'https://acct.blob.core.windows.net/backups/log1.trn'
            WITH RECOVERY;
        GO
        """;

    private static string Wrap(string script = Restore, string name = "NineLives restore MyDb")
        => SqlAgentJobScript.Wrap(script, name);

    // ── GO is the whole reason this is not a one-liner ──────────────────────────

    /// <summary>
    /// GO is understood by SSMS and sqlcmd, not by SQL Server. A job step containing one fails with
    /// a syntax error at the moment the job RUNS - which, for a restore handed over for a
    /// maintenance window, is the worst possible moment to find out.
    /// </summary>
    [Fact]
    public void NoStepContainsAGoSeparator()
    {
        foreach (var batch in SqlAgentJobScript.SplitBatches(Restore))
            Assert.DoesNotContain("GO", batch, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EachBatchBecomesItsOwnStep()
    {
        var sql = Wrap();

        Assert.Contains("@step_name = N'Step 1 of 2'", sql);
        Assert.Contains("@step_name = N'Step 2 of 2'", sql);
    }

    [Fact]
    public void TheBatchesKeepTheirOrder()
    {
        var batches = SqlAgentJobScript.SplitBatches(Restore);

        Assert.Equal(2, batches.Count);
        Assert.Contains("RESTORE DATABASE", batches[0]);
        Assert.Contains("RESTORE LOG", batches[1]);
    }

    [Fact]
    public void TrailingAndRepeatedSeparatorsDoNotProduceEmptySteps()
    {
        var batches = SqlAgentJobScript.SplitBatches("SELECT 1;\nGO\nGO\n\nGO\n");

        Assert.Single(batches);
    }

    // ── what it deliberately does not do ────────────────────────────────────────

    /// <summary>
    /// The job is created disabled and with no schedule. A script that quietly arranged to restore
    /// over a database at some future point would be a remarkable thing to do by accident, and
    /// nothing about pasting a script into SSMS suggests it might.
    /// </summary>
    [Fact]
    public void TheJobIsCreatedDisabled()
    {
        var sql = Wrap();

        Assert.Contains("@enabled = 0", sql);
        Assert.DoesNotContain("sp_add_jobschedule", sql);
        Assert.DoesNotContain("sp_add_schedule", sql);
    }

    /// <summary>
    /// A job of this name may be somebody else's, and dropping it to make room is not a decision a
    /// generated script gets to take.
    /// </summary>
    [Fact]
    public void AnExistingJobOfTheSameNameIsRefusedRatherThanReplaced()
    {
        var sql = Wrap();

        Assert.Contains("IF EXISTS", sql);
        Assert.Contains("RAISERROR", sql);
        Assert.DoesNotContain("sp_delete_job @job_name = N'NineLives restore MyDb';\r\nEXEC msdb.dbo.sp_add_job", sql);
    }

    /// <summary>
    /// Any failure stops the job. Carrying on to the next RESTORE would apply a differential or a
    /// log to a database that never got its full.
    /// </summary>
    [Fact]
    public void AFailedStepStopsTheJob()
    {
        var sql = Wrap();

        Assert.Contains("@on_fail_action = 2", sql);
        Assert.DoesNotContain("@on_fail_action = 3", sql);
    }

    /// <summary>The last step reports success rather than falling off the end into nothing.</summary>
    [Fact]
    public void TheLastStepEndsTheJobSuccessfully()
    {
        var sql = Wrap();

        Assert.Contains("@on_success_action = 3", sql);   // step 1 moves on
        Assert.Contains("@on_success_action = 1", sql);   // step 2 finishes
    }

    // ── ordinary care ───────────────────────────────────────────────────────────

    /// <summary>
    /// A quote in the script must not end the literal it is being embedded in. Restore scripts are
    /// full of them - every URL and path is quoted.
    /// </summary>
    [Fact]
    public void QuotesInTheScriptCannotBreakOutOfTheCommand()
    {
        var sql = SqlAgentJobScript.Wrap("RESTORE DATABASE [X] FROM DISK = N'C:\\a.bak';", "job");

        Assert.Contains(@"FROM DISK = N''C:\a.bak''", sql);
    }

    [Fact]
    public void AQuoteInTheJobNameCannotBreakOutEither()
    {
        var sql = SqlAgentJobScript.Wrap("SELECT 1;", "Jake's restore");

        Assert.Contains("@job_name = N'Jake''s restore'", sql);
        Assert.DoesNotContain("N'Jake's restore'", sql);
    }

    [Fact]
    public void NothingIsProducedFromAnEmptyScript()
    {
        Assert.Equal(string.Empty, SqlAgentJobScript.Wrap("", "job"));
        Assert.Equal(string.Empty, SqlAgentJobScript.Wrap("   \n GO \n ", "job"));
    }

    [Fact]
    public void NothingIsProducedWithoutAName()
        => Assert.Equal(string.Empty, SqlAgentJobScript.Wrap(Restore, ""));

    /// <summary>
    /// Timestamped because these accumulate: two refreshes of the same test database a week apart
    /// are two different jobs, and a collision would otherwise refuse the second.
    /// </summary>
    [Fact]
    public void TheSuggestedNameSaysWhatAndWhen()
    {
        var name = SqlAgentJobScript.SuggestName("MyDb_Test", new DateTime(2026, 8, 7, 22, 30, 0));

        Assert.Equal("NineLives restore MyDb_Test 2026-08-07 2230", name);
    }

    /// <summary>The script says how to run it, enable it and remove it - it is handed to somebody else.</summary>
    [Fact]
    public void ItSaysHowToRunEnableAndRemoveTheJob()
    {
        var sql = Wrap();

        Assert.Contains("sp_start_job", sql);
        Assert.Contains("sp_update_job", sql);
        Assert.Contains("sp_delete_job", sql);
    }
}
