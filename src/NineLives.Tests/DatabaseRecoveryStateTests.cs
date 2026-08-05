using System.IO;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// What a failed restore left behind, and the statements that undo it (#14).
///
/// A chain that stops part-way leaves the target in RESTORING, and in SINGLE_USER too when
/// Disconnect sessions was on, because the closing SET MULTI_USER never runs. Both block other
/// connections, at the worst possible moment.
/// </summary>
public class DatabaseRecoveryStateTests
{
    private static DatabaseRecoveryState State(string? state, string? access = "MULTI_USER")
        => new(true, state, access);

    // ── reading the state ───────────────────────────────────────────────────────

    [Fact]
    public void AnOnlineMultiUserDatabaseNeedsNothing()
    {
        Assert.False(State("ONLINE").NeedsAttention);
        Assert.Empty(State("ONLINE").SuggestedActions("MyDb"));
    }

    [Fact]
    public void AMissingDatabaseNeedsNothingAndSaysSo()
    {
        var missing = DatabaseRecoveryState.Missing;

        Assert.False(missing.NeedsAttention);
        Assert.Empty(missing.SuggestedActions("MyDb"));
        Assert.Contains("not on the server", missing.Explain("MyDb"));
    }

    [Theory]
    [InlineData("RESTORING")]
    [InlineData("restoring")]
    [InlineData("RECOVERY_PENDING")]
    public void AnInterruptedRestoreNeedsAttention(string stateDesc)
        => Assert.True(State(stateDesc).NeedsAttention);

    [Fact]
    public void SingleUserAloneNeedsAttention()
    {
        // The database came online but Disconnect sessions left it locked to one connection.
        var state = State("ONLINE", "SINGLE_USER");

        Assert.True(state.NeedsAttention);
        Assert.True(state.IsSingleUser);
    }

    // ── the statements offered ──────────────────────────────────────────────────

    [Fact]
    public void ARestoringDatabaseIsOfferedWithRecovery()
    {
        var action = Assert.Single(State("RESTORING").SuggestedActions("MyDb"));

        Assert.Equal("RESTORE DATABASE [MyDb] WITH RECOVERY", action.Sql);
        Assert.Contains("cannot be applied after this", action.Caution);
    }

    [Fact]
    public void ASingleUserDatabaseIsOfferedMultiUser()
    {
        var action = Assert.Single(State("ONLINE", "SINGLE_USER").SuggestedActions("MyDb"));

        Assert.Equal("ALTER DATABASE [MyDb] SET MULTI_USER", action.Sql);
    }

    [Fact]
    public void BothProblemsAtOnceAreOfferedInOrder()
    {
        // The usual shape of a failed restore with Disconnect sessions on. Recovery first: there
        // is no point restoring access to a database that is still mid-restore.
        var actions = State("RESTORING", "SINGLE_USER").SuggestedActions("MyDb");

        Assert.Equal(2, actions.Count);
        Assert.Contains("WITH RECOVERY", actions[0].Sql);
        Assert.Contains("SET MULTI_USER", actions[1].Sql);
    }

    [Fact]
    public void TheDatabaseNameIsQuoted()
    {
        // Free text from the target-name box, so it goes through TSql like everything else.
        var actions = State("RESTORING", "SINGLE_USER").SuggestedActions("My Db]with-bracket");

        Assert.All(actions, a => Assert.Contains("[My Db]]with-bracket]", a.Sql));
    }

    // ── the explanation ─────────────────────────────────────────────────────────

    [Fact]
    public void TheExplanationNamesTheDatabaseAndTheCause()
    {
        var text = State("RESTORING", "SINGLE_USER").Explain("MyDb");

        Assert.Contains("[MyDb] is in RESTORING state", text);
        Assert.Contains("SINGLE_USER", text);
        Assert.Contains("Disconnect sessions", text);
    }

    [Fact]
    public void AnUnrecognisedStateStillGetsDescribed()
    {
        // Better a plain readout than an empty panel if SQL Server reports something unexpected.
        var text = State("SUSPECT", "RESTRICTED_USER").Explain("MyDb");

        Assert.Contains("SUSPECT", text);
        Assert.Contains("RESTRICTED_USER", text);
    }

    // ── against a real server ───────────────────────────────────────────────────

    /// <summary>
    /// The end-to-end version: put a database into the exact state a failed chain leaves, confirm
    /// it is detected, run the suggested statement, and confirm it is fixed. A NORECOVERY restore
    /// is what a mid-chain stop actually looks like, so this reproduces the situation rather than
    /// simulating it.
    /// </summary>
    [RequiresSqlFact]
    public async Task ARealRestoringDatabaseIsDetectedAndRecovered()
    {
        var server = new ServerConnection
        {
            Name = "ninelives-test",
            ServerName = Environment.GetEnvironmentVariable("NINELIVES_TEST_SQL")!,
            AuthMode = AuthMode.WindowsAuth,
            Encrypt = EncryptMode.No,
            TrustServerCertificate = true,
            ConnectionTimeoutSeconds = 15
        };

        var service = new SqlServerService(new CredentialStore());
        var dbName = "NineLives_RecoveryTest_" + Guid.NewGuid().ToString("n")[..8];

        // The server's own data directory, not ours. SQL Server writes the backup as its service
        // account, which has no business being able to reach a user's temp folder - and on this
        // machine it cannot.
        var (dataPath, _) = await service.GetDefaultPathsAsync(server);
        var backupPath = Path.Combine(dataPath, dbName + ".bak");

        try
        {
            await Exec(service, server, $"CREATE DATABASE {TSql.QuoteName(dbName)}");
            await Exec(service, server,
                $"BACKUP DATABASE {TSql.QuoteName(dbName)} TO DISK = '{TSql.EscapeLiteral(backupPath)}'");

            // Leave it mid-restore, exactly as a chain that stopped part-way would.
            await Exec(service, server,
                $"RESTORE DATABASE {TSql.QuoteName(dbName)} FROM DISK = '{TSql.EscapeLiteral(backupPath)}' " +
                "WITH NORECOVERY, REPLACE");

            var state = await service.GetDatabaseRecoveryStateAsync(server, dbName);
            Assert.True(state.Exists);
            Assert.True(state.IsRestoring);
            Assert.True(state.NeedsAttention);

            var action = Assert.Single(state.SuggestedActions(dbName));
            await service.ExecuteRecoveryActionAsync(server, action.Sql);

            var after = await service.GetDatabaseRecoveryStateAsync(server, dbName);
            Assert.Equal("ONLINE", after.StateDescription);
            Assert.False(after.NeedsAttention);
        }
        finally
        {
            try
            {
                await Exec(service, server,
                    $"IF DB_ID('{TSql.EscapeLiteral(dbName)}') IS NOT NULL BEGIN " +
                    $"ALTER DATABASE {TSql.QuoteName(dbName)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                    $"DROP DATABASE {TSql.QuoteName(dbName)}; END");
            }
            catch { /* cleanup */ }

            // The backup lives on the server's filesystem, so remove it through the server rather
            // than assuming this process can reach the path.
            try
            {
                await Exec(service, server,
                    $"EXEC master.dbo.xp_delete_files N'{TSql.EscapeLiteral(backupPath)}'");
            }
            catch
            {
                try { File.Delete(backupPath); } catch { /* cleanup */ }
            }
        }
    }

    [RequiresSqlFact]
    public async Task ADatabaseThatDoesNotExistReportsMissing()
    {
        var server = new ServerConnection
        {
            Name = "ninelives-test",
            ServerName = Environment.GetEnvironmentVariable("NINELIVES_TEST_SQL")!,
            AuthMode = AuthMode.WindowsAuth,
            Encrypt = EncryptMode.No,
            TrustServerCertificate = true,
            ConnectionTimeoutSeconds = 15
        };

        var state = await new SqlServerService(new CredentialStore())
            .GetDatabaseRecoveryStateAsync(server, "NineLives_NoSuchDatabase_" + Guid.NewGuid().ToString("n"));

        Assert.False(state.Exists);
        Assert.False(state.NeedsAttention);
    }

    private static async Task Exec(SqlServerService service, ServerConnection server, string sql)
    {
        await using var conn = service.CreateConnection(server);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
