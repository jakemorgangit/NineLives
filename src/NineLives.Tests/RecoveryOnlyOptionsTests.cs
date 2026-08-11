using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The three options that belong only on the statement that brings the database online (#364).
///
/// KEEP_REPLICATION, ENABLE_BROKER and NEW_BROKER were emitted on every statement of the chain,
/// while the recovery clause was chosen independently. SQL Server refuses the combination -
/// Msg 3031, "Option 'norecovery' conflicts with option(s) 'keep_replication'" - so the first
/// statement failed and the option was unusable for any chain longer than one statement. Which is
/// every log-shipping and replication scenario it exists for.
///
/// The pin that existed used a full-only chain, which ends in RECOVERY, so the case that was
/// broken had never been covered.
/// </summary>
public class RecoveryOnlyOptionsTests
{
    private readonly RestoreScriptGenerator _generator = new();

    private static readonly DateTime T0 = new(2026, 1, 10, 22, 0, 0);

    private static BackupSet Set(BackupType type, DateTime timestamp) => new()
    {
        SetId = timestamp.ToString("yyyyMMdd_HHmmss"),
        Type = type,
        Timestamp = timestamp,
        Files =
        [
            new BackupFileInfo
            {
                BlobName = $"{timestamp:yyyyMMdd_HHmmss}.bak",
                BlobUrl = $"https://acct.blob.core.windows.net/backups/{timestamp:yyyyMMdd_HHmmss}.bak",
                Type = type
            }
        ]
    };

    /// <summary>Full + diff + two logs: four statements, three of them NORECOVERY.</summary>
    private static BackupChain FourStatementChain() => new()
    {
        FullSet = Set(BackupType.Full, T0),
        DiffSets = [Set(BackupType.Differential, T0.AddHours(4))],
        LogSets = [Set(BackupType.TransactionLog, T0.AddHours(5)), Set(BackupType.TransactionLog, T0.AddHours(6))]
    };

    private static RestoreOptions Options(Action<RestoreOptions>? mutate = null)
    {
        var o = new RestoreOptions
        {
            TargetDatabaseName = "TestDb",
            DisconnectSessions = false,
            RecoveryMode = RecoveryMode.Recovery,
            KeepReplication = true,
            EnableBroker = true,
            NewBroker = true
        };
        mutate?.Invoke(o);
        return o;
    }

    /// <summary>Every RESTORE ... GO block in the script, in order.</summary>
    private static List<string> Statements(string script) => script
        .Replace("\r\n", "\n")
        .Split("GO\n")
        .Where(s => s.Contains("RESTORE DATABASE") || s.Contains("RESTORE LOG"))
        .ToList();

    [Fact]
    public void OnlyTheRecoveringStatementCarriesThem()
    {
        var statements = Statements(_generator.Generate(FourStatementChain(), Options()));

        Assert.Equal(4, statements.Count);

        foreach (var norecovery in statements.Take(3))
        {
            Assert.Contains("NORECOVERY", norecovery);
            Assert.DoesNotContain("KEEP_REPLICATION", norecovery);
            Assert.DoesNotContain("ENABLE_BROKER", norecovery);
            Assert.DoesNotContain("NEW_BROKER", norecovery);
        }

        var last = statements[^1];
        Assert.DoesNotContain("NORECOVERY", last);
        Assert.Contains("KEEP_REPLICATION,", last);
        Assert.Contains("ENABLE_BROKER,", last);
        Assert.Contains("NEW_BROKER,", last);
    }

    /// <summary>
    /// A chain deliberately left open carries them nowhere. This is the distinction the fix turns
    /// on: not "the last statement" but "the statement that recovers" - a log-shipping secondary
    /// ends in NORECOVERY and never has a running database for these options to describe.
    /// </summary>
    [Fact]
    public void AChainLeftInNoRecoveryCarriesThemNowhere()
    {
        var script = _generator.Generate(
            FourStatementChain(), Options(o => o.RecoveryMode = RecoveryMode.NoRecovery));

        Assert.DoesNotContain("KEEP_REPLICATION", script);
        Assert.DoesNotContain("ENABLE_BROKER", script);
        Assert.DoesNotContain("NEW_BROKER", script);
    }

    /// <summary>Same for STANDBY - readable, but still mid-sequence.</summary>
    [Fact]
    public void AChainEndingInStandbyCarriesThemNowhere()
    {
        var script = _generator.Generate(FourStatementChain(), Options(o =>
        {
            o.RecoveryMode = RecoveryMode.Standby;
            o.StandbyFilePath = @"D:\undo\standby.bak";
        }));

        Assert.Contains("STANDBY", script);
        Assert.DoesNotContain("KEEP_REPLICATION", script);
        Assert.DoesNotContain("ENABLE_BROKER", script);
    }

    /// <summary>
    /// The single-statement case still works, which is what the old pin covered and what kept
    /// this looking correct.
    /// </summary>
    [Fact]
    public void ASingleStatementChainStillCarriesThem()
    {
        var script = _generator.Generate(
            new BackupChain { FullSet = Set(BackupType.Full, T0) }, Options());

        Assert.Contains("RECOVERY,", script);
        Assert.Contains("KEEP_REPLICATION,", script);
        Assert.Contains("ENABLE_BROKER,", script);
    }

    /// <summary>
    /// The options that DO belong on every statement stayed there. CHECKSUM and
    /// CONTINUE_AFTER_ERROR describe how each backup is read, not what the database does
    /// afterwards, so the split must not have swept them along.
    /// </summary>
    [Fact]
    public void ThePerStatementOptionsAreStillOnEveryStatement()
    {
        var statements = Statements(_generator.Generate(FourStatementChain(), Options(o =>
        {
            o.WithChecksum = true;
            o.ContinueAfterError = true;
            o.StatsPercent = 5;
        })));

        Assert.Equal(4, statements.Count);
        Assert.All(statements, s =>
        {
            Assert.Contains("CHECKSUM,", s);
            Assert.Contains("CONTINUE_AFTER_ERROR,", s);
            Assert.Contains("STATS = 5;", s);
        });
    }
}
