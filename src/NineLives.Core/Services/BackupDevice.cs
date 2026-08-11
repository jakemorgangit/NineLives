namespace Blackcat.NineLives.Services;

/// <summary>
/// Whether a backup device is a path or a URL, and the clause that names it (#375).
///
/// One rule, in one place, because there were two. The inspection statements answered it from
/// the device STRING - a UNC prefix or a drive letter is a path, everything else is a URL - and
/// the restore generator answered it from which FIELD of BackupFileInfo happened to hold the
/// device. Those agree for everything discovered by listing a container, and disagree for a
/// backup written to a bucket and later read back from the instance's own msdb: that arrives
/// with its s3:// URL in LocalPath, so IsOnDisk is true for something that was never on a disk,
/// and the generated statement said <c>DISK = N's3://...'</c>, which SQL Server cannot open.
///
/// The string is the honest thing to ask. A device is what the RESTORE will name, and where the
/// app happened to learn it says nothing about what it is.
/// </summary>
public static class BackupDevice
{
    /// <summary>
    /// True when this device is a filesystem path rather than a URL.
    ///
    /// A UNC prefix or a drive letter, and nothing else. Deliberately not a list of known URL
    /// schemes: a new provider's scheme should be treated as a URL by default, which is what
    /// happens when the question is "is it a path" rather than "is it one of the URLs I know".
    /// </summary>
    public static bool IsPath(string? device) =>
        !string.IsNullOrWhiteSpace(device)
        && (device.StartsWith(@"\\", StringComparison.Ordinal)
            || (device.Length >= 2 && device[1] == ':'));

    /// <summary>The device clause naming one backup device, escaped for a string literal.</summary>
    public static string Clause(string device) =>
        IsPath(device)
            ? $"DISK = N'{TSql.EscapeLiteral(device)}'"
            : $"URL = N'{TSql.EscapeLiteral(device)}'";
}
