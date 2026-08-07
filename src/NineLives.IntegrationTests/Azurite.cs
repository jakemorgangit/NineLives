using System.IO;
using System.Net.Sockets;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Blackcat.NineLives.Models;
using Xunit;

namespace Blackcat.NineLives.IntegrationTests;

/// <summary>
/// A fact that needs the Azure Storage emulator, and skips when it is not there.
///
/// Skipping rather than failing, so somebody cloning this repo gets a green run without first
/// installing anything. CI starts Azurite explicitly, so the coverage is real where it counts - and
/// a silent skip in CI would defeat the whole point, which is why the workflow fails if the
/// emulator did not come up.
/// </summary>
public sealed class AzuriteFactAttribute : FactAttribute
{
    public AzuriteFactAttribute()
    {
        if (!Azurite.IsRunning)
            Skip = $"Azurite is not listening on {Azurite.Host}:{Azurite.BlobPort}. " +
                   "Start it with: npx --package azurite azurite-blob --skipApiVersionCheck " +
                   "--location <temp dir>";
    }
}

/// <summary>
/// The Azure Storage emulator, and the container-config plumbing to point the app at it (#37).
///
/// The gap this closes: <c>ListBackupFilesAsync</c> and the path-pattern parsing that decides what
/// each blob actually IS had no coverage against a real blob API. Everything downstream - the
/// chain, the timeline, the script - depends on that inference being right, and it has been wrong
/// in several ways this repo has since fixed (#44, #45).
///
/// The unit tests exercise the parsing over hand-built objects. What they cannot show is that the
/// enumeration hands the parser the shape it expects: a blob name with its full virtual path, a
/// size, a last-modified, an ETag. That is the join these cover.
///
/// --skipApiVersionCheck is required, not optional. The Azure SDK this app pins sends a newer
/// x-ms-version than Azurite recognises, and without the flag every single call comes back as
/// HTTP 400 InvalidHeaderValue - which reads like a bug in the app rather than a version skew in
/// the emulator. That will keep happening every time the SDK moves ahead of Azurite, so the flag
/// is part of how this is run rather than a workaround for today.
/// </summary>
public static class Azurite
{
    public const string Host = "127.0.0.1";
    public const int BlobPort = 10000;

    /// <summary>The emulator's well-known development account. Public by design; not a secret.</summary>
    public const string AccountName = "devstoreaccount1";

    public const string AccountKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    public static string ServiceUrl => $"http://{Host}:{BlobPort}/{AccountName}";

    /// <summary>
    /// Whether anything is listening, checked once per run.
    ///
    /// A socket probe rather than a blob call: a connection refused is instant, where an SDK call
    /// against a dead endpoint spends its retry policy first - which would turn a skipped suite
    /// into a slow one.
    /// </summary>
    public static bool IsRunning { get; } = Probe();

    private static bool Probe()
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(Host, BlobPort).Wait(TimeSpan.FromSeconds(2))
                   && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static StorageSharedKeyCredential Credential => new(AccountName, AccountKey);

    /// <summary>A fresh, empty container with a name nothing else in the run will use.</summary>
    public static async Task<BlobContainerClient> NewContainerAsync()
    {
        var name = $"ninelives-{Guid.NewGuid():n}";
        var client = new BlobContainerClient(new Uri($"{ServiceUrl}/{name}"), Credential);

        await client.CreateIfNotExistsAsync();
        return client;
    }

    /// <summary>
    /// Uploads a blob of a given size at a given virtual path.
    ///
    /// The content is zeros and the size is what the listing will report - these tests are about
    /// what the app infers from a blob's NAME and metadata, not about reading backups.
    /// </summary>
    public static async Task PutAsync(BlobContainerClient container, string blobName, long sizeBytes = 1024)
    {
        using var content = new MemoryStream(new byte[sizeBytes]);
        await container.GetBlobClient(blobName).UploadAsync(content, overwrite: true);
    }

    /// <summary>
    /// The app's own config, pointed at an emulator container with a real SAS.
    ///
    /// A genuine SAS rather than a shared key, because that is the path the app actually takes -
    /// CreateClient appends the token to the URL, and a test that used a different mechanism would
    /// not exercise the code being tested.
    /// </summary>
    public static BlobContainerConfig ConfigFor(
        BlobContainerClient container, string? pathPattern = null)
    {
        var sas = new BlobSasBuilder
        {
            BlobContainerName = container.Name,
            Resource = "c",
            ExpiresOn = DateTimeOffset.UtcNow.AddHours(1)
        };

        sas.SetPermissions(BlobContainerSasPermissions.Read | BlobContainerSasPermissions.List);

        var token = sas.ToSasQueryParameters(Credential).ToString();

        var config = new BlobContainerConfig
        {
            Id = BlobContainerConfig.NewId(),
            Name = container.Name,
            ContainerUrl = container.Uri.ToString(),
            PathPattern = pathPattern ?? BlobContainerConfig.DefaultPathPattern
        };

        // The unsaved token, so nothing reaches Windows Credential Manager - a test must not leave
        // anything behind in the profile of whoever ran it.
        config.UnsavedSasToken = token;

        return config;
    }
}
