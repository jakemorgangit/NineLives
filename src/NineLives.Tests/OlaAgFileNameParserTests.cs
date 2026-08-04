using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Characterization tests for <see cref="OlaAgFileNameParser"/>, which parses
/// Ola Hallengren default AG backup filenames of the shape
/// cluster$AG_Db_FULL_yyyymmdd_hhmmss_n.ext.
/// </summary>
public class OlaAgFileNameParserTests
{
    // ---------------------------------------------------------------
    // TryParse — happy paths
    // ---------------------------------------------------------------

    [Fact]
    public void TryParse_DocumentedFullExample_ParsesAllFields()
    {
        var result = OlaAgFileNameParser.TryParse(
            "mycluster01$My-AG1_MyDatabase_FULL_20260226_200032_1.bak");

        Assert.NotNull(result);
        Assert.Equal("mycluster01", result.ClusterName);
        Assert.Equal("My-AG1", result.AgName);
        Assert.Equal("MyDatabase", result.DatabaseName);
        Assert.Equal(BackupType.Full, result.BackupType);
        Assert.Equal("20260226_200032", result.SetId);
        Assert.Equal(1, result.FileNumber);
        Assert.Equal("bak", result.FileExtension);
    }

    [Fact]
    public void TryParse_DiffBackup_ReturnsDifferentialType()
    {
        var result = OlaAgFileNameParser.TryParse(
            "cluster$AG_Db_DIFF_20260226_200032_1.diff");

        Assert.NotNull(result);
        Assert.Equal(BackupType.Differential, result.BackupType);
        Assert.Equal("diff", result.FileExtension);
    }

    [Theory]
    [InlineData("cluster$AG_Db_LOG_20260226_200032_1.trn", "trn")]
    [InlineData("cluster$AG_Db_LOG_20260226_200032_1.log", "log")]
    public void TryParse_LogBackup_ReturnsTransactionLogType(string blobName, string expectedExt)
    {
        var result = OlaAgFileNameParser.TryParse(blobName);

        Assert.NotNull(result);
        Assert.Equal(BackupType.TransactionLog, result.BackupType);
        Assert.Equal(expectedExt, result.FileExtension);
    }

    [Theory]
    [InlineData("bak")]
    [InlineData("trn")]
    [InlineData("diff")]
    [InlineData("log")]
    public void TryParse_EveryAllowedExtension_MatchesRegardlessOfBackupType(string ext)
    {
        // The regex does not tie the extension to the backup type token;
        // any of the four extensions is accepted for any type.
        var result = OlaAgFileNameParser.TryParse(
            $"cluster$AG_Db_FULL_20260226_200032_1.{ext}");

        Assert.NotNull(result);
        Assert.Equal(BackupType.Full, result.BackupType);
        Assert.Equal(ext, result.FileExtension);
    }

    [Fact]
    public void TryParse_LowercaseTypeAndUppercaseExtension_MatchesCaseInsensitively()
    {
        var result = OlaAgFileNameParser.TryParse(
            "MYCLUSTER01$my-ag1_mydb_full_20260226_200032_1.BAK");

        Assert.NotNull(result);
        Assert.Equal("MYCLUSTER01", result.ClusterName);
        Assert.Equal("my-ag1", result.AgName);
        Assert.Equal("mydb", result.DatabaseName);
        Assert.Equal(BackupType.Full, result.BackupType);
        // Captured groups preserve the original casing.
        Assert.Equal("BAK", result.FileExtension);
    }

    [Fact]
    public void TryParse_HyphenatedAgName_KeepsHyphensInAgName()
    {
        var result = OlaAgFileNameParser.TryParse(
            "prod-cluster$AG-East-1_Sales_LOG_20260101_010203_3.trn");

        Assert.NotNull(result);
        Assert.Equal("prod-cluster", result.ClusterName);
        Assert.Equal("AG-East-1", result.AgName);
        Assert.Equal("Sales", result.DatabaseName);
        Assert.Equal(3, result.FileNumber);
    }

    [Fact]
    public void TryParse_UnderscoreInDatabaseName_GreedyAgGroupSwallowsExtraSegments()
    {
        // The database group is [^_]+, so a DB name containing an underscore
        // cannot be captured whole. The greedy AG group (.+) absorbs everything
        // up to the last underscore-delimited segment before the type token:
        // AG becomes "AG1_My" and the database becomes "Database".
        var result = OlaAgFileNameParser.TryParse(
            "mycluster01$AG1_My_Database_FULL_20260226_200032_1.bak");

        Assert.NotNull(result);
        Assert.Equal("AG1_My", result.AgName);
        Assert.Equal("Database", result.DatabaseName);
    }

    [Fact]
    public void TryParse_BlobPathWithFolderPrefixes_ParsesLastSegment()
    {
        var result = OlaAgFileNameParser.TryParse(
            "myserver/MSSQLSERVER/MyDatabase/FULL/mycluster01$My-AG1_MyDatabase_FULL_20260226_200032_1.bak");

        Assert.NotNull(result);
        Assert.Equal("mycluster01", result.ClusterName);
        Assert.Equal("My-AG1", result.AgName);
        Assert.Equal("MyDatabase", result.DatabaseName);
        Assert.Equal("20260226_200032", result.SetId);
    }

    [Fact]
    public void TryParse_MultiDigitFileNumber_ParsesFileNumber()
    {
        var result = OlaAgFileNameParser.TryParse(
            "cluster$AG_Db_FULL_20260226_200032_12.bak");

        Assert.NotNull(result);
        Assert.Equal(12, result.FileNumber);
    }

    [Fact]
    public void TryParse_FileNumberTooLargeForInt_FallsBackToZero()
    {
        // (\d+) matches, but int.TryParse overflows, so FileNumber falls back to 0.
        var result = OlaAgFileNameParser.TryParse(
            "cluster$AG_Db_FULL_20260226_200032_99999999999.bak");

        Assert.NotNull(result);
        Assert.Equal(0, result.FileNumber);
    }

    // ---------------------------------------------------------------
    // TryParse — non-matching names
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("MyDatabase_FULL_20260226_200032_1.bak")]        // no $ separator
    [InlineData("cluster$AG_Db_COPY_20260226_200032_1.bak")]     // unknown type token
    [InlineData("cluster$AG_Db_FULL_20260226_200032.bak")]       // missing file number
    [InlineData("cluster$AG_Db_FULL_20260226_2000_1.bak")]       // time must be 6 digits
    [InlineData("cluster$AG_Db_FULL_20260226_200032_1.zip")]     // extension not allowed
    [InlineData("cluster$AG_Db_FULL_20260226_200032_1")]         // no extension
    [InlineData("readme.txt")]
    [InlineData("")]
    public void TryParse_NonMatchingName_ReturnsNull(string blobName)
    {
        Assert.Null(OlaAgFileNameParser.TryParse(blobName));
    }

    // ---------------------------------------------------------------
    // LooksLikeAgDefault
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("mycluster01$My-AG1_MyDatabase_FULL_20260226_200032_1.bak")]
    [InlineData("cluster$AG_Db_LOG_20260226_200032_2.trn")]
    [InlineData("folder/sub/mycluster01$My-AG1_MyDatabase_FULL_20260226_200032_1.bak")]
    public void LooksLikeAgDefault_ValidAgName_ReturnsTrue(string blobName)
    {
        Assert.True(OlaAgFileNameParser.LooksLikeAgDefault(blobName));
    }

    [Theory]
    [InlineData("MyDatabase_FULL_20260226_200032_1.bak")]        // no $ separator
    [InlineData("20260128_114441_1.bak")]                        // plain striped name
    [InlineData("cluster$AG_Db_FULL_20260226_200032_1.zip")]     // disallowed extension
    [InlineData("readme.txt")]
    [InlineData("")]
    public void LooksLikeAgDefault_NonAgName_ReturnsFalse(string blobName)
    {
        Assert.False(OlaAgFileNameParser.LooksLikeAgDefault(blobName));
    }

    // ---------------------------------------------------------------
    // OlaAgParsedName.ServerDisplay
    // ---------------------------------------------------------------

    [Fact]
    public void ServerDisplay_CombinesClusterAndAgNameWithDollar()
    {
        var result = OlaAgFileNameParser.TryParse(
            "mycluster01$My-AG1_MyDatabase_FULL_20260226_200032_1.bak");

        Assert.NotNull(result);
        Assert.Equal("mycluster01$My-AG1", result.ServerDisplay);
    }
}
