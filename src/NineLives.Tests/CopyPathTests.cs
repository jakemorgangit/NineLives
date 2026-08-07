using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The two screens that list blobs copy the same path (#42).
///
/// They had a copy of this each, identical apart from a null check. Worth sharing rather than
/// leaving alone because these paths get pasted into tickets and scripts, and a divergence where
/// one of them started including a SAS token would be genuinely dangerous - a token in a ticket is
/// a token in whatever the ticket system indexes.
/// </summary>
public class CopyPathTests
{
    private static BlobContainerConfig Container() => new()
    {
        Id = "c1",
        Name = "backups",
        ContainerUrl = "https://acct.blob.core.windows.net/backups"
    };

    /// <summary>
    /// No SAS token, ever. The URL is built for a human to read, not for a machine to authenticate
    /// with - and the app never displays stored tokens anywhere else either.
    /// </summary>
    [Fact]
    public void TheHttpsPathCarriesNoToken()
    {
        var container = Container();
        container.CacheSasToken("sv=2026-01-01&sig=SHOULDNOTAPPEAR");

        var url = BlobStorageService.BuildBlobUrl(container, "FULL/SRV01/MyDb/full.bak");

        Assert.Equal("https://acct.blob.core.windows.net/backups/FULL/SRV01/MyDb/full.bak", url);
        Assert.DoesNotContain("sig=", url);
        Assert.DoesNotContain("?", url);
    }

    [Fact]
    public void ATrailingSlashOnTheContainerUrlDoesNotDoubleUp()
    {
        var container = Container();
        container.ContainerUrl = "https://acct.blob.core.windows.net/backups/";

        var url = BlobStorageService.BuildBlobUrl(container, "full.bak");

        Assert.DoesNotContain("backups//full.bak", url);
    }
}
