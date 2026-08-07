using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The measurement #130 asks for before anything is designed around it.
///
/// That issue proposes building chains from backup headers rather than filenames, and every part
/// of its suggested shape rests on an assumption nobody has tested: that one HEADERONLY read is
/// slow enough that reading a whole container is out of the question. It even says so - "if it
/// turns out to be ~20ms, the whole question changes shape".
///
/// Check chain already does exactly one read per chain member against real backups, so the number
/// costs nothing to collect there.
/// </summary>
public class HeaderTimingTests
{
    /// <summary>
    /// Per FILE, not per set. A striped set is several reads, and the question is what reading
    /// every file in a container would cost.
    /// </summary>
    [Fact]
    public void TheCostIsReportedPerFileBecauseThatIsWhatWouldScale()
    {
        var text = RestoreViewModel.DescribeHeaderTiming(
            setCount: 3, fileCount: 6, elapsed: TimeSpan.FromMilliseconds(1200));

        Assert.Contains("6 file(s)", text);
        Assert.Contains("3 set(s)", text);
        Assert.Contains("200 ms per file", text);
    }

    [Fact]
    public void NothingReadIsNothingToReport()
        => Assert.Equal(string.Empty,
            RestoreViewModel.DescribeHeaderTiming(0, 0, TimeSpan.FromSeconds(1)));
}
