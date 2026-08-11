using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// A chain belongs to one database on one instance (#362).
///
/// The builder partitioned by Type alone, so a container holding the same database from two
/// servers - the everyday DR pair - produced chains that took a full from one and the logs of
/// the other. The app never saw it because its inventory screen filters to one instance first;
/// the CLI has no such filter, and a container source cannot narrow to a server at all, so
/// `9lives script --container backups --database Sales` produced exactly that mixture. Nothing
/// downstream catches it: the identity check compares database NAMES, and both are Sales.
/// </summary>
public class ChainIdentityTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private static BackupSet Set(BackupType type, string server, DateTime at, decimal? lsn = null) => new()
    {
        SetId = $"{server}_{at:yyyyMMdd_HHmmss}",
        Type = type,
        Timestamp = at,
        DatabaseName = "Sales",
        ServerName = server,
        CheckpointLsn = lsn,
        Files =
        [
            new BackupFileInfo
            {
                BlobName = $"{server}_{at:yyyyMMddHHmmss}.bak",
                BlobUrl = $"https://acct.blob.core.windows.net/backups/{server}_{at:yyyyMMddHHmmss}.bak",
                Type = type,
                SizeBytes = 1000
            }
        ]
    };

    /// <summary>Every set a restore point would actually restore from.</summary>
    private static IEnumerable<BackupSet> SetsOf(RestorePoint p) =>
        new[] { p.RequiredFullSet }.Concat(p.RequiredDiffSets).Concat(p.RequiredLogSets);

    /// <summary>
    /// The one this exists for: no restore point may mix two servers' backups.
    /// </summary>
    [Fact]
    public void NoChainPairsOneServersFullWithAnothersLog()
    {
        var sets = new List<BackupSet>
        {
            Set(BackupType.Full, "SRV01", T0),
            Set(BackupType.Full, "SRV02", T0.AddMinutes(30)),
            Set(BackupType.TransactionLog, "SRV01", T0.AddHours(1)),
            Set(BackupType.TransactionLog, "SRV02", T0.AddHours(2))
        };

        var points = new BackupChainBuilder().ComputeRestorePoints(sets);

        Assert.NotEmpty(points);
        foreach (var point in points)
        {
            var servers = SetsOf(point)
                .Select(s => s.ServerName)
                .Distinct()
                .ToList();

            Assert.Single(servers);
        }
    }

    /// <summary>
    /// Both servers are still offered - the fix separates the chains, it does not throw half
    /// the container away.
    /// </summary>
    [Fact]
    public void BothServersStillGetTheirOwnRestorePoints()
    {
        var sets = new List<BackupSet>
        {
            Set(BackupType.Full, "SRV01", T0),
            Set(BackupType.Full, "SRV02", T0.AddMinutes(30)),
            Set(BackupType.TransactionLog, "SRV01", T0.AddHours(1)),
            Set(BackupType.TransactionLog, "SRV02", T0.AddHours(2))
        };

        var points = new BackupChainBuilder().ComputeRestorePoints(sets);

        Assert.Contains(points, p => SetsOf(p).All(s => s.ServerName == "SRV01"));
        Assert.Contains(points, p => SetsOf(p).All(s => s.ServerName == "SRV02"));
    }

    /// <summary>
    /// Two databases on one server are separated by the same rule - the identity is the pair,
    /// not just the server.
    /// </summary>
    [Fact]
    public void TwoDatabasesOnOneServerDoNotShareAChain()
    {
        var sales = Set(BackupType.Full, "SRV01", T0);
        var payroll = Set(BackupType.Full, "SRV01", T0.AddMinutes(5));
        payroll.DatabaseName = "Payroll";
        var payrollLog = Set(BackupType.TransactionLog, "SRV01", T0.AddHours(1));
        payrollLog.DatabaseName = "Payroll";

        var points = new BackupChainBuilder().ComputeRestorePoints([sales, payroll, payrollLog]);

        foreach (var point in points)
            Assert.Single(SetsOf(point).Select(s => s.DatabaseName).Distinct());
    }

    /// <summary>
    /// The ordinary case - one database, one server - goes down exactly the path it always did.
    /// This is every route through the app, so it is the one that must not move.
    /// </summary>
    [Fact]
    public void OneIdentityBehavesExactlyAsBefore()
    {
        var sets = new List<BackupSet>
        {
            Set(BackupType.Full, "SRV01", T0),
            Set(BackupType.TransactionLog, "SRV01", T0.AddHours(1)),
            Set(BackupType.TransactionLog, "SRV01", T0.AddHours(2))
        };

        var points = new BackupChainBuilder().ComputeRestorePoints(sets);

        // One full point plus one per log that carries the chain forward.
        Assert.Equal(3, points.Count);
        Assert.Equal(BackupType.Full, points[0].Type);
    }
}
