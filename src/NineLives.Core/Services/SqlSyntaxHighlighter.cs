using System.Text.RegularExpressions;

namespace Blackcat.NineLives.Services;

/// <summary>What a span of a T-SQL script is, for colouring.</summary>
public enum SqlTokenKind
{
    Plain,
    Keyword,
    /// <summary>A string literal, N'...' included.</summary>
    Literal,
    Comment,
    Number,
    /// <summary>A [bracketed identifier] - the database and credential names.</summary>
    Identifier
}

/// <summary>One coloured span.</summary>
public sealed record SqlToken(string Text, SqlTokenKind Kind);

/// <summary>
/// Splits a T-SQL script into coloured spans.
///
/// Hand-rolled rather than pulling in an editor component. The script pane is read-only and a few
/// kilobytes at most, so a tokeniser is a hundred lines against a dependency the single-file exe
/// would have to carry - and #16 pinned the dependency graph deliberately.
///
/// Not a parser and does not need to be. It gets comments, strings, bracketed identifiers, numbers
/// and keywords right, in that order of precedence, which is everything a restore script contains.
/// Anything it cannot classify stays plain, so the worst case is uncoloured text rather than
/// mangled text.
/// </summary>
public static class SqlSyntaxHighlighter
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "RESTORE", "DATABASE", "LOG", "FROM", "URL", "DISK", "WITH", "FILE", "MOVE", "TO",
        "REPLACE", "RECOVERY", "NORECOVERY", "STANDBY", "STATS", "STOPAT", "CHECKSUM",
        "CONTINUE_AFTER_ERROR", "KEEP_REPLICATION", "ENABLE_BROKER", "NEW_BROKER",
        "ERROR_BROKER_CONVERSATIONS", "CREDENTIAL", "HEADERONLY", "FILELISTONLY", "VERIFYONLY",
        "ALTER", "SET", "SINGLE_USER", "MULTI_USER", "ROLLBACK", "IMMEDIATE", "GO",
        "CREATE", "DROP", "IDENTITY", "SECRET", "USE", "MASTER", "IF", "EXISTS", "BEGIN", "END",
        "SELECT", "WHERE", "AND", "OR", "NOT", "NULL", "AS", "ON", "OFF", "PRINT", "DECLARE",
        "EXEC", "NORECOVERY,", "MEDIANAME", "MEDIADESCRIPTION", "BLOCKSIZE", "MAXTRANSFERSIZE"
    };

    // Order matters: comments and strings first, so a keyword inside either is not coloured as
    // code, and a bracketed identifier containing a quote does not open a string.
    private static readonly Regex Tokeniser = new(
        @"(?<comment>--[^\r\n]*|/\*.*?\*/)" +
        @"|(?<literal>N?'(?:[^']|'')*')" +
        @"|(?<identifier>\[(?:[^\]]|\]\])*\])" +
        @"|(?<number>\b\d+(?:\.\d+)?\b)" +
        @"|(?<word>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Splits the script. Concatenating the results reproduces the input exactly.</summary>
    public static IReadOnlyList<SqlToken> Tokenise(string? sql)
    {
        if (string.IsNullOrEmpty(sql)) return [];

        var tokens = new List<SqlToken>();
        var position = 0;

        foreach (Match match in Tokeniser.Matches(sql))
        {
            if (match.Index > position)
                tokens.Add(new SqlToken(sql[position..match.Index], SqlTokenKind.Plain));

            var kind = SqlTokenKind.Plain;
            if (match.Groups["comment"].Success) kind = SqlTokenKind.Comment;
            else if (match.Groups["literal"].Success) kind = SqlTokenKind.Literal;
            else if (match.Groups["identifier"].Success) kind = SqlTokenKind.Identifier;
            else if (match.Groups["number"].Success) kind = SqlTokenKind.Number;
            else if (match.Groups["word"].Success)
                kind = Keywords.Contains(match.Value) ? SqlTokenKind.Keyword : SqlTokenKind.Plain;

            tokens.Add(new SqlToken(match.Value, kind));
            position = match.Index + match.Length;
        }

        if (position < sql.Length)
            tokens.Add(new SqlToken(sql[position..], SqlTokenKind.Plain));

        return tokens;
    }
}
