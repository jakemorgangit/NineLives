using System.IO;
using Blackcat.NineLives.Cli;
using Blackcat.NineLives.Cli.Verbs;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// A scripted restore creates the credential it will authenticate with (#353).
///
/// RESTORE FROM URL needs a credential on the TARGET instance for the container's URL, and the
/// generated script deliberately never carries one - the app writes it from its credential
/// panel. Nothing in the CLI did, so the provisioning template the README leads with -
/// add-server, add-container, restore --execute on a fresh VM - died on its last line with
/// Msg 3201, after both provisioning verbs had validated green. `backup` had the step; the two
/// verbs that restore did not.
/// </summary>
public class CliRestoreCredentialTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private const string ContainerUrl = "https://acct.blob.core.windows.net/backups";

    private static (CliServices services, FakeSqlServerService sql, FakeCredentialStore store) Stage()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV02", ServerName = "SRV02" });

        var container = new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = ContainerUrl,
            PathPattern = "{BackupType}/{ServerName}/{DatabaseName}/{FileName}"
        };
        store.Config.BlobContainers.Add(container);
        store.SaveSasToken(container, "sv=2026-01-01&sig=secret");

        var blobs = new FakeBlobStorageService
        {
            Files =
            [
                new BackupFileInfo
                {
                    BlobName = "FULL/SRV01/MyDb/MyDb_FULL_20260801_220000.bak",
                    BlobUrl = $"{ContainerUrl}/FULL/SRV01/MyDb/MyDb_FULL_20260801_220000.bak",
                    Type = BackupType.Full,
                    ContainerId = "c1",
                    InferredDatabaseName = "MyDb",
                    InferredServerName = "SRV01",
                    SizeBytes = 5_000_000,
                    LastModified = new DateTimeOffset(T0, TimeSpan.Zero)
                }
            ]
        };

        var sql = new FakeSqlServerService();
        var services = new CliServices(
            store, sql, blobs, new FakeRestoreHistoryStore(), new FakeRunNotifier());

        return (services, sql, store);
    }

    private static async Task<(int exit, string errors)> Run(
        Func<CliArguments, CliServices, TextWriter, TextWriter, CancellationToken, Task<int>> verb,
        VerbSpec spec, string[] args, CliServices services)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var exit = await verb(CliArguments.Parse(args, spec), services, output, errors, default);
        return (exit, errors.ToString());
    }

    /// <summary>
    /// The one this exists for. A generate-only restore is enough to prove it: the credential
    /// belongs to the preflights, which run whether or not the restore executes.
    /// </summary>
    [Fact]
    public async Task ARestoreFromAContainerCreatesTheCredentialOnTheTarget()
    {
        var (services, sql, _) = Stage();
        sql.Credential = new BlobCredentialStatus(BlobCredentialIdentity.Missing, null);

        await Run(RestoreVerb.RunAsync, RestoreVerb.Spec,
            ["--container", "backups", "--database", "MyDb", "--target", "SRV02"], services);

        Assert.Contains(ContainerUrl, sql.CredentialWrites);
    }

    [Fact]
    public async Task ARehearsalFromAContainerCreatesItTooBeforeTheFileListIsRead()
    {
        var (services, sql, _) = Stage();
        sql.Credential = new BlobCredentialStatus(BlobCredentialIdentity.Missing, null);

        await Run(RehearseVerb.RunAsync, RehearseVerb.Spec,
            ["--container", "backups", "--database", "MyDb", "--target", "SRV02"], services);

        // Before, not after: FILELISTONLY reads the backup itself, so a rehearsal without the
        // credential fails at the read and reports NOT PROVEN - which blames the backup for
        // what is really an unconfigured host.
        Assert.Contains(ContainerUrl, sql.CredentialWrites);
    }

    // ── rehearse runs the same preflights (#359) ────────────────────────────────

    /// <summary>
    /// A rehearsal blocked by the HOST says so, rather than reporting the backup unproven.
    ///
    /// A missing TDE certificate on the rehearsal target used to surface as NOT PROVEN, exit
    /// 2, with a retained scratch copy - which reads as "this backup is bad". It is not; this
    /// target cannot restore it. At three in the morning those want opposite responses.
    /// </summary>
    [Fact]
    public async Task ARehearsalRefusedByItsTargetIsBlockedNotDisproven()
    {
        var (services, sql, _) = Stage();

        // The certificate the backup needs is not on the target.
        sql.Header = new BackupFileInfo
        {
            DatabaseName = "MyDb",
            Type = BackupType.Full,
            BackupTypeCode = 1,
            TdeThumbprint = [1, 2, 3, 4]
        };
        // CertificatesByThumbprint stays empty, so the target holds no such certificate.

        // FILELISTONLY has to answer, or the rehearsal stops before the preflights - which is
        // its own early return, not the refusal this test is about.
        sql.FileList =
        [
            new FileMoveOption { LogicalName = "MyDb", PhysicalName = @"D:\data\MyDb.mdf", Type = "D" },
            new FileMoveOption { LogicalName = "MyDb_log", PhysicalName = @"L:\logs\MyDb.ldf", Type = "L" }
        ];

        var (exit, errors) = await Run(RehearseVerb.RunAsync, RehearseVerb.Spec,
            ["--container", "backups", "--database", "MyDb", "--target", "SRV02", "--execute"],
            services);

        Assert.Equal(ExitCodes.CouldNotAnswer, exit);
        Assert.Contains("REFUSED", errors);
        Assert.Contains("neither proven nor disproven", errors);

        // And nothing ran: a blocked rehearsal is not a failed one.
        Assert.Empty(sql.ExecutedScripts);
    }

    /// <summary>
    /// A source that is an instance's own history has no container to take a credential from,
    /// and must not be refused for it - those backups are read FROM DISK.
    /// </summary>
    [Fact]
    public async Task AServerSourcedRestoreIsNotRefusedForHavingNoContainer()
    {
        var (services, sql, _) = Stage();
        services.Store.LoadConfig().Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });

        sql.BackupHistory =
        [
            new BackupHistoryEntry
            {
                DatabaseName = "MyDb",
                Type = BackupType.Full,
                StartedAt = T0,
                FinishedAt = T0.AddMinutes(5),
                CheckpointLsn = 100,
                Position = 1,
                Files = [@"X:\backups\full.bak"]
            }
        ];

        var (_, errors) = await Run(RestoreVerb.RunAsync, RestoreVerb.Spec,
            ["--server", "SRV01", "--database", "MyDb", "--target", "SRV02"], services);

        Assert.DoesNotContain("credential", errors, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(sql.CredentialWrites);
    }
}
