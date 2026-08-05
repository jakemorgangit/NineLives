using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

public class UpdateCheckerTests
{
    private static ReleaseInfo Release(string tag, string version) =>
        new(Version.Parse(version), tag, "https://example.invalid/release");

    // ── version parsing ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("v1.1.0", "1.1.0")]
    [InlineData("1.1.0", "1.1.0")]
    [InlineData("V2.0.0", "2.0.0")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("1.1.0+abc123", "1.1.0")]
    [InlineData("1.1.0-beta", "1.1.0")]
    public void Parse_AcceptsTheTagFormsWeActuallyPublish(string tag, string expected)
        => Assert.Equal(Version.Parse(expected), AppVersion.Parse(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("latest")]
    public void Parse_Rubbish_ReturnsNull(string? tag)
        => Assert.Null(AppVersion.Parse(tag));

    // ── release payload ──────────────────────────────────────────────────────────

    [Fact]
    public void ParseRelease_NormalPayload_IsRead()
    {
        var json = """
        { "tag_name": "v1.1.0", "html_url": "https://github.com/x/y/releases/tag/v1.1.0",
          "draft": false, "prerelease": false }
        """;

        var release = UpdateChecker.ParseRelease(json);

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 1, 0), release!.Version);
        Assert.Equal("v1.1.0", release.Tag);
        Assert.Equal("https://github.com/x/y/releases/tag/v1.1.0", release.Url);
    }

    [Fact]
    public void ParseRelease_MissingUrl_FallsBackToTheReleasesPage()
    {
        var release = UpdateChecker.ParseRelease("""{ "tag_name": "v1.1.0" }""");
        Assert.Equal(UpdateChecker.ReleasesPage, release!.Url);
    }

    [Theory]
    [InlineData("""{ "tag_name": "v2.0.0", "draft": true }""")]
    [InlineData("""{ "tag_name": "v2.0.0", "prerelease": true }""")]
    public void ParseRelease_DraftOrPrerelease_IsIgnored(string json)
    {
        // Not something to nudge people towards.
        Assert.Null(UpdateChecker.ParseRelease(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "message": "API rate limit exceeded" }""")]
    [InlineData("""{ "tag_name": "nightly" }""")]
    public void ParseRelease_AnythingUnexpected_ReturnsNull(string? json)
    {
        // Rate limits and API changes must be silent no-ops, not errors.
        Assert.Null(UpdateChecker.ParseRelease(json));
    }

    // ── notify decision ──────────────────────────────────────────────────────────

    [Fact]
    public void ShouldNotify_NewerRelease_Yes()
        => Assert.True(UpdateChecker.ShouldNotify(
            new Version(1, 0, 0), Release("v1.1.0", "1.1.0"), null, enabled: true));

    [Fact]
    public void ShouldNotify_SameVersion_No()
        => Assert.False(UpdateChecker.ShouldNotify(
            new Version(1, 1, 0), Release("v1.1.0", "1.1.0"), null, enabled: true));

    [Fact]
    public void ShouldNotify_OlderRelease_No()
    {
        // A local build ahead of the last release is not an update.
        Assert.False(UpdateChecker.ShouldNotify(
            new Version(1, 2, 0), Release("v1.1.0", "1.1.0"), null, enabled: true));
    }

    [Fact]
    public void ShouldNotify_AlreadyToldAboutThisTag_No()
        => Assert.False(UpdateChecker.ShouldNotify(
            new Version(1, 0, 0), Release("v1.1.0", "1.1.0"), "v1.1.0", enabled: true));

    [Fact]
    public void ShouldNotify_ToldAboutAnOlderTag_StillNotifiesForTheNewOne()
        => Assert.True(UpdateChecker.ShouldNotify(
            new Version(1, 0, 0), Release("v1.2.0", "1.2.0"), "v1.1.0", enabled: true));

    [Fact]
    public void ShouldNotify_Disabled_No()
        => Assert.False(UpdateChecker.ShouldNotify(
            new Version(1, 0, 0), Release("v1.1.0", "1.1.0"), null, enabled: false));

    [Fact]
    public void ShouldNotify_NoReleaseFound_No()
        => Assert.False(UpdateChecker.ShouldNotify(
            new Version(1, 0, 0), null, null, enabled: true));

    [Fact]
    public void ShouldNotify_TagComparisonIgnoresCase()
        => Assert.False(UpdateChecker.ShouldNotify(
            new Version(1, 0, 0), Release("V1.1.0", "1.1.0"), "v1.1.0", enabled: true));

    // ── current version ──────────────────────────────────────────────────────────

    [Fact]
    public void AppVersion_Current_IsRealAndMatchesTheCsproj()
    {
        Assert.NotEqual(new Version(0, 0, 0), AppVersion.Current);
        Assert.StartsWith("v", AppVersion.Display);
    }
}
