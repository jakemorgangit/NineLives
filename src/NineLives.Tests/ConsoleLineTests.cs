using Blackcat.NineLives.Models;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Classification of console output.
///
/// Only drives colour, so being wrong is cosmetic - but colouring an ordinary line red during a
/// restore is its own kind of wrong, which is why anything unrecognised stays Normal rather than
/// being guessed at.
/// </summary>
public class ConsoleLineTests
{
    [Theory]
    [InlineData("ERROR: Could not open backup device")]
    [InlineData("CANCELLED. The statement in flight was rolled back")]
    [InlineData("FAILED on statement 3 of 12: RESTORE LOG")]
    public void FailuresReadAsErrors(string message)
        => Assert.Equal(ConsoleLineKind.Error, ConsoleLine.From(message).Kind);

    [Theory]
    [InlineData("  Note: this connection trusts the server certificate")]
    [InlineData("[MyDb] is in RESTORING state.")]
    [InlineData("[MyDb] is in SINGLE_USER.")]
    public void ThingsWorthNoticingReadAsWarnings(string message)
        => Assert.Equal(ConsoleLineKind.Warning, ConsoleLine.From(message).Kind);

    [Theory]
    [InlineData("Restore completed successfully!")]
    [InlineData("Completed.")]
    [InlineData("100 percent processed.")]
    public void CompletionReadsAsSuccess(string message)
        => Assert.Equal(ConsoleLineKind.Success, ConsoleLine.From(message).Kind);

    [Theory]
    [InlineData("Executing statement 1 of 4: RESTORE DATABASE")]
    [InlineData("Beginning restore execution...")]
    [InlineData("Credential [x] is missing - creating it...")]
    public void NarrationReadsAsASte(string message)
        => Assert.Equal(ConsoleLineKind.Step, ConsoleLine.From(message).Kind);

    [Theory]
    [InlineData("50 percent processed.")]
    [InlineData("RESTORE DATABASE successfully processed 1234 pages in 5.678 seconds")]
    [InlineData("")]
    public void AnythingElseStaysNormal(string message)
        => Assert.Equal(ConsoleLineKind.Normal, ConsoleLine.From(message).Kind);

    [Fact]
    public void TheTextIsKeptVerbatimIncludingLeadingSpace()
    {
        // Indentation carries meaning in the console - nested detail under a step - so it must
        // survive classification untouched.
        const string indented = "  Note: something";

        Assert.Equal(indented, ConsoleLine.From(indented).Text);
    }
}
