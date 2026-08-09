using System.Text;

namespace Blackcat.NineLives.Services;

/// <summary>
/// The certificate question, asked before the restore instead of answered by error 33111 (#222).
///
/// Two kinds of protection put a certificate between a backup and its restore:
///
///  - TDE: the database's own encryption rides along inside every backup, protected by a
///    certificate in the SOURCE server's master. The header says so via TDEThumbprint.
///  - Encrypted backups (WITH ENCRYPTION): the backup file itself is encrypted against a server
///    certificate. The header says so via EncryptorThumbprint.
///
/// Either way the rule is the same: the TARGET must hold a certificate with that thumbprint, or
/// the restore fails - and without a preflight it fails mid-DR, staring at a thumbprint, with the
/// source server possibly gone. The thumbprint is in the header the preflight already reads
/// (#210), and the certificate question is one query against master.sys.certificates.
/// </summary>
public static class EncryptionGuidance
{
    /// <summary>0x-prefixed uppercase hex, the way SQL Server prints thumbprints in error 33111.</summary>
    public static string Hex(byte[] thumbprint) =>
        "0x" + Convert.ToHexString(thumbprint);

    /// <summary>What the header says the backup is protected by, for logs and metadata.</summary>
    public static string DescribeProtection(byte[]? tdeThumbprint, byte[]? encryptorThumbprint) =>
        (tdeThumbprint, encryptorThumbprint) switch
        {
            (null, null) => string.Empty,
            (not null, null) => $"This backup is of a TDE-encrypted database (certificate thumbprint {Hex(tdeThumbprint!)}).",
            (null, not null) => $"This backup file is encrypted (certificate thumbprint {Hex(encryptorThumbprint!)}).",
            _ => $"This backup file is encrypted (thumbprint {Hex(encryptorThumbprint!)}) and the database " +
                 $"inside it is TDE-encrypted (thumbprint {Hex(tdeThumbprint!)})."
        };

    /// <summary>
    /// The refusal, with the whole way out spelled from the source side to the target side.
    ///
    /// The certificate name is included when the source instance could be asked for it, because
    /// "back up certificate 0x9A3F..." is not something anybody can type - BACKUP CERTIFICATE
    /// takes a name, and mid-incident is the wrong time to go looking for which one.
    ///
    /// The password and the file paths stay placeholders, never invented (#205's rule): the
    /// private-key password protects the key material itself, and it belongs to whoever runs the
    /// source server.
    /// </summary>
    public static string ExplainMissingCertificate(
        bool isTde,
        byte[] thumbprint,
        string targetName,
        string? certificateNameOnSource,
        string? sourceServerName)
    {
        var sb = new StringBuilder();

        sb.Append(isTde
            ? "This backup is of a TDE-encrypted database, and its certificate is not on " +
              $"{targetName}. The restore would fail with error 33111 - Cannot find server " +
              $"certificate with thumbprint '{Hex(thumbprint)}'."
            : "This backup file is encrypted, and the certificate it is encrypted against is not " +
              $"on {targetName}. The restore would fail with error 33111 - Cannot find server " +
              $"certificate with thumbprint '{Hex(thumbprint)}'.");

        sb.Append(' ');

        if (certificateNameOnSource != null && sourceServerName != null)
        {
            sb.Append($"On {sourceServerName} that thumbprint is certificate " +
                      $"[{certificateNameOnSource}]. Export it there and create it here:\n\n" +
                      $"-- On {sourceServerName}:\n" +
                      $"BACKUP CERTIFICATE [{certificateNameOnSource}] " +
                      "TO FILE = '<path>.cer' " +
                      "WITH PRIVATE KEY (FILE = '<path>.pvk', " +
                      "ENCRYPTION BY PASSWORD = '<a password whoever runs it chooses>');\n\n" +
                      $"-- On {targetName}, in master:\n" +
                      $"CREATE CERTIFICATE [{certificateNameOnSource}] " +
                      "FROM FILE = '<path>.cer' " +
                      "WITH PRIVATE KEY (FILE = '<path>.pvk', " +
                      "DECRYPTION BY PASSWORD = '<the same password>');");
        }
        else
        {
            sb.Append("The certificate lives in the master database of the server that took the " +
                      "backup. Find it by thumbprint there - SELECT name FROM sys.certificates " +
                      $"WHERE thumbprint = {Hex(thumbprint)} - then BACKUP CERTIFICATE it to files " +
                      $"and CREATE CERTIFICATE from those files in {targetName}'s master.");
        }

        sb.Append(isTde
            ? "\n\nNothing has been changed on the server or the database. TDE certificates " +
              "protect the key that decrypts the data itself - restoring this backup anywhere " +
              "will always need that certificate first."
            : "\n\nNothing has been changed on the server or the database.");

        return sb.ToString();
    }

    /// <summary>The all-clear, said out loud because silence would look like the check never ran.</summary>
    public static string ExplainPresent(bool isTde, string certificateName, string targetName) =>
        isTde
            ? $"{targetName} holds certificate [{certificateName}] matching this backup's TDE thumbprint."
            : $"{targetName} holds certificate [{certificateName}] matching this backup's encryption thumbprint.";

    /// <summary>
    /// The copy screen's early warning: the source database is TDE and the target lacks the
    /// certificate, known BEFORE the backup half runs - a backup the target can never read should
    /// not be taken on the strength of this screen.
    /// </summary>
    public static string ExplainCopyOfTdeDatabase(
        string database, string targetName, string? certificateNameOnSource)
    {
        var cert = certificateNameOnSource == null
            ? "its TDE certificate"
            : $"its TDE certificate [{certificateNameOnSource}]";

        return $"[{database}] is TDE-encrypted, and {targetName} does not hold {cert}. The backup " +
               "half of this copy would succeed and the restore half would fail with error 33111. " +
               $"Create the certificate on {targetName} first - export it from the source's master " +
               "with BACKUP CERTIFICATE, then CREATE CERTIFICATE FROM FILE on the target.";
    }
}
