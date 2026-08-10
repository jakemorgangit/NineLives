using System.Text.RegularExpressions;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Parses Ola Hallengren default AG backup filenames:
/// {ClusterName}$AgName_{DatabaseName}_{BackupType}_{Year}{Month}{Day}_{Hour}{Minute}{Second}_{FileNumber}.{FileExtension}
/// e.g. mycluster01$My-AG1_MyDatabase_FULL_20260226_200032_1.bak
/// </summary>
public static class OlaAgFileNameParser
{
    // Matches: clustername$AgName_DatabaseName_FULL|DIFF|LOG[_COPY_ONLY]_YYYYMMDD_HHMMSS_N.ext
    // DatabaseName is [^_]+ so no underscores in DB name; AG name can have hyphens (e.g. My-AG1).
    //
    // The optional _COPY_ONLY group matters: without it a copy-only backup does not match at all,
    // falls through to path parsing, and in a flat container ends up with no inferred database -
    // so it silently vanishes from the model instead of being offered as a restore point.
    // Named groups, because an optional group would otherwise shift every positional index.
    private static readonly Regex AgDefaultRegex = new(
        @"^(?<cluster>.+?)\$(?<ag>.+)_(?<db>[^_]+)_(?<type>FULL|DIFF|LOG)(?<copyonly>_COPY_ONLY)?_(?<setid>\d{8}_\d{6})_(?<file>\d+)\.(?<ext>bak|trn|diff|log)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Tries to parse an Ola default AG backup filename. Returns null if the blob name does not match.
    /// </summary>
    public static OlaAgParsedName? TryParse(string blobName)
    {
        var fileName = blobName.Contains('/')
            ? blobName.Split('/').Last()
            : blobName;

        var match = AgDefaultRegex.Match(fileName);
        if (!match.Success)
            return null;

        var backupTypeStr = match.Groups["type"].Value.ToUpperInvariant();
        var backupType = backupTypeStr switch
        {
            "FULL" => BackupType.Full,
            "DIFF" => BackupType.Differential,
            "LOG" => BackupType.TransactionLog,
            _ => BackupType.Unknown
        };

        return new OlaAgParsedName
        {
            ClusterName = match.Groups["cluster"].Value,
            AgName = match.Groups["ag"].Value,
            DatabaseName = match.Groups["db"].Value,
            BackupType = backupType,
            IsCopyOnly = match.Groups["copyonly"].Success,
            SetId = match.Groups["setid"].Value, // YYYYMMDD_HHMMSS
            FileNumber = int.TryParse(match.Groups["file"].Value, out var n) ? n : 0,
            FileExtension = match.Groups["ext"].Value
        };
    }

    /// <summary>
    /// Returns true if the blob name's file segment matches the full Ola default AG naming pattern (same regex TryParse uses).
    /// </summary>
    public static bool LooksLikeAgDefault(string blobName)
    {
        var fileName = blobName.Contains('/') ? blobName.Split('/').Last() : blobName;
        return AgDefaultRegex.IsMatch(fileName);
    }
}

public class OlaAgParsedName
{
    public string ClusterName { get; set; } = string.Empty;
    public string AgName { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;
    public BackupType BackupType { get; set; }

    /// <summary>True when the filename carries Ola's COPY_ONLY marker.</summary>
    public bool IsCopyOnly { get; set; }

    public string SetId { get; set; } = string.Empty;
    public int FileNumber { get; set; }
    public string FileExtension { get; set; } = string.Empty;
    /// <summary>
    /// Server display: ClusterName$AgName (e.g. mycluster01$My-AG1).
    /// </summary>
    public string ServerDisplay => $"{ClusterName}${AgName}";
}
