using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Deriving Azure prefixes from a path pattern (#28).
///
/// The failure that matters here is asymmetric. Deriving too BROAD a prefix costs a little speed;
/// deriving too NARROW a one silently returns fewer backups than exist, which on the restore
/// screen means a restore point disappearing with no error. So the rule under test throughout is:
/// when a safe prefix cannot be proven, return null and let the caller scan everything.
/// </summary>
public class BlobPrefixTests
{
    private const string Default = "{BackupType}/{ServerName}/{DatabaseName}/{FileName}";
    private static readonly string[] Types = ["FULL", "DIFF", "LOG"];

    // ── the default pattern, which is the common case ───────────────────────────

    [Fact]
    public void ServerAndDatabaseGiveOnePrefixPerBackupType()
    {
        var prefixes = BlobPrefix.Derive(Default, "SRV01", "Sales", Types);

        Assert.Equal(
            ["FULL/SRV01/Sales/", "DIFF/SRV01/Sales/", "LOG/SRV01/Sales/"],
            prefixes);
    }

    [Fact]
    public void ServerAloneStopsAtTheServerLevel()
    {
        var prefixes = BlobPrefix.Derive(Default, "SRV01", null, Types);

        Assert.Equal(["FULL/SRV01/", "DIFF/SRV01/", "LOG/SRV01/"], prefixes);
    }

    /// <summary>
    /// {ServerName} comes before {DatabaseName}, so a database without a server cannot be
    /// expressed as a prefix - the segment in between is unknown. Falling back is the only correct
    /// answer; guessing would drop every other server's copy of that database.
    /// </summary>
    [Fact]
    public void DatabaseWithoutServerCannotBeScopedPastTheBackupType()
    {
        var prefixes = BlobPrefix.Derive(Default, null, "Sales", Types);

        Assert.Equal(["FULL/", "DIFF/", "LOG/"], prefixes);
    }

    /// <summary>
    /// {BackupType} leads the default pattern, so with no folder names there is nothing to build
    /// on. This is the case the issue flagged as a blocker; one cheap hierarchy call solves it.
    /// </summary>
    [Fact]
    public void WithoutBackupTypeFoldersTheDefaultPatternCannotBeScoped()
        => Assert.Null(BlobPrefix.Derive(Default, "SRV01", "Sales", backupTypeFolders: null));

    [Fact]
    public void AnEmptyFolderListIsTreatedTheSameAsNone()
        => Assert.Null(BlobPrefix.Derive(Default, "SRV01", "Sales", []));

    // ── other layouts ───────────────────────────────────────────────────────────

    [Fact]
    public void APatternLedByServerNeedsNoBackupTypeFolders()
    {
        var prefixes = BlobPrefix.Derive(
            "{ServerName}/{DatabaseName}/{BackupType}/{FileName}", "SRV01", "Sales");

        Assert.Equal(["SRV01/Sales/"], prefixes);
    }

    [Fact]
    public void LiteralSegmentsAreAlwaysKnown()
    {
        var prefixes = BlobPrefix.Derive(
            "sqlbackups/{ServerName}/{DatabaseName}/{FileName}", "SRV01", "Sales");

        Assert.Equal(["sqlbackups/SRV01/Sales/"], prefixes);
    }

    [Fact]
    public void ALiteralPrefixIsUsableEvenWithNoFilterAtAll()
    {
        // Still narrows the listing, and costs nothing.
        var prefixes = BlobPrefix.Derive("sqlbackups/{ServerName}/{FileName}", null, null);

        Assert.Equal(["sqlbackups/"], prefixes);
    }

    [Fact]
    public void AnUnknownTokenStopsTheDerivation()
    {
        // {InstanceName} has no value to hand, so nothing past it is predictable.
        var prefixes = BlobPrefix.Derive(
            "{ServerName}/{InstanceName}/{DatabaseName}/{FileName}", "SRV01", "Sales");

        Assert.Equal(["SRV01/"], prefixes);
    }

    [Fact]
    public void ClusterAndAgTokensAreTreatedAsTheServerSegment()
    {
        var prefixes = BlobPrefix.Derive(
            "{BackupType}/{ClusterName$AgName}/{DatabaseName}/{FileName}",
            "clu01$AG1", "Sales", Types);

        Assert.Equal(
            ["FULL/clu01$AG1/Sales/", "DIFF/clu01$AG1/Sales/", "LOG/clu01$AG1/Sales/"],
            prefixes);
    }

    [Fact]
    public void NothingAfterTheFileNameSegmentIsUsed()
    {
        var prefixes = BlobPrefix.Derive("{ServerName}/{FileName}/{DatabaseName}", "SRV01", "Sales");

        Assert.Equal(["SRV01/"], prefixes);
    }

    // ── falling back ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyPatternFallsBack(string? pattern)
        => Assert.Null(BlobPrefix.Derive(pattern, "SRV01", "Sales", Types));

    [Fact]
    public void APatternThatIsNothingButUnknownTokensFallsBack()
        => Assert.Null(BlobPrefix.Derive("{FileName}", "SRV01", "Sales", Types));

    [Fact]
    public void APatternLedByAnUnknownTokenFallsBack()
        => Assert.Null(BlobPrefix.Derive("{InstanceName}/{DatabaseName}", "SRV01", "Sales", Types));

    /// <summary>
    /// Flat Ola AG naming puts every file at the container root with the structure encoded in the
    /// filename, so there is nothing to prefix on and the scan must stay full.
    /// </summary>
    [Theory]
    [InlineData(BackupSourceType.Standalone, true)]
    [InlineData(BackupSourceType.AvailabilityGroup, false)]
    [InlineData(BackupSourceType.Mixed, false)]
    public void OnlyStandaloneLayoutsSupportPrefixes(BackupSourceType type, bool expected)
        => Assert.Equal(expected, BlobPrefix.SupportsPrefixes(type));

    // ── the scope object ────────────────────────────────────────────────────────

    [Fact]
    public void AnEmptyScopeIsRecognisedAsEmpty()
    {
        Assert.False(new BlobListingScope().HasAnything);
        Assert.False(new BlobListingScope("", "  ").HasAnything);
        Assert.True(new BlobListingScope("SRV01").HasAnything);
        Assert.True(new BlobListingScope(null, "Sales").HasAnything);
    }

    // ── the prefixes match what the real container actually holds ───────────────

    [Fact]
    public void TheDerivedPrefixesMatchTheRealLayout()
    {
        // Exactly the shape measured against the live container: DIFF/, FULL/ and LOG/ at the top,
        // then server, then database. If this ever stops matching, the optimisation is silently
        // scoping to paths that do not exist and returning nothing.
        var prefixes = BlobPrefix.Derive(Default, "centaur-sql-001", "Utility", ["DIFF", "FULL", "LOG"]);

        Assert.Equal(
            [
                "DIFF/centaur-sql-001/Utility/",
                "FULL/centaur-sql-001/Utility/",
                "LOG/centaur-sql-001/Utility/"
            ],
            prefixes);
    }
}
