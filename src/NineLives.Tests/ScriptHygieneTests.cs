using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Names cannot smuggle statements past the person reading the script (#294). sysname permits
/// control characters and line breaks, and the restore target is free text - so a name
/// containing a newline used to terminate a header comment and hand the remainder to the
/// server as executable text, breaking the read-the-script-before-it-runs property every
/// screen rests on. And names that came FROM the server are quoted exactly: QuoteName's
/// bracket unwrapping is for what people TYPE.
/// </summary>
public class ScriptHygieneTests
{
    // ── comments describe, they do not execute ──────────────────────────────────

    [Fact]
    public void CommentTextFlattensControlCharacters()
    {
        Assert.Equal("MyDb  DROP DATABASE x", TSql.CommentText("MyDb\r\nDROP DATABASE x"));
        Assert.Equal("tab here", TSql.CommentText("tab\there"));
        Assert.Equal(string.Empty, TSql.CommentText(null));
    }

    [Fact]
    public void ANewlineInTheRestoreTargetCannotEscapeTheHeaderComment()
    {
        var chain = new BackupChain
        {
            FullSet = new BackupSet
            {
                DatabaseName = "MyDb",
                Type = BackupType.Full,
                Timestamp = new DateTime(2026, 8, 1, 22, 0, 0),
                Files = [new BackupFileInfo
                {
                    BlobName = "FULL/SRV01/MyDb/20260801_220000.bak",
                    BlobUrl = "https://acct.blob.core.windows.net/backups/FULL/SRV01/MyDb/20260801_220000.bak",
                    Type = BackupType.Full
                }]
            }
        };
        var script = new RestoreScriptGenerator().Generate(chain, new RestoreOptions
        {
            TargetDatabaseName = "MyDb\nDROP DATABASE [Payroll]--"
        });

        // The header names the target on ONE commented line - the newline became a space.
        Assert.Contains("-- Target Database: MyDb DROP DATABASE [Payroll]--", script);

        // Everywhere else the name travels as DATA: inside DB_ID's string literal and inside
        // a bracketed identifier, where a newline is legal and inert. The hostile text never
        // stands alone as a statement.
        Assert.Contains("DB_ID('MyDb\nDROP DATABASE [Payroll]--')", script);
        Assert.Contains("SET SINGLE_USER", script);
    }

    [Fact]
    public void ANewlineInTheBackupDatabaseNameCannotEscapeTheHeaderComment()
    {
        var script = new BackupScriptGenerator().Generate(new BackupOptions
        {
            DatabaseName = "MyDb\nGO\nDROP DATABASE [Payroll]",
            Destinations = [@"\\nas01\sql\MyDb.bak"],
            Medium = BackupMedium.SharedPath
        });

        Assert.Contains("-- Database:  MyDb GO DROP DATABASE [Payroll]", script);
    }

    // ── server-sourced names are quoted exactly (#294) ──────────────────────────

    [Fact]
    public void QuoteNameExactRoundTripsANameThatLooksQuoted()
    {
        // Typed [My Db] means My Db - QuoteName unwraps. A database GENUINELY named
        // [Archive] must not become a different database on the way through.
        Assert.Equal("[My Db]", TSql.QuoteName("[My Db]"));
        Assert.Equal("[[Archive]]]", TSql.QuoteNameExact("[Archive]"));
        Assert.Equal("[Plain]", TSql.QuoteNameExact("Plain"));
    }

    [Fact]
    public void ABackupOfABracketNamedDatabaseTargetsTheRightDatabase()
    {
        var script = new BackupScriptGenerator().Generate(new BackupOptions
        {
            DatabaseName = "[Archive]",
            Destinations = [@"\\nas01\sql\Archive.bak"],
            Medium = BackupMedium.SharedPath
        });

        Assert.Contains("BACKUP DATABASE [[Archive]]]", script);
    }

    [Fact]
    public void TheOrphanFixQuotesTheDatabasesOwnUserExactly()
    {
        var action = PostRestoreAdvice.FixOrphan("MyDb", new OrphanedUser("[svc]", HasSameNamedLogin: true));

        Assert.Contains("ALTER USER [[svc]]] WITH LOGIN = [[svc]]]", action.Sql);
    }
}
