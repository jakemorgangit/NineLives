using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

public class RestoreScriptGeneratorTests
{
    private readonly RestoreScriptGenerator _generator = new();

    private static readonly DateTime T0 = new(2026, 1, 10, 22, 0, 0);

    private static BackupSet Set(BackupType type, DateTime timestamp, params string[] urls)
    {
        if (urls.Length == 0)
            urls = [$"https://acct.blob.core.windows.net/backups/{timestamp:yyyyMMdd_HHmmss}.bak"];

        return new BackupSet
        {
            SetId = timestamp.ToString("yyyyMMdd_HHmmss"),
            Type = type,
            Timestamp = timestamp,
            Files = urls.Select(u => new BackupFileInfo
            {
                BlobName = u[(u.LastIndexOf('/') + 1)..],
                BlobUrl = u,
                Type = type
            }).ToList()
        };
    }

    private static BackupChain FullOnlyChain() => new() { FullSet = Set(BackupType.Full, T0) };

    private static BackupChain FullDiffLogChain() => new()
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
            WithReplace = false,
            DisconnectSessions = false,
            RecoveryMode = RecoveryMode.Recovery
        };
        mutate?.Invoke(o);
        return o;
    }

    private static List<string> Lines(string script) =>
        script.Replace("\r\n", "\n").Split('\n').ToList();

    [Fact]
    public void Generate_FullOnly_Recovery_EmitsRecoveryNotNoRecovery()
    {
        var script = _generator.Generate(FullOnlyChain(), Options());

        Assert.Contains("RESTORE DATABASE [TestDb]", script);
        Assert.Contains("RECOVERY,", script);
        Assert.DoesNotContain("NORECOVERY", script);
        Assert.Contains("STATS = 10;", script);
    }

    [Fact]
    public void Generate_FullOnly_NoRecoveryMode_EmitsNoRecovery()
    {
        var script = _generator.Generate(FullOnlyChain(), Options(o => o.RecoveryMode = RecoveryMode.NoRecovery));

        Assert.Contains("NORECOVERY,", script);
    }

    [Fact]
    public void Generate_FullOnly_Standby_EmitsStandbyPath()
    {
        var script = _generator.Generate(FullOnlyChain(), Options(o =>
        {
            o.RecoveryMode = RecoveryMode.Standby;
            o.StandbyFilePath = @"D:\standby\TestDb.tuf";
        }));

        Assert.Contains(@"STANDBY = 'D:\standby\TestDb.tuf',", script);
    }

    [Fact]
    public void Generate_FullDiffLogChain_OnlyLastStepRecovers()
    {
        var script = _generator.Generate(FullDiffLogChain(), Options());
        var lines = Lines(script);

        // Full and diff and the first log are intermediate: NORECOVERY.
        var noRecoveryLines = lines.Where(l => l.Trim() == "NORECOVERY,").ToList();
        Assert.Equal(3, noRecoveryLines.Count);

        // Exactly one bare RECOVERY clause, and it belongs to the final RESTORE LOG.
        var recoveryLines = lines.Where(l => l.Trim() == "RECOVERY,").ToList();
        Assert.Single(recoveryLines);
        var lastRestoreIdx = lines.FindLastIndex(l => l.StartsWith("RESTORE LOG"));
        var recoveryIdx = lines.FindIndex(l => l.Trim() == "RECOVERY,");
        Assert.True(recoveryIdx > lastRestoreIdx, "RECOVERY must come after the last RESTORE LOG statement");
    }

    [Fact]
    public void Generate_ChainStatements_AppearInRestoreOrder()
    {
        var script = _generator.Generate(FullDiffLogChain(), Options());

        var fullIdx = script.IndexOf("Restore FULL backup");
        var diffIdx = script.IndexOf("Restore DIFFERENTIAL backup");
        var logIdx = script.IndexOf("Restore LOG backup");
        Assert.True(fullIdx >= 0 && diffIdx > fullIdx && logIdx > diffIdx);
        Assert.Equal(2, CountOccurrences(script, "RESTORE LOG [TestDb]"));
    }

    [Fact]
    public void Generate_WithReplace_EmitsReplaceOnFullRestoreOnly()
    {
        var script = _generator.Generate(FullDiffLogChain(), Options(o => o.WithReplace = true));

        Assert.Equal(1, CountOccurrences(script, "REPLACE,"));
        // REPLACE precedes the diff restore - it belongs to the full only
        Assert.True(script.IndexOf("REPLACE,") < script.IndexOf("Restore DIFFERENTIAL"));
    }

    [Fact]
    public void Generate_WithoutReplace_NoReplaceClause()
    {
        var script = _generator.Generate(FullOnlyChain(), Options());
        Assert.DoesNotContain("REPLACE,", script);
    }

    [Fact]
    public void Generate_FileMoves_OnlyNonEmptyTargetsEmitted()
    {
        var script = _generator.Generate(FullOnlyChain(), Options(o =>
        {
            o.FileMoves =
            [
                new FileMoveOption { LogicalName = "TestDb_Data", NewPhysicalName = @"D:\Data\TestDb.mdf" },
                new FileMoveOption { LogicalName = "TestDb_Log", NewPhysicalName = "" },
            ];
        }));

        Assert.Contains(@"MOVE N'TestDb_Data' TO N'D:\Data\TestDb.mdf',", script);
        Assert.DoesNotContain("TestDb_Log", script);
    }

    [Fact]
    public void Generate_StopAtWithLogs_EmittedOnEveryLogRestore()
    {
        // Microsoft's guidance is to repeat STOPAT on each RESTORE LOG so SQL Server stops in
        // whichever log actually contains the target. Emitting it only on the last statement
        // overshoots when the target falls in an earlier log.
        var stopAt = T0.AddHours(5).AddMinutes(30);
        var chain = FullDiffLogChain(); // 2 log sets
        var script = _generator.Generate(chain, Options(o => o.StopAt = stopAt));

        Assert.Equal(chain.LogSets.Count, CountOccurrences(script, "STOPAT"));
        Assert.Equal(2, CountOccurrences(script, $"STOPAT = '{stopAt:yyyy-MM-ddTHH:mm:ss}',"));
    }

    [Fact]
    public void Generate_StopAt_NotEmittedOnFullOrDiffRestore()
    {
        // STOPAT belongs to the log chain; putting it on the data restores is not the pattern.
        var stopAt = T0.AddHours(5).AddMinutes(30);
        var script = _generator.Generate(FullDiffLogChain(), Options(o => o.StopAt = stopAt));

        var firstLogIdx = script.IndexOf("RESTORE LOG");
        var beforeLogs = script[..firstLogIdx];
        Assert.DoesNotContain("STOPAT", beforeLogs);
    }

    [Fact]
    public void Generate_StopAtSingleLogChain_EmittedOnce()
    {
        var stopAt = T0.AddHours(5).AddMinutes(1);
        var chain = new BackupChain
        {
            FullSet = Set(BackupType.Full, T0),
            LogSets = [Set(BackupType.TransactionLog, T0.AddHours(5))]
        };

        var script = _generator.Generate(chain, Options(o => o.StopAt = stopAt));

        Assert.Equal(1, CountOccurrences(script, "STOPAT"));
        Assert.Contains($"STOPAT = '{stopAt:yyyy-MM-ddTHH:mm:ss}',", script);
    }

    [Fact]
    public void Generate_StopAt_UsesIsoFormatSqlServerParsesUnambiguously()
    {
        // A locale-dependent format here would be read differently by a server in another
        // region; the ISO form removes the ambiguity between dd/MM and MM/dd.
        var stopAt = new DateTime(2026, 3, 4, 14, 23, 41);
        var script = _generator.Generate(FullDiffLogChain(), Options(o => o.StopAt = stopAt));

        Assert.Contains("STOPAT = '2026-03-04T14:23:41',", script);
    }

    [Fact]
    public void Generate_StopAtWithoutLogs_NotEmitted()
    {
        var script = _generator.Generate(FullOnlyChain(), Options(o => o.StopAt = T0.AddHours(1)));
        Assert.DoesNotContain("STOPAT", script);
        // ... but the header still documents the requested point-in-time
        Assert.Contains("Point-in-Time:", script);
    }

    [Fact]
    public void Generate_DisconnectSessions_EmitsSingleUserGuardAndMultiUserRestore()
    {
        var script = _generator.Generate(FullOnlyChain(), Options(o => o.DisconnectSessions = true));

        Assert.Contains("IF DB_ID('TestDb') IS NOT NULL", script);
        Assert.Contains("ALTER DATABASE [TestDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;", script);
        Assert.Contains("ALTER DATABASE [TestDb] SET MULTI_USER;", script);
    }

    [Fact]
    public void Generate_DisconnectSessions_NoMultiUserWhenNotRecovering()
    {
        var script = _generator.Generate(FullOnlyChain(), Options(o =>
        {
            o.DisconnectSessions = true;
            o.RecoveryMode = RecoveryMode.NoRecovery;
        }));

        Assert.Contains("SINGLE_USER", script);
        Assert.DoesNotContain("MULTI_USER", script);
    }

    [Fact]
    public void Generate_NoDisconnectSessions_NoUserModeStatements()
    {
        var script = _generator.Generate(FullOnlyChain(), Options());
        Assert.DoesNotContain("SINGLE_USER", script);
        Assert.DoesNotContain("MULTI_USER", script);
    }

    [Fact]
    public void Generate_PlainDbName_GetsBracketed()
    {
        var script = _generator.Generate(FullOnlyChain(), Options(o => o.TargetDatabaseName = "MyDb"));
        Assert.Contains("RESTORE DATABASE [MyDb]", script);
    }

    [Fact]
    public void Generate_AlreadyBracketedDbName_KeptAsIs()
    {
        var script = _generator.Generate(FullOnlyChain(), Options(o => o.TargetDatabaseName = "[My Db]"));
        Assert.Contains("RESTORE DATABASE [My Db]", script);
        Assert.DoesNotContain("[[", script);
    }

    [Fact]
    public void Generate_DbNameWithCloseBracket_IsEscaped()
    {
        // Previously produced RESTORE DATABASE [My]DB] - a syntax error, because the bracket
        // was not doubled and so terminated the identifier early.
        var script = _generator.Generate(FullOnlyChain(), Options(o => o.TargetDatabaseName = "My]DB"));

        Assert.Contains("RESTORE DATABASE [My]]DB]", script);
        Assert.DoesNotContain("RESTORE DATABASE [My]DB]", script);
    }

    [Fact]
    public void Generate_DbNameWithCloseBracket_EscapedInDisconnectBlockToo()
    {
        var script = _generator.Generate(FullOnlyChain(), Options(o =>
        {
            o.TargetDatabaseName = "My]DB";
            o.DisconnectSessions = true;
        }));

        // Identifier positions are bracket-quoted...
        Assert.Contains("ALTER DATABASE [My]]DB] SET SINGLE_USER", script);
        Assert.Contains("ALTER DATABASE [My]]DB] SET MULTI_USER", script);
        // ...and the DB_ID argument is the bare name as a string literal, not a mangled one.
        Assert.Contains("IF DB_ID('My]DB') IS NOT NULL", script);
    }

    [Fact]
    public void Generate_DbNameWithApostrophe_EscapedInDbIdLiteral()
    {
        // DB_ID takes the name as data; an apostrophe would otherwise terminate the literal.
        var script = _generator.Generate(FullOnlyChain(), Options(o =>
        {
            o.TargetDatabaseName = "O'Brien";
            o.DisconnectSessions = true;
        }));

        Assert.Contains("IF DB_ID('O''Brien') IS NOT NULL", script);
        Assert.Contains("ALTER DATABASE [O'Brien] SET SINGLE_USER", script);
    }

    [Fact]
    public void Generate_StandbyPathWithApostrophe_IsEscaped()
    {
        var script = _generator.Generate(FullOnlyChain(), Options(o =>
        {
            o.RecoveryMode = RecoveryMode.Standby;
            o.StandbyFilePath = @"D:\O'Brien\standby.tuf";
        }));

        Assert.Contains(@"STANDBY = 'D:\O''Brien\standby.tuf'", script);
    }

    [Fact]
    public void Generate_FileMovePathWithApostrophe_IsEscaped()
    {
        var script = _generator.Generate(FullOnlyChain(), Options(o =>
        {
            o.FileMoves =
            [
                new FileMoveOption { LogicalName = "O'Data", NewPhysicalName = @"D:\O'Brien\db.mdf" }
            ];
        }));

        Assert.Contains(@"MOVE N'O''Data' TO N'D:\O''Brien\db.mdf',", script);
    }

    [Fact]
    public void Generate_EmptyDbName_UsesPlaceholder()
    {
        var script = _generator.Generate(FullOnlyChain(), Options(o => o.TargetDatabaseName = ""));
        Assert.Contains("RESTORE DATABASE [DatabaseName]", script);
    }

    [Fact]
    public void Generate_StripedSet_EmitsAllUrlsCommaSeparated()
    {
        var chain = new BackupChain
        {
            FullSet = Set(BackupType.Full, T0,
                "https://acct.blob.core.windows.net/backups/full_1.bak",
                "https://acct.blob.core.windows.net/backups/full_2.bak",
                "https://acct.blob.core.windows.net/backups/full_3.bak")
        };

        var script = _generator.Generate(chain, Options());
        var lines = Lines(script);

        Assert.Contains(lines, l => l.StartsWith("    FROM URL = N'") && l.EndsWith("full_1.bak',"));
        Assert.Contains(lines, l => l.TrimStart().StartsWith("URL = N'") && l.EndsWith("full_2.bak',"));
        // Last URL has no trailing comma
        Assert.Contains(lines, l => l.TrimStart().StartsWith("URL = N'") && l.EndsWith("full_3.bak'"));
    }

    [Fact]
    public void Generate_UrlWithSpace_PercentEncoded()
    {
        var chain = new BackupChain
        {
            FullSet = Set(BackupType.Full, T0, "https://acct.blob.core.windows.net/backups/my db/full backup.bak")
        };

        var script = _generator.Generate(chain, Options());

        Assert.Contains("my%20db/full%20backup.bak", script);
        Assert.DoesNotContain("my db/full backup.bak", script);
    }

    [Fact]
    public void Generate_CommonOptionFlags_EmittedWhenSet()
    {
        var script = _generator.Generate(FullOnlyChain(), Options(o =>
        {
            o.KeepReplication = true;
            o.EnableBroker = true;
            o.NewBroker = true;
            o.StatsPercent = 5;
        }));

        Assert.Contains("KEEP_REPLICATION,", script);
        Assert.Contains("ENABLE_BROKER,", script);
        Assert.Contains("NEW_BROKER,", script);
        Assert.Contains("STATS = 5;", script);
    }

    [Fact]
    public void Generate_ChecksumAndContinueAfterError_OffByDefault()
    {
        // CONTINUE_AFTER_ERROR produces a database SQL Server has already reported as damaged, and
        // CHECKSUM fails outright against a backup that was taken without checksums. Neither is a
        // safe default for a restore that runs unattended.
        var script = _generator.Generate(FullOnlyChain(), Options());

        Assert.DoesNotContain("CHECKSUM", script);
        Assert.DoesNotContain("CONTINUE_AFTER_ERROR", script);
    }

    [Fact]
    public void Generate_ChecksumAndContinueAfterError_EmittedOnEveryStatementWhenSet()
    {
        var script = _generator.Generate(FullDiffLogChain(), Options(o =>
        {
            o.WithChecksum = true;
            o.ContinueAfterError = true;
        }));

        // Full + diff + two logs. An option applied to only the first statement would leave the
        // rest of the chain restoring under different rules from the one the user chose.
        Assert.Equal(4, CountOccurrences(script, "         CHECKSUM,"));
        Assert.Equal(4, CountOccurrences(script, "         CONTINUE_AFTER_ERROR,"));
    }

    [Fact]
    public void Generate_ContinueAfterError_WarnsInTheScriptItself()
    {
        // The script gets pasted into SSMS and run by someone who never saw the checkbox.
        var script = _generator.Generate(FullOnlyChain(), Options(o => o.ContinueAfterError = true));

        Assert.Contains("WARNING: CONTINUE_AFTER_ERROR", script);
        Assert.Contains("DBCC CHECKDB", script);
    }

    [Fact]
    public void Generate_HeaderAndFooter_DescribeTheRestore()
    {
        var script = _generator.Generate(FullDiffLogChain(), Options());

        Assert.Contains("Nine Lives - Generated Restore Script", script);
        Assert.Contains("-- Target Database: TestDb", script);
        Assert.Contains("-- Restore Chain: 1 Full + 1 Diff(s) + 2 Log(s)", script);
        Assert.Contains("-- Restore script complete.", script);
        Assert.DoesNotContain("Point-in-Time:", script); // no StopAt requested
    }

    [Fact]
    public void Generate_StatementsSeparatedByGo()
    {
        var script = _generator.Generate(FullDiffLogChain(), Options(o => o.DisconnectSessions = true));
        var goCount = Lines(script).Count(l => l.Trim() == "GO");

        // disconnect + full + diff + 2 logs + reconnect
        Assert.Equal(6, goCount);
    }

    [Fact]
    public void Generate_NeverEmbedsCredentials()
    {
        // The options object no longer carries a SAS token or a credential name at all (#42), so
        // the strongest thing left to assert is the contract itself: nothing that could carry a
        // secret reaches the script. WITH CREDENTIAL is separately wrong for a SAS credential -
        // SQL Server rejects it with Msg 3225 and matches by URL instead (#60).
        var script = _generator.Generate(FullDiffLogChain(), Options());

        Assert.DoesNotContain("CREDENTIAL", script);
        Assert.DoesNotContain("sig=", script);
        Assert.DoesNotContain("sv=", script);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
