using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

public class TagPaletteTests
{
    // GitHub's label swatches, which the palette draws from.
    private const string GitHubRed = "#B60205";
    private const string GitHubOrange = "#D93F0B";
    private const string GitHubYellow = "#FBCA04";
    private const string GitHubGreen = "#0E8A16";
    private const string GitHubBlue = "#1D76DB";

    private static readonly string[] AllSwatches =
    [
        "#B60205", "#D93F0B", "#FBCA04", "#0E8A16",
        "#006B75", "#1D76DB", "#0052CC", "#5319E7"
    ];

    // ── semantic colours ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("prod")]
    [InlineData("production")]
    [InlineData("live")]
    [InlineData("PROD")]
    [InlineData("  Prod  ")]
    public void ColorFor_ProductionNames_AreRed(string tag)
        => Assert.Equal(GitHubRed, TagPalette.ColorFor(tag));

    [Fact]
    public void ColorFor_EnvironmentNames_UseTheirConventionalColours()
    {
        Assert.Equal(GitHubOrange, TagPalette.ColorFor("dr"));
        Assert.Equal(GitHubYellow, TagPalette.ColorFor("uat"));
        Assert.Equal(GitHubGreen, TagPalette.ColorFor("test"));
        Assert.Equal(GitHubBlue, TagPalette.ColorFor("dev"));
    }

    [Fact]
    public void IsProductionLike_OnlyTrueForProductionNames()
    {
        Assert.True(TagPalette.IsProductionLike("prod"));
        Assert.True(TagPalette.IsProductionLike("PRODUCTION"));
        Assert.True(TagPalette.IsProductionLike("live"));

        Assert.False(TagPalette.IsProductionLike("test"));
        Assert.False(TagPalette.IsProductionLike("prod-eu"));
        Assert.False(TagPalette.IsProductionLike(null));
    }

    [Fact]
    public void ColorFor_PartialEnvironmentName_FallsThroughToHashing()
    {
        // "prod-eu" is NOT matched semantically. Better to hash it than to imply a safety
        // signal from a name that only resembles one.
        Assert.NotEqual(GitHubRed, TagPalette.ColorFor("prod-eu"));
        Assert.False(TagPalette.IsProductionLike("prod-eu"));
    }

    // ── hashed colours ───────────────────────────────────────────────────────────

    [Fact]
    public void ColorFor_ArbitraryTag_IsFromTheGitHubPalette()
        => Assert.Contains(TagPalette.ColorFor("client-acme"), AllSwatches);

    [Fact]
    public void ColorFor_IsStableAcrossCalls()
    {
        var first = TagPalette.ColorFor("finance-cluster");
        for (int i = 0; i < 50; i++)
            Assert.Equal(first, TagPalette.ColorFor("finance-cluster"));
    }

    [Fact]
    public void ColorFor_IsCaseInsensitive()
        => Assert.Equal(TagPalette.ColorFor("Reporting"), TagPalette.ColorFor("REPORTING"));

    [Fact]
    public void ColorFor_KnownValue_IsPinned()
    {
        // Pins the hash itself. If the algorithm is ever swapped, every existing tag silently
        // changes colour - which users notice and dislike. This test makes that deliberate.
        // The value is whatever FNV-1a currently produces; it is recorded, not chosen.
        Assert.Equal("#0E8A16", TagPalette.ColorFor("client-acme"));
    }

    [Fact]
    public void ColorFor_DistributesAcrossThePalette()
    {
        // A hash that collapsed onto one or two swatches would make every tag look the same.
        var used = Enumerable.Range(0, 60)
            .Select(i => TagPalette.ColorFor($"tag-{i}"))
            .Distinct()
            .ToList();

        Assert.True(used.Count >= 6, $"Only {used.Count} of {AllSwatches.Length} swatches used.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ColorFor_EmptyInput_StillReturnsAValidSwatch(string? tag)
        => Assert.Contains(TagPalette.ColorFor(tag), AllSwatches);

    // ── parsing ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseTags_SplitsTrimsAndDropsBlanks()
        => Assert.Equal(["prod", "eu-west", "critical"],
            TagPalette.ParseTags("  prod ,, eu-west ,  critical  "));

    [Fact]
    public void ParseTags_AcceptsSemicolonsToo()
        => Assert.Equal(["a", "b"], TagPalette.ParseTags("a; b"));

    [Fact]
    public void ParseTags_RemovesDuplicatesKeepingTheFirstSpelling()
        => Assert.Equal(["Prod"], TagPalette.ParseTags("Prod, prod, PROD"));

    [Fact]
    public void ParseTags_PreservesOrder()
        => Assert.Equal(["zebra", "apple"], TagPalette.ParseTags("zebra, apple"));

    [Fact]
    public void ParseTags_TruncatesOverlongTags()
    {
        var tag = Assert.Single(TagPalette.ParseTags(new string('x', 100)));
        Assert.Equal(32, tag.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" , , ")]
    public void ParseTags_NothingUsable_ReturnsEmpty(string? input)
        => Assert.Empty(TagPalette.ParseTags(input));

    [Fact]
    public void FormatTags_RoundTripsThroughParse()
    {
        var original = new[] { "prod", "eu-west", "critical" };
        Assert.Equal(original, TagPalette.ParseTags(TagPalette.FormatTags(original)));
    }

    [Fact]
    public void FormatTags_Null_IsEmptyString()
        => Assert.Equal(string.Empty, TagPalette.FormatTags(null));
}
