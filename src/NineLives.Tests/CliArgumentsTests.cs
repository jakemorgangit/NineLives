using Blackcat.NineLives.Cli;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The CLI's parser (#63 step 2). Hand-rolled and schema-checked, so the rules a pipeline
/// depends on are pinned here rather than living in a package's changelog: options are
/// validated against what the verb declares, errors accumulate, and timestamps parse exactly
/// or not at all.
/// </summary>
public class CliArgumentsTests
{
    private static readonly VerbSpec Spec = new(
        "points", "summary", "usage",
        Valued: ["container", "database", "at"],
        Switches: ["json"],
        Options: [], Notes: [], ExitCodes: [], Examples: []);

    [Fact]
    public void ValuedOptionsAndSwitchesParse()
    {
        var args = CliArguments.Parse(
            ["--container", "backups", "--database", "Sales", "--json"], Spec);

        Assert.True(args.Ok);
        Assert.Equal("backups", args.Get("container"));
        Assert.Equal("Sales", args.Get("database"));
        Assert.True(args.Has("json"));
        Assert.False(args.Has("quiet"));
    }

    [Fact]
    public void AnOptionTheVerbDoesNotTakeIsAnError()
    {
        var args = CliArguments.Parse(["--nonsense", "x"], Spec);

        Assert.False(args.Ok);
        Assert.Contains(args.Errors, e => e.Contains("--nonsense"));
    }

    [Fact]
    public void AValuedOptionWithoutItsValueIsAnError()
    {
        var args = CliArguments.Parse(["--database"], Spec);

        Assert.False(args.Ok);
        Assert.Contains(args.Errors, e => e.Contains("--database") && e.Contains("value"));
    }

    /// <summary>A switch swallowing the next token would corrupt the invocation silently.</summary>
    [Fact]
    public void AStrayValueAfterASwitchIsAnErrorNotSwallowed()
    {
        var args = CliArguments.Parse(["--json", "Sales"], Spec);

        Assert.False(args.Ok);
        Assert.Contains(args.Errors, e => e.Contains("'Sales'"));
    }

    [Fact]
    public void ErrorsAccumulateRatherThanStoppingAtTheFirst()
    {
        var args = CliArguments.Parse(["--bad1", "--bad2"], Spec);

        Assert.Equal(2, args.Errors.Count);
    }

    [Fact]
    public void TheSameOptionTwiceIsAnError()
    {
        var args = CliArguments.Parse(
            ["--database", "A", "--database", "B"], Spec);

        Assert.False(args.Ok);
        Assert.Contains(args.Errors, e => e.Contains("twice"));
    }

    // ── time parsing ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-08-02 19:00:00")]
    [InlineData("2026-08-02 19:00")]
    [InlineData("2026-08-02")]
    public void ExactInvariantFormatsParse(string text)
        => Assert.NotNull(CliArguments.ParseTime(text));

    /// <summary>
    /// The ambiguity a restore tool cannot afford: whether "01/02" is January or February
    /// depends on the host's culture, so culture-shaped strings are refused outright.
    /// </summary>
    [Theory]
    [InlineData("01/02/2026")]
    [InlineData("02-08-2026 19:00")]
    [InlineData("19:00")]
    [InlineData("yesterday")]
    public void CultureShapedTimesAreRefused(string text)
        => Assert.Null(CliArguments.ParseTime(text));
}
