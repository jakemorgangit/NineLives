using Blackcat.NineLives.Models;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The single byte formatter (#42). The same loop had been copied into four SizeDisplay
/// properties across three files - nothing had gone wrong with it yet, but four copies is four
/// chances to drift and start disagreeing about the size of the same backup on different screens.
/// </summary>
public class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0.0 B")]
    [InlineData(512, "512.0 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1024L * 1024, "1.0 MB")]
    [InlineData(1024L * 1024 * 1024, "1.0 GB")]
    [InlineData(1024L * 1024 * 1024 * 1024, "1.0 TB")]
    public void SizesFormatInBinaryUnits(long bytes, string expected)
        => Assert.Equal(expected, ByteSize.Format(bytes));

    [Fact]
    public void VeryLargeSizesStopAtTerabytes()
    {
        // The loop is bounded by the unit list, so a petabyte-scale container reads as thousands
        // of TB rather than running off the end of the array.
        Assert.Equal("2048.0 TB", ByteSize.Format(2048L * 1024 * 1024 * 1024 * 1024));
    }

    [Fact]
    public void ANegativeSizeIsShownAsIsRatherThanLoopingOrPrintingNonsense()
    {
        // Not physically meaningful, but a subtraction bug upstream should look odd rather than
        // print "-0.0 B".
        Assert.Equal("-5 B", ByteSize.Format(-5));
    }

    [Fact]
    public void TheBoundaryBelowAUnitDoesNotRoundUpIntoIt()
    {
        Assert.Equal("1023.0 B", ByteSize.Format(1023));
    }
}
