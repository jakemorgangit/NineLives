namespace Blackcat.NineLives.Models;

/// <summary>How a console line should read at a glance.</summary>
public enum ConsoleLineKind
{
    /// <summary>Ordinary output.</summary>
    Normal,

    /// <summary>A step starting - the app narrating what it is about to do.</summary>
    Step,

    /// <summary>Something completed.</summary>
    Success,

    /// <summary>Worth noticing but not fatal.</summary>
    Warning,

    /// <summary>The restore failed or was stopped.</summary>
    Error
}

/// <summary>
/// One line in the execution console.
///
/// A collection of these replaced a single ever-growing string. Appending to a bound string
/// rebuilds the whole thing and re-renders the entire TextBox on every message, which is O(n^2)
/// and was a large part of why a restore emitting progress every few percent looked like it was
/// arriving in bursts rather than live.
/// </summary>
/// <param name="Text">The line as written.</param>
/// <param name="Kind">Drives the colour, nothing else.</param>
public sealed record ConsoleLine(string Text, ConsoleLineKind Kind = ConsoleLineKind.Normal)
{
    /// <summary>
    /// Classifies a raw message so the console reads at a glance without every call site having to
    /// think about it. Deliberately conservative: anything unrecognised stays Normal rather than
    /// guessing and colouring an ordinary line red.
    /// </summary>
    public static ConsoleLine From(string message)
    {
        var trimmed = message.TrimStart();

        if (trimmed.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("CANCELLED", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("FAILED", StringComparison.OrdinalIgnoreCase))
            return new ConsoleLine(message, ConsoleLineKind.Error);

        if (trimmed.StartsWith("Note:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Warning", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("is in RESTORING", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("SINGLE_USER", StringComparison.OrdinalIgnoreCase))
            return new ConsoleLine(message, ConsoleLineKind.Warning);

        if (trimmed.Contains("completed successfully", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Completed", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("100 percent processed", StringComparison.OrdinalIgnoreCase))
            return new ConsoleLine(message, ConsoleLineKind.Success);

        if (trimmed.StartsWith("Executing statement", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Beginning", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Running:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Using the existing", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Credential", StringComparison.OrdinalIgnoreCase))
            return new ConsoleLine(message, ConsoleLineKind.Step);

        return new ConsoleLine(message);
    }
}
