using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

public class TSqlTests
{
    // ── QuoteName ────────────────────────────────────────────────────────────────

    [Fact]
    public void QuoteName_PlainName_IsBracketed()
        => Assert.Equal("[MyDb]", TSql.QuoteName("MyDb"));

    [Fact]
    public void QuoteName_NameWithSpaces_IsBracketed()
        => Assert.Equal("[My Db]", TSql.QuoteName("My Db"));

    [Fact]
    public void QuoteName_EmbeddedCloseBracket_IsDoubled()
    {
        // The bug: [My]DB] parses as identifier `My` followed by stray text.
        Assert.Equal("[My]]DB]", TSql.QuoteName("My]DB"));
    }

    [Fact]
    public void QuoteName_MultipleCloseBrackets_AllDoubled()
        => Assert.Equal("[a]]b]]c]", TSql.QuoteName("a]b]c"));

    [Fact]
    public void QuoteName_AlreadyBracketed_IsUnwrappedAndRequoted()
    {
        // A user who types [My Db] means the database My Db, not `[My Db]`.
        Assert.Equal("[My Db]", TSql.QuoteName("[My Db]"));
    }

    [Fact]
    public void QuoteName_IsIdempotent()
    {
        var once = TSql.QuoteName("My]DB");
        Assert.Equal(once, TSql.QuoteName(once));
    }

    [Fact]
    public void QuoteName_OpenBracketOnly_IsTreatedAsPartOfTheName()
    {
        // Not a wrapped name (no trailing ]), so it is data.
        Assert.Equal("[[MyDb]", TSql.QuoteName("[MyDb"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void QuoteName_EmptyInput_UsesFallback(string? input)
        => Assert.Equal("[DatabaseName]", TSql.QuoteName(input, fallback: "DatabaseName"));

    [Fact]
    public void QuoteName_Trims()
        => Assert.Equal("[MyDb]", TSql.QuoteName("  MyDb  "));

    [Fact]
    public void QuoteName_InjectionShapedName_IsNeutralised()
    {
        // The payload from the security report: without doubling this closes the bracket and
        // opens a new statement. With doubling it stays a single (absurd) identifier.
        const string payload =
            "https://acct.blob.core.windows.net/backups] WITH IDENTITY='SHARED ACCESS SIGNATURE', " +
            "SECRET='z'; ALTER SERVER ROLE sysadmin ADD MEMBER [svc]; CREATE CREDENTIAL [decoy";

        var quoted = TSql.QuoteName(payload);

        Assert.StartsWith("[", quoted);
        Assert.EndsWith("]", quoted);
        // Every ] from the payload survives as a doubled pair, so none of them terminates
        // the identifier: the only single ] in the result is the final delimiter.
        Assert.Equal(1, CountUnpairedCloseBrackets(quoted));
    }

    [Fact]
    public void QuoteName_Ipv6EndpointUrl_IsQuotedNotBroken()
    {
        // A legal container URL for an emulator; the ] is data, not syntax.
        Assert.Equal(
            "[https://[fe80::1]]:10000/devstoreaccount1/backups]",
            TSql.QuoteName("https://[fe80::1]:10000/devstoreaccount1/backups"));
    }

    // ── EscapeLiteral ────────────────────────────────────────────────────────────

    [Fact]
    public void EscapeLiteral_Apostrophe_IsDoubled()
        => Assert.Equal("O''Brien", TSql.EscapeLiteral("O'Brien"));

    [Fact]
    public void EscapeLiteral_NoApostrophe_Unchanged()
        => Assert.Equal(@"D:\Data\db.mdf", TSql.EscapeLiteral(@"D:\Data\db.mdf"));

    [Fact]
    public void EscapeLiteral_LiteralTerminatorPayload_IsNeutralised()
    {
        // The apostrophe that would have closed the literal early is doubled, so the payload
        // stays inside the string rather than becoming statement structure.
        Assert.Equal("x''; DROP TABLE t; --", TSql.EscapeLiteral("x'; DROP TABLE t; --"));
    }

    [Fact]
    public void EscapeLiteral_ResultHasNoUnpairedApostrophe()
    {
        // The property that actually matters: every apostrophe in the output is part of a pair,
        // so none of them can terminate the surrounding literal.
        var escaped = TSql.EscapeLiteral("a'b''c'''d");
        Assert.Equal(0, CountUnpairedApostrophes(escaped));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EscapeLiteral_NullOrEmpty_ReturnsEmpty(string? input)
        => Assert.Equal(string.Empty, TSql.EscapeLiteral(input));

    // ── UnquoteName ──────────────────────────────────────────────────────────────

    [Fact]
    public void UnquoteName_Bracketed_ReturnsBareName()
        => Assert.Equal("My Db", TSql.UnquoteName("[My Db]"));

    [Fact]
    public void UnquoteName_BracketedWithDoubledBracket_Unescapes()
        => Assert.Equal("My]DB", TSql.UnquoteName("[My]]DB]"));

    [Fact]
    public void UnquoteName_Unbracketed_ReturnsAsIs()
        => Assert.Equal("MyDb", TSql.UnquoteName("MyDb"));

    // ── ValidateIdentifier ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("MyDb")]
    [InlineData("https://acct.blob.core.windows.net/backups")]
    [InlineData("name]with]brackets")]
    public void ValidateIdentifier_AcceptableNames_DoNotThrow(string name)
        => TSql.ValidateIdentifier(name, "test");

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("carriage\rreturn")]
    [InlineData("tab\there")]
    [InlineData("null\0char")]
    public void ValidateIdentifier_ControlCharacters_Throw(string name)
        => Assert.Throws<ArgumentException>(() => TSql.ValidateIdentifier(name, "test"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateIdentifier_EmptyName_Throws(string? name)
        => Assert.Throws<ArgumentException>(() => TSql.ValidateIdentifier(name, "test"));

    private static int CountUnpairedApostrophes(string value)
    {
        int count = 0;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\'') continue;
            if (i + 1 < value.Length && value[i + 1] == '\'') { i++; continue; }
            count++;
        }
        return count;
    }

    private static int CountUnpairedCloseBrackets(string value)
    {
        int count = 0;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != ']') continue;
            if (i + 1 < value.Length && value[i + 1] == ']') { i++; continue; }
            count++;
        }
        return count;
    }
}
