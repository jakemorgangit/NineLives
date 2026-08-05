using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

public class SqlVersionNameTests
{
    // Real @@VERSION banner from SQL Server 2022, as returned by the test instance.
    private const string Sql2022 =
        "Microsoft SQL Server 2022 (RTM) - 16.0.1000.6 (X64) \n\tOct  8 2022 05:58:25 \n\t" +
        "Copyright (C) 2022 Microsoft Corporation\n\tDeveloper Edition (64-bit) on Windows 10";

    // Real banner from the local SQL Server 2025 Express instance.
    private const string Sql2025 =
        "Microsoft SQL Server 2025 (RTM-GDR) (KB5102333) - 17.0.1125.2 (X64) \n\tJun 18 2026 14:38:44 \n\t" +
        "Copyright (C) 2025 Microsoft Corporation\n\tExpress Edition (64-bit) on Windows 11";

    [Fact]
    public void FromVersionBanner_Sql2022_IsNamed()
        => Assert.Equal("SQL Server 2022", SqlVersionName.FromVersionBanner(Sql2022));

    [Fact]
    public void FromVersionBanner_Sql2025_IsNamed()
        => Assert.Equal("SQL Server 2025", SqlVersionName.FromVersionBanner(Sql2025));

    [Theory]
    [InlineData("Microsoft SQL Server 2019 (RTM) - 15.0.2000.5 (X64)", "SQL Server 2019")]
    [InlineData("Microsoft SQL Server 2017 (RTM) - 14.0.1000.169 (X64)", "SQL Server 2017")]
    [InlineData("Microsoft SQL Server 2016 (SP3) - 13.0.6300.2 (X64)", "SQL Server 2016")]
    public void FromVersionBanner_BoxedReleases_UseThePrintedYear(string banner, string expected)
        => Assert.Equal(expected, SqlVersionName.FromVersionBanner(banner));

    [Fact]
    public void FromVersionBanner_NoPrintedYear_FallsBackToTheProductVersion()
    {
        // Some banners omit the year; the major version number is always present.
        Assert.Equal("SQL Server 2022",
            SqlVersionName.FromVersionBanner("Microsoft SQL Server (RTM) - 16.0.1000.6 (X64)"));
    }

    [Theory]
    [InlineData("Microsoft SQL Azure (RTM) - 12.0.2000.8")]
    [InlineData("Microsoft Azure SQL Edge Developer (RTM) - 15.0.2000.1552")]
    public void FromVersionBanner_Azure_IsNamedAzure(string banner)
    {
        // The year in an Azure banner is a build lineage, not a product year - reporting
        // "SQL Server 2014" for Azure SQL would be actively misleading.
        Assert.Equal("Azure SQL", SqlVersionName.FromVersionBanner(banner));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("something that is not a version banner at all")]
    public void FromVersionBanner_Unrecognised_ReturnsNull(string? banner)
    {
        // No tag is better than a wrong one on a screen used to decide what to overwrite.
        Assert.Null(SqlVersionName.FromVersionBanner(banner));
    }

    [Fact]
    public void FromVersionBanner_UnknownMajorVersion_ReturnsNull()
        => Assert.Null(SqlVersionName.FromVersionBanner("Microsoft SQL Server (RTM) - 99.0.1000.6 (X64)"));
}
