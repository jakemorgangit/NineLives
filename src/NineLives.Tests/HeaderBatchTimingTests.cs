using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Reading several headers over one connection, and saying honestly what it cost (#130).
///
/// Prompted by a real measurement: three HEADERONLY statements over nine striped files took 17.4
/// seconds, reported as "1,934 ms per file". Two things were wrong with that as a basis for
/// designing an audit.
///
/// The unit was wrong — a striped set is ONE statement covering several files, so dividing by files
/// understated what each read costs by the stripe count. The reads were about 5.8 seconds each.
///
/// And it was measuring something it did not mean to: every read opened its own connection, so part
/// of what looked like read cost was connect cost paid three times.
/// </summary>
public class HeaderBatchTimingTests
{
    private static readonly TimeSpan Connecting = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan Reading = TimeSpan.FromMilliseconds(17400);

    /// <summary>
    /// Per statement AND per file, because they are different numbers and the difference is the
    /// stripe count. Only one of them predicts what a bigger audit costs.
    /// </summary>
    [Fact]
    public void TheTimingGivesBothUnitsBecauseTheyDiffer()
    {
        var text = SqlServerService.DescribeBatchTiming(3, 9, Connecting, Reading);

        Assert.Contains("5,800 ms per statement", text);
        Assert.Contains("1,933 ms per file", text);
    }

    /// <summary>
    /// Connecting is reported separately, because it is paid once for the whole batch and does not
    /// scale with it - a total cannot tell the two apart, and they lead to opposite conclusions
    /// about whether an audit over a whole database is feasible.
    /// </summary>
    [Fact]
    public void ConnectingIsReportedApartFromReading()
    {
        var text = SqlServerService.DescribeBatchTiming(3, 9, Connecting, Reading);

        Assert.Contains("1,200 ms connecting", text);
        Assert.Contains("17,400 ms reading", text);
    }

    [Fact]
    public void TheCountsAreStatedSoTheNumbersCanBeCheckedAgainstThem()
    {
        var text = SqlServerService.DescribeBatchTiming(3, 9, Connecting, Reading);

        Assert.Contains("3 statement(s) over 9 file(s)", text);
    }

    [Fact]
    public void NothingIsSaidWhenNothingWasRead()
        => Assert.Equal(string.Empty, SqlServerService.DescribeBatchTiming(0, 0, Connecting, Reading));

    // ── the batch itself ────────────────────────────────────────────────────────

    /// <summary>
    /// Every file the sweep asks about goes through ONE call, so one connection covers all of them.
    /// The point of the change: a per-file connect on a read that takes seconds is most of the cost.
    /// </summary>
    [Fact]
    public async Task EveryUnplaceableFileIsReadInASingleBatch()
    {
        var sql = new FakeSqlServerService
        {
            Header = new BackupFileInfo { DatabaseName = "MyDb", Type = BackupType.Full, BackupTypeCode = 1 }
        };

        var files = Enumerable.Range(1, 5).Select(i => new BackupFileInfo
        {
            BlobName = $"mystery{i}.bak",
            BlobUrl = $"https://acct.blob.core.windows.net/backups/mystery{i}.bak",
            Type = BackupType.Unknown
        }).ToList();

        var settled = await new BackupHeaderIdentifier(sql).IdentifyAsync(Server(), files);

        Assert.Equal(5, settled);
        Assert.Single(sql.HeaderBatches);
        Assert.Equal(5, sql.HeaderBatches[0].Count);
    }

    /// <summary>
    /// A striped set is one statement covering all its files - a stripe on its own is not a
    /// readable backup, and asking about them separately would report failures that are not there.
    /// </summary>
    [Fact]
    public async Task AStripedSetIsOneStatementCoveringAllItsFiles()
    {
        var sql = new FakeSqlServerService();

        await sql.RestoreHeaderOnlyBatchAsync(Server(),
        [
            new[] { "p1.bak", "p2.bak", "p3.bak" }
        ]);

        Assert.Equal(3, Assert.Single(sql.HeaderReads).Count);
    }

    [Fact]
    public async Task NothingUnplaceableMeansNoConnectionAtAll()
    {
        var sql = new FakeSqlServerService();

        await new BackupHeaderIdentifier(sql).IdentifyAsync(Server(), []);

        Assert.Empty(sql.HeaderBatches);
    }

    private static ServerConnection Server() =>
        new() { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" };
}
