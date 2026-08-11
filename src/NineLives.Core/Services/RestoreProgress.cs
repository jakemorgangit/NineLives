using System.Text.RegularExpressions;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Turns SQL Server's own progress prose into a number (#204).
///
/// The scripts run WITH STATS = 10, so the server reports "10 percent processed." as each
/// statement advances, and "RESTORE DATABASE successfully processed ..." as each one finishes.
/// Those lines were scrolling into the console as text and nothing more - a 40-minute restore
/// looked identical at 5% and at 95% unless somebody read the log. The number was always there;
/// this makes it a number again.
///
/// A chain is several statements, so overall progress is statements-completed plus the current
/// statement's fraction, over the total. Weighting by statement rather than by bytes is knowingly
/// approximate - a 200 GB full and a 2 MB log weigh the same - but honest per-statement text
/// ("statement 2 of 5") keeps the bar from promising precision it does not have.
/// </summary>
public sealed partial class RestoreProgress
{
    [GeneratedRegex(@"^\s*(\d{1,3})\s+percent\s+processed\.?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PercentLine();

    [GeneratedRegex(@"RESTORE\s+(DATABASE|LOG)\s+successfully\s+processed", RegexOptions.IgnoreCase)]
    private static partial Regex CompletionLine();

    [GeneratedRegex(@"^\s*RESTORE\s+(DATABASE|LOG)\s", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex RestoreStatement();

    /// <summary>
    /// How many RESTORE statements the script will run - the denominator.
    ///
    /// Counted from the script itself rather than passed in, so the number can never disagree
    /// with what actually executes. VERIFYONLY, HEADERONLY and FILELISTONLY do not match: they
    /// report no STATS and would dilute the total with statements that finish in one line.
    /// </summary>
    public static int CountStatements(string script) => RestoreStatement().Matches(script).Count;

    public RestoreProgress(int totalStatements)
    {
        TotalStatements = Math.Max(1, totalStatements);
    }

    public int TotalStatements { get; }

    public int StatementsCompleted { get; private set; }

    /// <summary>The current statement's own percent, 0-100, as the server last reported it.</summary>
    public int CurrentStatementPercent { get; private set; }

    /// <summary>
    /// Feeds one console line. Returns true when it changed anything, so the caller can skip
    /// re-rendering for the vast majority of lines, which are ordinary output.
    /// </summary>
    public bool Feed(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        var percent = PercentLine().Match(line);
        if (percent.Success && int.TryParse(percent.Groups[1].Value, out var value))
        {
            CurrentStatementPercent = Math.Clamp(value, 0, 100);
            return true;
        }

        if (CompletionLine().IsMatch(line))
        {
            // The server does not always say "100 percent" before the completion line - a small
            // statement can finish without ever reporting - so completion IS the 100.
            StatementsCompleted = Math.Min(TotalStatements, StatementsCompleted + 1);
            CurrentStatementPercent = 0;
            return true;
        }

        return false;
    }

    /// <summary>Overall progress, 0-100, across the whole chain.</summary>
    public double OverallPercent =>
        Math.Min(100.0,
            (StatementsCompleted + CurrentStatementPercent / 100.0) / TotalStatements * 100.0);

    /// <summary>
    /// What to print next to the bar. Statement-aware only when there are several, because
    /// "statement 1 of 1" is noise wearing a number.
    /// </summary>
    public string Describe()
    {
        var current = Math.Min(TotalStatements, StatementsCompleted + 1);

        return TotalStatements == 1
            ? $"{CurrentStatementPercent}%"
            : StatementsCompleted >= TotalStatements
                ? $"All {TotalStatements} statements complete"
                : $"Statement {current} of {TotalStatements} - {CurrentStatementPercent}%";
    }
}
