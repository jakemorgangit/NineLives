using System.Text.RegularExpressions;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Turns the sprawling <c>@@VERSION</c> banner into something short enough for a tag.
///
/// The banner looks like:
///   Microsoft SQL Server 2022 (RTM) - 16.0.1000.6 (X64) \n Jun 18 2026 ... \n Copyright ...
///
/// Two routes to a name, because neither works alone:
///   - the release year is printed for boxed versions but Azure SQL says "Azure SQL Edge" or
///     similar with no year;
///   - the major version number is always there, and maps to a year for boxed releases.
/// </summary>
public static class SqlVersionName
{
    private static readonly Regex YearRegex = new(
        @"Microsoft SQL Server\s+(?<year>\d{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ProductVersionRegex = new(
        @"-\s*(?<major>\d{1,2})\.\d+\.\d+", RegexOptions.Compiled);

    private static readonly Regex AzureRegex = new(
        @"Microsoft SQL Azure|Azure SQL", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Major version number to release year, for boxed SQL Server.</summary>
    private static readonly Dictionary<int, string> MajorToYear = new()
    {
        [17] = "2025",
        [16] = "2022",
        [15] = "2019",
        [14] = "2017",
        [13] = "2016",
        [12] = "2014",
        [11] = "2012",
        [10] = "2008",
        [9] = "2005"
    };

    /// <summary>
    /// A short label such as "SQL Server 2022", or null when the banner cannot be read - in
    /// which case no tag is better than a misleading one.
    /// </summary>
    public static string? FromVersionBanner(string? banner)
    {
        if (string.IsNullOrWhiteSpace(banner)) return null;

        if (AzureRegex.IsMatch(banner)) return "Azure SQL";

        var year = YearRegex.Match(banner);
        if (year.Success) return $"SQL Server {year.Groups["year"].Value}";

        // No year printed - fall back to the product version number, which always is.
        var product = ProductVersionRegex.Match(banner);
        if (product.Success
            && int.TryParse(product.Groups["major"].Value, out var major)
            && MajorToYear.TryGetValue(major, out var mapped))
        {
            return $"SQL Server {mapped}";
        }

        return null;
    }
}
