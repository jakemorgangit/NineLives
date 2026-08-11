namespace Blackcat.NineLives.Models;

/// <summary>
/// What a BACKUP should do (#165).
///
/// The app has only ever restored backups other things took. Taking one is the other half of an
/// orchestrator, and it is the half that touches a production server - so the defaults here matter
/// more than the restore ones do. Every default below is the safe reading of a choice that has a
/// dangerous one.
/// </summary>
public class BackupOptions
{
    /// <summary>The database to back up, as it is named on the source instance.</summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>
    /// Full, differential or log (#300). COPY_ONLY applies to full and log backups;
    /// a differential has no copy-only form and the generator ignores the flag for one -
    /// a differential does not move the base anyway, which is what COPY_ONLY protects.
    /// </summary>
    public BackupType Type { get; set; } = BackupType.Full;

    /// <summary>Where it is written. The medium decides TO URL or TO DISK.</summary>
    public BackupMedium Medium { get; set; } = BackupMedium.AzureBlob;

    /// <summary>
    /// The files or blobs to write, in stripe order.
    ///
    /// More than one is a striped backup, which is faster on a large database and is also the only
    /// way past the 195 GB per-blob limit in Azure - a single-blob backup of anything larger simply
    /// fails partway, which is a bad way to find out.
    /// </summary>
    public List<string> Destinations { get; set; } = [];

    /// <summary>
    /// The server certificate to encrypt the backup with, or null for none (#222).
    ///
    /// Always AES_256 when set - offering the weaker algorithms SQL Server still accepts would
    /// be a menu of mistakes. An encrypted backup can only be restored where this certificate
    /// exists, which is the point, and also the caution the UI states: a TDE database's backups
    /// are already encrypted regardless, so this is for everything else - the plain database
    /// whose stolen blob would otherwise be restorable by whoever holds the SAS.
    /// </summary>
    public string? EncryptionCertificate { get; set; }

    /// <summary>
    /// COPY_ONLY, and on by default.
    ///
    /// A plain full backup resets the differential base on the source. Doing that to a production
    /// server that runs a differential schedule silently breaks every differential taken afterwards
    /// - they will be based on a backup that lives in this app's container and that whoever runs
    /// the restore has never heard of.
    ///
    /// The app already understands copy-only fulls (#49), so it knows what it is writing. Turning
    /// this off is a real choice with a real consequence and should look like one.
    /// </summary>
    public bool CopyOnly { get; set; } = true;

    /// <summary>
    /// COMPRESSION. On by default: it is faster on nearly everything, and on a backup that is about
    /// to cross a network or go into a container the size matters twice over.
    /// </summary>
    public bool Compression { get; set; } = true;

    /// <summary>
    /// CHECKSUM. On by default, because it is what makes a later RESTORE VERIFYONLY mean anything -
    /// verifying a backup taken without it can only confirm the file is readable, not that its
    /// contents are intact.
    /// </summary>
    public bool Checksum { get; set; } = true;

    /// <summary>
    /// FORMAT, which overwrites whatever the media set already holds.
    ///
    /// Off, and it stays off unless somebody asks for it: on a path that already holds a backup,
    /// this discards it. INIT and NOINIT below decide the ordinary append-or-overwrite question.
    /// </summary>
    public bool Format { get; set; }

    /// <summary>
    /// INIT: overwrite the backup set on this device rather than appending to it.
    ///
    /// On by default, because the app writes one backup per file with a timestamped name, so there
    /// is nothing to append to and appending to a file that unexpectedly exists produces a media
    /// set with two backups in it that nothing here would then read correctly.
    /// </summary>
    public bool Init { get; set; } = true;

    public int StatsPercent { get; set; } = 10;

    /// <summary>A description written into the backup header, for whoever finds it later.</summary>
    public string? Description { get; set; }
}
