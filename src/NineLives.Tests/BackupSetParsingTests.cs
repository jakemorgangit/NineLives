using Blackcat.NineLives.Models;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Characterization tests for the static parsing helpers on <see cref="BackupSet"/>:
/// <see cref="BackupSet.ParseFileName"/> and <see cref="BackupSet.ParseTimestamp"/>.
/// </summary>
public class BackupSetParsingTests
{
    // ---------------------------------------------------------------
    // ParseFileName — stripe suffix extraction
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("20260128_114441_1.bak", "20260128_114441", 1)]
    [InlineData("20260128_114441_2.bak", "20260128_114441", 2)]
    [InlineData("20260128_114441_9.trn", "20260128_114441", 9)]
    [InlineData("20260128_114441_12.bak", "20260128_114441", 12)]
    public void ParseFileName_TimestampNameWithStripeSuffix_ExtractsSetIdAndStripe(
        string fileName, string expectedSetId, int expectedStripe)
    {
        var (setId, stripe) = BackupSet.ParseFileName(fileName);

        Assert.Equal(expectedSetId, setId);
        Assert.Equal(expectedStripe, stripe);
    }

    [Fact]
    public void ParseFileName_NoStripeSuffix_ReturnsBaseNameWithStripeZero()
    {
        var (setId, stripe) = BackupSet.ParseFileName("20260128_114441.bak");

        Assert.Equal("20260128_114441", setId);
        Assert.Equal(0, stripe);
    }

    [Theory]
    [InlineData("My_Backup_2.bak", "My_Backup_2")]
    [InlineData("backup_5.bak", "backup_5")]
    [InlineData("weekly_full_1.bak", "weekly_full_1")]
    public void ParseFileName_TrailingDigitButNoTimestampInCandidate_NotTreatedAsStriped(
        string fileName, string expectedSetId)
    {
        // The candidate set id must contain \d{8}_\d{4,6}; otherwise the trailing
        // _N is kept as part of the set id and stripe is 0.
        var (setId, stripe) = BackupSet.ParseFileName(fileName);

        Assert.Equal(expectedSetId, setId);
        Assert.Equal(0, stripe);
    }

    [Fact]
    public void ParseFileName_ExtensionlessStripedName_StillExtractsStripe()
    {
        var (setId, stripe) = BackupSet.ParseFileName("20260128_114441_1");

        Assert.Equal("20260128_114441", setId);
        Assert.Equal(1, stripe);
    }

    [Fact]
    public void ParseFileName_ExtensionlessNameWithoutStripe_ReturnsStripeZero()
    {
        // "20260128_114441" ends in six digits, which cannot satisfy the
        // 1-2 digit stripe suffix pattern, so it is not treated as striped.
        var (setId, stripe) = BackupSet.ParseFileName("20260128_114441");

        Assert.Equal("20260128_114441", setId);
        Assert.Equal(0, stripe);
    }

    [Fact]
    public void ParseFileName_ThreeDigitSuffix_NotTreatedAsStripe()
    {
        // Stripe suffix is limited to \d{1,2}; a three-digit tail does not match.
        var (setId, stripe) = BackupSet.ParseFileName("20260128_114441_123.bak");

        Assert.Equal("20260128_114441_123", setId);
        Assert.Equal(0, stripe);
    }

    [Fact]
    public void ParseFileName_TimestampWithFourDigitTimePortion_TreatedAsStriped()
    {
        // The guard accepts \d{8}_\d{4,6}, so an HHMM-only timestamp qualifies.
        var (setId, stripe) = BackupSet.ParseFileName("MyDb_FULL_20260128_1144_1.bak");

        Assert.Equal("MyDb_FULL_20260128_1144", setId);
        Assert.Equal(1, stripe);
    }

    [Fact]
    public void ParseFileName_TimestampEmbeddedInLongerName_ExtractsStripe()
    {
        // The timestamp guard is unanchored, so a prefixed name still counts.
        var (setId, stripe) = BackupSet.ParseFileName("MyDb_FULL_20260226_200032_2.bak");

        Assert.Equal("MyDb_FULL_20260226_200032", setId);
        Assert.Equal(2, stripe);
    }

    [Fact]
    public void ParseFileName_PathWithFolderPrefix_UsesFileNameOnly()
    {
        var (setId, stripe) = BackupSet.ParseFileName("container/20260128_114441_2.bak");

        Assert.Equal("20260128_114441", setId);
        Assert.Equal(2, stripe);
    }

    // ---------------------------------------------------------------
    // ParseTimestamp
    // ---------------------------------------------------------------

    [Fact]
    public void ParseTimestamp_FullDateAndSixDigitTime_ParsesAllComponents()
    {
        var result = BackupSet.ParseTimestamp("20260128_114441");

        Assert.Equal(new DateTime(2026, 1, 28, 11, 44, 41), result);
    }

    [Fact]
    public void ParseTimestamp_FourDigitTime_SecondsDefaultToZero()
    {
        // The seconds group is optional; HHMM-only set ids parse with :00 seconds.
        var result = BackupSet.ParseTimestamp("20260128_2200");

        Assert.Equal(new DateTime(2026, 1, 28, 22, 0, 0), result);
    }

    [Fact]
    public void ParseTimestamp_TimestampEmbeddedInLongerString_StillParses()
    {
        // The regex is unanchored, so surrounding text does not prevent a match.
        var result = BackupSet.ParseTimestamp("MyDb_FULL_20260226_200032");

        Assert.Equal(new DateTime(2026, 2, 26, 20, 0, 32), result);
    }

    [Theory]
    [InlineData("20261328_114441")] // month 13
    [InlineData("20260230_120000")] // 30 February
    [InlineData("20260128_250000")] // hour 25
    [InlineData("20260100_114441")] // day 00
    public void ParseTimestamp_MatchingPatternButInvalidDate_ReturnsNull(string setId)
    {
        Assert.Null(BackupSet.ParseTimestamp(setId));
    }

    [Theory]
    [InlineData("not-a-timestamp")]
    [InlineData("2026_0128")]      // date portion split by underscore
    [InlineData("20260128")]       // date only, no time portion
    [InlineData("20260128_1")]     // time portion too short
    [InlineData("")]
    public void ParseTimestamp_NonMatchingString_ReturnsNull(string setId)
    {
        Assert.Null(BackupSet.ParseTimestamp(setId));
    }
}
