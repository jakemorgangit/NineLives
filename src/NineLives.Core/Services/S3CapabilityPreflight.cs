using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Whether the target instance can speak S3 at all (#51, #349).
///
/// The S3 connector arrived in SQL Server 2022 and is absent from Express on every version.
/// That is a CAPABILITY rather than evidence, which is why the refusal is hard: --force exists
/// to override a judgement made on evidence, and no amount of insistence puts a connector into
/// an engine that does not have one. The restore would fail on the server, after consent, with
/// an error about the device rather than about the version.
///
/// Shared rather than written per front end. It shipped in the CLI's preflights alone, which
/// left the app - the thing most people actually restore with - promising a check in the README
/// that only the other front end performed.
///
/// Best effort by the same rule as every other preflight here: an instance that will not answer
/// produces no refusal, because refusing on a guess blocks legal restores.
/// </summary>
public static class S3CapabilityPreflight
{
    /// <summary>
    /// True when any device in the chain is an s3:// URL.
    ///
    /// The WHOLE chain, not just the full backup: containers are per-source and a chain can span
    /// several, so a full on Azure carried forward by logs in a bucket is an ordinary shape - and
    /// one the engine still has to be able to read.
    /// </summary>
    public static bool UsesS3(BackupChain? chain) =>
        chain != null && chain.AllSets.Any(set => set.Files.Any(IsS3Device));

    /// <summary>
    /// Read from <see cref="BackupFileInfo.RestoreDevice"/> rather than from BlobUrl, because
    /// that is the string the RESTORE will actually name - and it is the only one that is right
    /// in both directions. A set discovered by listing a bucket carries its s3:// URL in BlobUrl;
    /// one read back from an instance's own history carries the same URL in LocalPath, which
    /// makes IsOnDisk true for something that was never on a disk. RestoreDevice resolves to
    /// whichever is populated, so the question is asked once instead of twice.
    /// </summary>
    private static bool IsS3Device(BackupFileInfo file) =>
        file.RestoreDevice.StartsWith("s3://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Why this instance cannot restore the chain from S3, or null when it can - including when
    /// the chain does not touch S3 at all, and when the instance would not say.
    /// </summary>
    public static async Task<string?> RefusalAsync(
        ISqlServerService sql, ServerConnection target, BackupChain? chain,
        CancellationToken ct = default)
    {
        if (!UsesS3(chain)) return null;

        try
        {
            var major = await sql.GetProductMajorVersionAsync(target, ct);
            if (major is { } m && m < 16)
                return $"This chain restores from S3-compatible storage, and {target.ServerName} " +
                       $"is {VersionCompatibility.Describe(m)} - the S3 connector arrived in SQL " +
                       "Server 2022. Restore onto a 2022+ instance, or from an Azure or shared-" +
                       "path copy of the backups.";

            var edition = await sql.GetEngineEditionAsync(target, ct);
            if (edition == ExpressEngineEdition)
                return $"This chain restores from S3-compatible storage, and {target.ServerName} " +
                       "is Express edition - the S3 connector is not available in Express, on " +
                       "any version. Restore onto a Standard, Developer or Enterprise instance.";
        }
        catch
        {
            // The instance would not answer; no verdict from silence. The restore itself will
            // say so properly if it comes to that.
        }

        return null;
    }

    /// <summary>What <c>SERVERPROPERTY('EngineEdition')</c> answers for Express.</summary>
    private const int ExpressEngineEdition = 4;
}
