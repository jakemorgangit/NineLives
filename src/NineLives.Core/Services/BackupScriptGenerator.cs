using System.Text;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Writes the BACKUP statement (#165).
///
/// Deliberately the same shape as <see cref="RestoreScriptGenerator"/>, and for the same reason:
/// nothing runs that was not on screen first. A BACKUP touches a production server, so what it is
/// about to do has to be readable before it does it - including the parts somebody would not think
/// to ask about, which is why COPY_ONLY gets a comment rather than just a keyword.
///
/// The medium decides TO URL or TO DISK and nothing else. That is the same one-line difference the
/// restore path has, kept in one place here too.
/// </summary>
public class BackupScriptGenerator
{
    public string Generate(BackupOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DatabaseName) || options.Destinations.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();

        AppendHeader(sb, options);
        AppendBackup(sb, options);

        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, BackupOptions options)
    {
        sb.AppendLine("-- ============================================================");
        sb.AppendLine("-- Nine Lives - Generated Backup Script (Blackcat Data Solutions)");
        sb.AppendLine($"-- Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"-- Database:  {TSql.CommentText(options.DatabaseName)}");
        sb.AppendLine($"-- Writing:   {options.Destinations.Count} file(s)");
        sb.AppendLine("-- ============================================================");

        // Said in the script rather than only in a tooltip: the script gets copied into SSMS and
        // run by somebody who never saw the checkbox.
        if (!options.CopyOnly)
        {
            sb.AppendLine();
            sb.AppendLine("-- WARNING: this is NOT a copy-only backup, so it RESETS THE DIFFERENTIAL");
            sb.AppendLine("-- BASE on this database. Every differential taken afterwards will be based");
            sb.AppendLine("-- on this backup rather than on the last scheduled full - so restoring from");
            sb.AppendLine("-- the scheduled fulls will need this file too. If this database has a");
            sb.AppendLine("-- differential schedule, that schedule is now depending on this backup.");
        }

        sb.AppendLine();
    }

    private static void AppendBackup(StringBuilder sb, BackupOptions options)
    {
        // Exact, not unwrapped (#294): this name came from the server's own database list.
        sb.AppendLine($"BACKUP DATABASE {TSql.QuoteNameExact(options.DatabaseName)}");
        sb.AppendLine($"    TO {DeviceClause(options)}");
        sb.AppendLine($"    WITH {string.Join(", ", WithOptions(options))};");
        sb.AppendLine("GO");
    }

    /// <summary>
    /// The devices, in stripe order, one per line so a striped backup is readable.
    ///
    /// A blob URL is percent-encoded and a path is not: a UNC path is not a URI, and encoding one
    /// produces a path SQL Server cannot open.
    /// </summary>
    private static string DeviceClause(BackupOptions options)
    {
        var keyword = options.Medium == BackupMedium.AzureBlob ? "URL" : "DISK";

        var devices = options.Destinations.Select(d =>
        {
            var value = options.Medium == BackupMedium.AzureBlob ? BlobUrlEncoder.Encode(d) : d;
            return $"{keyword} = N'{TSql.EscapeLiteral(value)}'";
        });

        return string.Join($",{Environment.NewLine}       ", devices);
    }

    private static List<string> WithOptions(BackupOptions options)
    {
        var with = new List<string>();

        // First, because it is the one that changes what happens to the SOURCE.
        if (options.CopyOnly) with.Add("COPY_ONLY");

        if (options.Compression) with.Add("COMPRESSION");
        if (options.Checksum) with.Add("CHECKSUM");

        if (!string.IsNullOrWhiteSpace(options.EncryptionCertificate))
            with.Add("ENCRYPTION (ALGORITHM = AES_256, " +
                     $"SERVER CERTIFICATE = {TSql.QuoteNameExact(options.EncryptionCertificate)})");

        // FORMAT implies INIT, and naming both is noise at best. FORMAT also discards whatever the
        // media set already held, so it is never inferred - only ever asked for.
        if (options.Format) with.Add("FORMAT");
        else if (options.Init) with.Add("INIT");

        if (!string.IsNullOrWhiteSpace(options.Description))
            with.Add($"DESCRIPTION = N'{TSql.EscapeLiteral(options.Description)}'");

        with.Add($"STATS = {options.StatsPercent}");

        return with;
    }
}
