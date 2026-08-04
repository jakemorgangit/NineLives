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
    // Matches: clustername$AgName_DatabaseName_FULL|DIFF|LOG_YYYYMMDD_HHMMSS_N.ext
    // DatabaseName is [^_]+ so no underscores in DB name; AG name can have hyphens (e.g. My-AG1).
    private static readonly Regex AgDefaultRegex = new(
        @"^(.+?)\$(.+)_([^_]+)_(FULL|DIFF|LOG)_(\d{8}_\d{6})_(\d+)\.(bak|trn|diff|log)$",
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

        var backupTypeStr = match.Groups[4].Value.ToUpperInvariant();
        var backupType = backupTypeStr switch
        {
            "FULL" => BackupType.Full,
            "DIFF" => BackupType.Differential,
            "LOG" => BackupType.TransactionLog,
            _ => BackupType.Unknown
        };

        return new OlaAgParsedName
        {
            ClusterName = match.Groups[1].Value,
            AgName = match.Groups[2].Value,
            DatabaseName = match.Groups[3].Value,
            BackupType = backupType,
            SetId = match.Groups[5].Value, // YYYYMMDD_HHMMSS
            FileNumber = int.TryParse(match.Groups[6].Value, out var n) ? n : 0,
            FileExtension = match.Groups[7].Value
        };
    }

    /// <summary>
    /// Returns true if the blob name looks like Ola default AG naming (contains $ and _FULL_ or _DIFF_ or _LOG_ followed by date/time pattern).
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
    public string SetId { get; set; } = string.Empty;
    public int FileNumber { get; set; }
    public string FileExtension { get; set; } = string.Empty;
    /// <summary>
    /// Server display: ClusterName$AgName (e.g. mycluster01$My-AG1).
    /// </summary>
    public string ServerDisplay => $"{ClusterName}${AgName}";
}
