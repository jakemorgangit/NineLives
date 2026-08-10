namespace Blackcat.NineLives.Services;

/// <summary>
/// Quoting helpers for the T-SQL this app generates and executes.
///
/// Every identifier and string literal that reaches a command must go through here. Two of the
/// values involved are free text the user can type (the target database name and the STANDBY
/// path) and one is auto-populated from a container URL that round-trips through plain-text
/// config.json - so "it will always look like a URL" is not a safety property we can rely on.
/// </summary>
public static class TSql
{
    /// <summary>
    /// Wraps an identifier in brackets, doubling any embedded <c>]</c> so the brackets actually
    /// delimit it. Without the doubling, <c>My]DB</c> yields <c>[My]DB]</c>, which SQL Server
    /// parses as the identifier <c>My</c> followed by stray text - a syntax error at best, and
    /// an injection point where the identifier is concatenated into a larger statement.
    ///
    /// A value that is already bracketed is unwrapped first and then re-quoted, so a user who
    /// types <c>[My Db]</c> gets the database they meant and the function stays idempotent.
    /// </summary>
    public static string QuoteName(string? name, string fallback = "")
    {
        var raw = Unwrap(name);
        if (string.IsNullOrWhiteSpace(raw))
            return string.IsNullOrEmpty(fallback) ? "[]" : QuoteName(fallback);

        return $"[{raw.Replace("]", "]]")}]";
    }

    /// <summary>
    /// Escapes a value for use inside a single-quoted T-SQL string literal (does not add the
    /// surrounding quotes). Applies to SAS secrets, blob URLs, STANDBY paths and MOVE targets.
    /// </summary>
    public static string EscapeLiteral(string? value)
        => (value ?? string.Empty).Replace("'", "''");

    /// <summary>
    /// Returns the bare identifier with any bracket quoting removed - for when a name is needed
    /// as DATA rather than as an identifier, e.g. inside <c>DB_ID('...')</c>. Callers must still
    /// pass the result through <see cref="EscapeLiteral"/>.
    /// </summary>
    public static string UnquoteName(string? name) => Unwrap(name);

    /// <summary>
    /// Quotes an identifier that came FROM the server exactly as it is (#294).
    /// <see cref="QuoteName"/>'s bracket unwrapping is right for TYPED names - somebody typing
    /// [My Db] means My Db - but a database genuinely named [Archive] (sys.databases says so)
    /// must round-trip as [[Archive]]], not silently become a different database.
    /// </summary>
    public static string QuoteNameExact(string? name) =>
        $"[{(name ?? string.Empty).Replace("]", "]]")}]";

    /// <summary>
    /// A value about to be interpolated into a <c>--</c> comment (#294). sysname permits
    /// control characters and line breaks, and a name containing a newline would terminate
    /// the comment and hand the rest of the line to the server as executable text - breaking
    /// the read-the-script-before-it-runs property every screen rests on. Control characters
    /// become spaces: the comment describes; the statements below quote properly.
    /// </summary>
    public static string CommentText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(char.IsControl(c) ? ' ' : c);
        return sb.ToString();
    }

    /// <summary>
    /// Rejects identifiers that cannot be safely quoted. Bracket doubling handles <c>]</c>, but
    /// control characters and line breaks have no business in a credential or database name and
    /// would let a value smuggle statement structure past a human reading the generated script.
    /// </summary>
    public static void ValidateIdentifier(string? name, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Value cannot be empty.", parameterName);

        foreach (var c in name)
        {
            if (char.IsControl(c))
                throw new ArgumentException(
                    "Value cannot contain control characters or line breaks.", parameterName);
        }
    }

    private static string Unwrap(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
            return trimmed[1..^1].Replace("]]", "]");

        return trimmed;
    }
}
