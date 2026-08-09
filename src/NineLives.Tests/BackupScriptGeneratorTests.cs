using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Taking a backup, to either medium (#165).
///
/// The app has only ever restored backups other things took. This is the half that touches a
/// PRODUCTION server, so the defaults matter more than the restore ones do - and most of these
/// tests are about a default rather than about syntax.
/// </summary>
public class BackupScriptGeneratorTests
{
    private static string Generate(Action<BackupOptions>? configure = null)
    {
        var options = new BackupOptions
        {
            DatabaseName = "MyDb",
            Medium = BackupMedium.SharedPath,
            Destinations = [@"\\nas01\sql\MyDb_full.bak"]
        };

        configure?.Invoke(options);
        return new BackupScriptGenerator().Generate(options);
    }

    // ── the medium ──────────────────────────────────────────────────────────────

    [Fact]
    public void ABackupToAShareIsWrittenToDisk()
    {
        var script = Generate();

        Assert.Contains(@"TO DISK = N'\\nas01\sql\MyDb_full.bak'", script);
        Assert.DoesNotContain("URL =", script);
    }

    [Fact]
    public void ABackupToBlobIsWrittenToUrl()
    {
        var script = Generate(o =>
        {
            o.Medium = BackupMedium.AzureBlob;
            o.Destinations = ["https://acct.blob.core.windows.net/backups/MyDb_full.bak"];
        });

        Assert.Contains("TO URL = N'https://acct.blob.core.windows.net/backups/MyDb_full.bak'", script);
        Assert.DoesNotContain("DISK =", script);
    }

    /// <summary>
    /// A blob URL is percent-encoded and a path is not. A UNC path is not a URI, and encoding one
    /// produces a path SQL Server cannot open.
    /// </summary>
    [Fact]
    public void APathWithSpacesIsNotUrlEncoded()
    {
        var script = Generate(o => o.Destinations = [@"\\nas01\SQL Backups\My Db\full backup.bak"]);

        Assert.Contains(@"DISK = N'\\nas01\SQL Backups\My Db\full backup.bak'", script);
        Assert.DoesNotContain("%20", script);
    }

    [Fact]
    public void ABlobUrlWithSpacesIsEncoded()
    {
        var script = Generate(o =>
        {
            o.Medium = BackupMedium.AzureBlob;
            o.Destinations = ["https://acct.blob.core.windows.net/backups/My Db.bak"];
        });

        Assert.Contains("My%20Db.bak", script);
    }

    [Fact]
    public void EveryStripeIsNamedInOrder()
    {
        var script = Generate(o => o.Destinations =
            [@"\\nas01\sql\p1.bak", @"\\nas01\sql\p2.bak", @"\\nas01\sql\p3.bak"]);

        var one = script.IndexOf("p1.bak", StringComparison.Ordinal);
        var two = script.IndexOf("p2.bak", StringComparison.Ordinal);
        var three = script.IndexOf("p3.bak", StringComparison.Ordinal);

        Assert.True(one > 0 && two > one && three > two, "stripes must be named in order");
    }

    // ── the defaults that protect the source ────────────────────────────────────

    /// <summary>
    /// The most important default in this file.
    ///
    /// A plain full backup resets the differential base on the source, so every differential the
    /// production schedule takes afterwards is based on a backup living in this app's container
    /// that whoever runs the restore has never heard of.
    /// </summary>
    [Fact]
    public void ABackupIsCopyOnlyUnlessSomebodySaysOtherwise()
    {
        Assert.Contains("COPY_ONLY", Generate());
    }

    /// <summary>
    /// And turning it off says what that costs, in the script itself - the script gets copied into
    /// SSMS and run by somebody who never saw the checkbox.
    /// </summary>
    [Fact]
    public void TurningCopyOnlyOffWarnsAboutTheDifferentialBaseInTheScript()
    {
        var script = Generate(o => o.CopyOnly = false);

        Assert.DoesNotContain("COPY_ONLY", script);
        Assert.Contains("RESETS THE DIFFERENTIAL", script);
    }

    [Fact]
    public void CompressionAndChecksumAreOnByDefault()
    {
        var script = Generate();

        Assert.Contains("COMPRESSION", script);
        Assert.Contains("CHECKSUM", script);
    }

    /// <summary>
    /// CHECKSUM is what makes a later RESTORE VERIFYONLY mean anything - against a backup taken
    /// without it, verifying can only confirm the file is readable.
    /// </summary>
    [Fact]
    public void ChecksumCanBeTurnedOff()
    {
        Assert.DoesNotContain("CHECKSUM", Generate(o => o.Checksum = false));
    }

    /// <summary>
    /// FORMAT discards whatever the media set already holds, so it is never inferred - only ever
    /// asked for.
    /// </summary>
    [Fact]
    public void FormatIsNotWrittenUnlessItWasAskedFor()
    {
        var script = Generate();

        Assert.DoesNotContain("FORMAT", script);
        Assert.Contains("INIT", script);
    }

    /// <summary>FORMAT implies INIT, so naming both is noise at best.</summary>
    [Fact]
    public void FormatReplacesInitRatherThanJoiningIt()
    {
        var script = Generate(o => o.Format = true);

        Assert.Contains("FORMAT", script);
        Assert.DoesNotContain("INIT", script);
    }

    // ── the ordinary care ───────────────────────────────────────────────────────

    [Fact]
    public void ADatabaseNameIsQuotedRatherThanConcatenated()
    {
        var script = Generate(o => o.DatabaseName = "My Db");

        Assert.Contains("BACKUP DATABASE [My Db]", script);
    }

    [Fact]
    public void ADatabaseNameWithABracketCannotBreakOutOfTheIdentifier()
    {
        var script = Generate(o => o.DatabaseName = "My]Db");

        Assert.Contains("BACKUP DATABASE [My]]Db]", script);
    }

    [Fact]
    public void AQuoteInADescriptionCannotBreakOutOfTheLiteral()
    {
        var script = Generate(o => o.Description = "Jake's refresh");

        Assert.Contains("N'Jake''s refresh'", script);
    }

    [Fact]
    public void NothingIsGeneratedWithoutADatabase()
    {
        Assert.Equal(string.Empty, Generate(o => o.DatabaseName = string.Empty));
    }

    /// <summary>
    /// A BACKUP with nowhere to write is not a partial script to be fixed up by hand - it is a
    /// statement that would fail, and offering it invites somebody to fill in a device themselves.
    /// </summary>
    [Fact]
    public void NothingIsGeneratedWithoutSomewhereToWriteTo()
    {
        Assert.Equal(string.Empty, Generate(o => o.Destinations = []));
    }

    [Fact]
    public void TheDatabaseAndFileCountAreStatedInTheHeader()
    {
        var script = Generate(o => o.Destinations = [@"\\nas01\sql\p1.bak", @"\\nas01\sql\p2.bak"]);

        Assert.Contains("Database:  MyDb", script);
        Assert.Contains("2 file(s)", script);
    }
}
