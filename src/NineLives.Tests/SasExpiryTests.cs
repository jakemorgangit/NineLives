using System.Globalization;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// SAS expiry parsing (#21), and keeping the token off the clipboard (#18).
///
/// The expiry was parsed with DateTime.TryParse and no culture, and any failure returned null -
/// which every caller reads as "not expired". So on a non-invariant locale an expired token could
/// be presented as perfectly valid, and the user found out when the restore failed.
/// </summary>
public class SasExpiryTests
{
    private static BlobContainerConfig Container() => new()
    {
        Name = "prod",
        ContainerUrl = "https://acct.blob.core.windows.net/backups"
    };

    private static string TokenExpiring(string se) => $"sv=2024-11-04&se={se}&sr=c&sp=rl&sig=abc";

    // ── culture ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("en-GB")]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]   // non-Gregorian default calendar
    [InlineData("th-TH")]   // Buddhist calendar: years land ~543 out under the ambient culture
    public void ExpiryIsReadIdenticallyInEveryCulture(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            var expiry = Container().ReadSasExpiry(TokenExpiring("2026-03-09T17:45:00Z"));

            Assert.Equal(new DateTime(2026, 3, 9, 17, 45, 0, DateTimeKind.Utc), expiry.ExpiresAt);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void AnExpiryWithNoZoneIsTreatedAsUtc()
    {
        // Azure writes UTC here. Reading it as local time would shift the answer by the machine's
        // offset, so a token could look valid for another hour on one desk and not on the next.
        var expiry = Container().ReadSasExpiry(TokenExpiring("2026-03-09T17:45:00"));

        Assert.Equal(new DateTime(2026, 3, 9, 17, 45, 0, DateTimeKind.Utc), expiry.ExpiresAt);
    }

    // ── the three states ────────────────────────────────────────────────────────

    [Fact]
    public void AReadableFutureExpiryIsNotExpired()
    {
        var se = DateTime.UtcNow.AddHours(4).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        var expiry = Container().ReadSasExpiry(TokenExpiring(se));

        Assert.False(expiry.IsExpired);
        Assert.False(expiry.CouldNotParse);
    }

    [Fact]
    public void AReadablePastExpiryIsExpired()
    {
        var se = DateTime.UtcNow.AddHours(-4).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        Assert.True(Container().ReadSasExpiry(TokenExpiring(se)).IsExpired);
    }

    /// <summary>
    /// The regression. An se= we cannot read must not be reported as a valid token - we have no
    /// basis for saying so, and the restore is the wrong place to find out.
    /// </summary>
    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2026-13-45T99:99:99Z")]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnreadableExpiryCountsAsExpired(string se)
    {
        var expiry = Container().ReadSasExpiry($"sv=2024-11-04&se={Uri.EscapeDataString(se)}&sig=abc");

        Assert.True(expiry.IsExpired);
        Assert.Null(expiry.ExpiresAt);
    }

    [Fact]
    public void NoExpiryAtAllIsUnknownRatherThanExpired()
    {
        // A SAS built on a stored access policy legitimately carries no se= of its own. Calling
        // that expired would condemn a perfectly good token.
        var expiry = Container().ReadSasExpiry("sv=2024-11-04&sr=c&si=my-policy&sig=abc");

        Assert.False(expiry.IsExpired);
        Assert.False(expiry.CouldNotParse);
        Assert.Null(expiry.ExpiresAt);
    }

    [Fact]
    public void NoTokenAtAllIsUnknown()
    {
        var expiry = Container().ReadSasExpiry("");

        Assert.False(expiry.IsExpired);
        Assert.Null(expiry.ExpiresAt);
    }

    [Fact]
    public void ALeadingQuestionMarkIsAccepted()
    {
        var expiry = Container().ReadSasExpiry("?sv=2024-11-04&se=2026-03-09T17:45:00Z&sig=abc");

        Assert.Equal(new DateTime(2026, 3, 9, 17, 45, 0, DateTimeKind.Utc), expiry.ExpiresAt);
    }

    [Fact]
    public void DisplayTextMarksAContainerWithAnUnreadableExpiryAsExpired()
    {
        var container = Container();
        container.CacheSasToken("sv=2024-11-04&se=not-a-date&sig=abc");

        Assert.True(container.IsExpired);
        Assert.Contains("[EXPIRED]", container.DisplayText);
    }

    // ── #18: the copied URL carries no credential ───────────────────────────────

    [Fact]
    public void TheCopyableBlobUrlCarriesNoSasToken()
    {
        // Copying a SAS-bearing URL puts a live credential on the Windows clipboard, where
        // clipboard history and cloud sync can keep it long after the app has closed. The
        // token-free URL is what the generated scripts use anyway - RESTORE FROM URL
        // authenticates with the server-side credential, not with anything in the URL.
        var url = BlobStorageService.BuildBlobUrl(Container(), "FULL/SRV01/MyDb/MyDb_20260305.bak");

        Assert.Equal(
            "https://acct.blob.core.windows.net/backups/FULL/SRV01/MyDb/MyDb_20260305.bak",
            url);
        Assert.DoesNotContain("?", url);
        Assert.DoesNotContain("sig=", url);
    }

    [Fact]
    public void TheCopyableBlobUrlDoesNotDoubleUpSlashes()
    {
        var container = new BlobContainerConfig
        {
            ContainerUrl = "https://acct.blob.core.windows.net/backups/"
        };

        Assert.Equal(
            "https://acct.blob.core.windows.net/backups/FULL/MyDb.bak",
            BlobStorageService.BuildBlobUrl(container, "FULL/MyDb.bak"));
    }
}
