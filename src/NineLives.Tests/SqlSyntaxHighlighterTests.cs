using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// T-SQL tokenising for the script pane.
///
/// The one property that must always hold is that concatenating the tokens reproduces the input
/// exactly - the pane displays the script someone is about to run against production, and it must
/// not be able to show something different from what will execute. Colour being wrong is cosmetic;
/// text being wrong is not.
/// </summary>
public class SqlSyntaxHighlighterTests
{
    private static string Rebuild(string sql)
        => string.Concat(SqlSyntaxHighlighter.Tokenise(sql).Select(t => t.Text));

    [Theory]
    [InlineData("RESTORE DATABASE [MyDb] FROM URL = N'https://acct/backups/x.bak' WITH REPLACE, STATS = 10")]
    [InlineData("-- a comment\r\nRESTORE LOG [MyDb]\r\n  FROM URL = N'x'\r\nGO\r\n")]
    [InlineData("SECRET = 'abc''def'")]
    [InlineData("")]
    [InlineData("   \r\n\t  ")]
    [InlineData("/* block\ncomment */ SELECT 1")]
    public void TheTokensAlwaysReproduceTheInputExactly(string sql)
        => Assert.Equal(sql, Rebuild(sql));

    [Fact]
    public void KeywordsAreRecognised()
    {
        var tokens = SqlSyntaxHighlighter.Tokenise("RESTORE DATABASE [MyDb] WITH REPLACE");

        Assert.Contains(tokens, t => t.Text == "RESTORE" && t.Kind == SqlTokenKind.Keyword);
        Assert.Contains(tokens, t => t.Text == "DATABASE" && t.Kind == SqlTokenKind.Keyword);
        Assert.Contains(tokens, t => t.Text == "REPLACE" && t.Kind == SqlTokenKind.Keyword);
    }

    [Fact]
    public void KeywordsAreCaseInsensitive()
    {
        var tokens = SqlSyntaxHighlighter.Tokenise("restore Database");

        Assert.All(tokens.Where(t => t.Text.Trim().Length > 0),
            t => Assert.Equal(SqlTokenKind.Keyword, t.Kind));
    }

    [Fact]
    public void BracketedIdentifiersAreTheirOwnKind()
    {
        var tokens = SqlSyntaxHighlighter.Tokenise("RESTORE DATABASE [My Db]");

        Assert.Contains(tokens, t => t.Text == "[My Db]" && t.Kind == SqlTokenKind.Identifier);
    }

    [Fact]
    public void AnIdentifierContainingADoubledBracketStaysOneToken()
    {
        // TSql.QuoteName doubles a ']', so this shape reaches the pane for real.
        var tokens = SqlSyntaxHighlighter.Tokenise("[My]]Db]");

        Assert.Contains(tokens, t => t.Text == "[My]]Db]" && t.Kind == SqlTokenKind.Identifier);
    }

    [Fact]
    public void StringLiteralsIncludeTheNPrefixAndDoubledQuotes()
    {
        var tokens = SqlSyntaxHighlighter.Tokenise("SECRET = N'it''s here'");

        Assert.Contains(tokens, t => t.Text == "N'it''s here'" && t.Kind == SqlTokenKind.Literal);
    }

    [Fact]
    public void AKeywordInsideAStringIsNotColouredAsCode()
    {
        // A blob URL contains "backups" and could contain anything; it is a literal, not SQL.
        var tokens = SqlSyntaxHighlighter.Tokenise("FROM URL = N'https://acct/RESTORE/DATABASE.bak'");

        Assert.DoesNotContain(tokens, t => t.Text.Contains("https") && t.Kind == SqlTokenKind.Keyword);
        Assert.Contains(tokens, t => t.Kind == SqlTokenKind.Literal && t.Text.Contains("RESTORE"));
    }

    [Fact]
    public void AKeywordInsideACommentIsNotColouredAsCode()
    {
        var tokens = SqlSyntaxHighlighter.Tokenise("-- RESTORE DATABASE goes here\nSELECT 1");

        var comment = Assert.Single(tokens, t => t.Kind == SqlTokenKind.Comment);
        Assert.Contains("RESTORE DATABASE", comment.Text);
    }

    [Fact]
    public void NumbersAreRecognised()
    {
        var tokens = SqlSyntaxHighlighter.Tokenise("WITH STATS = 10");

        Assert.Contains(tokens, t => t.Text == "10" && t.Kind == SqlTokenKind.Number);
    }

    [Fact]
    public void OrdinaryWordsStayPlain()
    {
        var tokens = SqlSyntaxHighlighter.Tokenise("MyDatabaseName");

        Assert.Contains(tokens, t => t.Text == "MyDatabaseName" && t.Kind == SqlTokenKind.Plain);
    }

    [Fact]
    public void ARealisticRestoreScriptTokenisesWithoutLosingAnything()
    {
        const string script = """
            -- ============================================
            -- Nine Lives - generated restore script
            -- ============================================
            USE [master];
            GO

            RESTORE DATABASE [MyDb]
            FROM URL = N'https://acct.blob.core.windows.net/backups/FULL/SRV01/MyDb/x.bak'
            WITH NORECOVERY, REPLACE, STATS = 10;
            GO
            """;

        Assert.Equal(script, Rebuild(script));
        Assert.Contains(SqlSyntaxHighlighter.Tokenise(script), t => t.Kind == SqlTokenKind.Comment);
        Assert.Contains(SqlSyntaxHighlighter.Tokenise(script), t => t.Kind == SqlTokenKind.Identifier);
        Assert.Contains(SqlSyntaxHighlighter.Tokenise(script), t => t.Kind == SqlTokenKind.Literal);
    }
}
