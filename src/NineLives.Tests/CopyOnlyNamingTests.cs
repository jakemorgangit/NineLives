using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The file name claims COPY_ONLY only when the statement carries it (#441).
///
/// COPY_ONLY is the default for backups this app takes, and the marker was written into every
/// destination name regardless of type - but <c>BackupScriptGenerator</c> deliberately omits the
/// keyword on a differential, and correctly: there is no copy-only differential, because what
/// COPY_ONLY protects is the differential base and a differential does not move it.
///
/// So a name said something about the statement that the statement did not say about itself. That
/// is not merely untidy: the marker is load-bearing. The listing reads it back out of the name to
/// classify what it finds, and the receipt would say NOT copy-only beside a file called
/// <c>_COPY_ONLY_</c>.
/// </summary>
public class CopyOnlyNamingTests
{
    private static readonly DateTime T0 = new(2026, 8, 14, 15, 30, 0);

    [Fact]
    public void ADifferentialNeverCarriesTheMarker()
    {
        var name = BackupDestinationBuilder.FileName(
            "Sales", BackupType.Differential, T0, copyOnly: true);

        Assert.DoesNotContain("COPY_ONLY", name);
        Assert.Contains("_DIFF_", name);
    }

    /// <summary>
    /// The types that DO have a copy-only form still say so - this is a narrowing, not a removal,
    /// and a copy-only full that stops being recognised as one becomes a differential's base,
    /// which SQL Server rejects with 3136.
    /// </summary>
    [Theory]
    [InlineData(BackupType.Full)]
    [InlineData(BackupType.TransactionLog)]
    public void TheTypesThatHaveACopyOnlyFormStillCarryIt(BackupType type)
    {
        Assert.Contains("_COPY_ONLY",
            BackupDestinationBuilder.FileName("Sales", type, T0, copyOnly: true));
    }

    [Fact]
    public void NothingCarriesTheMarkerWhenCopyOnlyIsOff()
    {
        foreach (var type in new[] { BackupType.Full, BackupType.Differential, BackupType.TransactionLog })
            Assert.DoesNotContain("COPY_ONLY",
                BackupDestinationBuilder.FileName("Sales", type, T0, copyOnly: false));
    }

    /// <summary>
    /// The name and the statement now agree, which is the actual claim. Asked of both sides rather
    /// than of the name alone, so the two cannot drift apart again in either direction.
    /// </summary>
    [Theory]
    [InlineData(BackupType.Full)]
    [InlineData(BackupType.Differential)]
    [InlineData(BackupType.TransactionLog)]
    public void TheNameAgreesWithTheStatement(BackupType type)
    {
        var script = new BackupScriptGenerator().Generate(new BackupOptions
        {
            DatabaseName = "Sales",
            Type = type,
            CopyOnly = true,
            Destinations = ["https://acct.blob.core.windows.net/c/x.bak"]
        });

        var name = BackupDestinationBuilder.FileName("Sales", type, T0, copyOnly: true);

        Assert.Equal(script.Contains("COPY_ONLY"), name.Contains("COPY_ONLY"));
    }

    /// <summary>
    /// Backups already on disk carry the old naming, and this is a change to what gets written
    /// from now on rather than a format migration - so a differential that was written with the
    /// marker must still be read back as one, or an existing container's history changes shape
    /// under somebody who only upgraded the app.
    /// </summary>
    [Fact]
    public void ADifferentialAlreadyOnDiskWithTheOldNameIsStillReadAsCopyOnly()
    {
        Assert.True(BlobStorageService.IsCopyOnlyFileName("Sales_DIFF_COPY_ONLY_20260814_153000.bak"));
    }
}
