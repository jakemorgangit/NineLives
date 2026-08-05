using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// What RESTORE VERIFYONLY said about one backup set.
/// </summary>
/// <param name="IsValid">True when SQL Server read the whole set without complaint.</param>
/// <param name="Message">
/// SQL Server's own words, either the info message it emits on success or the error it raised.
/// Kept verbatim - a DBA reading "The backup set on file 1 is valid." or "Cannot open backup
/// device" knows exactly what happened, and a paraphrase would only lose detail.
/// </param>
public sealed record VerifyOnlyResult(bool IsValid, string Message);

/// <summary>
/// One member of a restore chain paired with its verification result, for display.
/// </summary>
public sealed class ChainVerifyResult
{
    public required BackupSet Set { get; init; }
    public required VerifyOnlyResult Result { get; init; }

    public string TypeDisplay => Set.TypeDisplay;
    public string SetId => Set.SetId;
    public bool IsValid => Result.IsValid;
    public string Message => Result.Message;

    /// <summary>Short label for the row, so the grid reads without needing the message column.</summary>
    public string StatusDisplay => Result.IsValid ? "Valid" : "FAILED";

    public string Summary => $"{TypeDisplay} {SetId}: {StatusDisplay}";
}
