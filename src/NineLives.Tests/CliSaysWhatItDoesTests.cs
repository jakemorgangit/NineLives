using System.IO;
using System.Text.Json;
using Blackcat.NineLives.Cli;
using Blackcat.NineLives.Cli.Verbs;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// What the CLI does, and what it says it does (#370).
///
/// Three ways those had come apart: a restore always kicked every other session out of the
/// database and never mentioned it, a refusal under --json put something other than JSON on
/// stdout, and the ephemeral switch was documented as working on any verb while being matched
/// more strictly than every other option.
/// </summary>
public class CliSaysWhatItDoesTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 22, 0, 0);

    private const string ContainerUrl = "https://acct.blob.core.windows.net/backups";

    private static (CliServices services, FakeSqlServerService sql) Stage()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV02", ServerName = "SRV02" });

        var container = new BlobContainerConfig
        { Id = "c1", Name = "backups", ContainerUrl = ContainerUrl };
        store.Config.BlobContainers.Add(container);
        store.SaveSasToken(container, "sv=2026&sig=secret");

        var sql = new FakeSqlServerService
        {
            BackupHistory =
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
            ]
        };

        return (new CliServices(
            store, sql, new FakeBlobStorageService(),
            new FakeRestoreHistoryStore(), new FakeRunNotifier()), sql);
    }

    private static async Task<(int exit, string output, string errors)> Run(
        Func<CliArguments, CliServices, TextWriter, TextWriter, CancellationToken, Task<int>> verb,
        VerbSpec spec, string[] args, CliServices services)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var exit = await verb(CliArguments.Parse(args, spec), services, output, errors, default);
        return (exit, output.ToString(), errors.ToString());
    }

    // ── the sessions it disconnects ─────────────────────────────────────────────

    /// <summary>
    /// The default still disconnects - a restore cannot begin while anybody is connected - but
    /// it is now sayable, which is what the app's tick box has always been.
    /// </summary>
    [Fact]
    public async Task ARestoreDisconnectsSessionsUnlessToldOtherwise()
    {
        var (services, _) = Stage();

        var (_, withDefault, _) = await Run(RestoreVerb.RunAsync, RestoreVerb.Spec,
            ["--server", "SRV01", "--database", "MyDb", "--target", "SRV02"], services);

        Assert.Contains("SET SINGLE_USER", withDefault);

        var (services2, _) = Stage();
        var (_, kept, _) = await Run(RestoreVerb.RunAsync, RestoreVerb.Spec,
            ["--server", "SRV01", "--database", "MyDb", "--target", "SRV02", "--keep-sessions"],
            services2);

        Assert.DoesNotContain("SET SINGLE_USER", kept);
    }

    // ── a refusal under --json is still JSON ────────────────────────────────────

    /// <summary>
    /// The path a wrapper most needs to read is why something did NOT run. It used to put a
    /// page of T-SQL on stdout instead, or nothing at all.
    /// </summary>
    [Fact]
    public async Task ABackupRefusedForItsUrlLengthStillAnswersInJson()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" });

        // A container deep enough to push the destination past the engine's 259-character cap.
        var container = new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "s3://s3.eu-west-2.amazonaws.com/" + new string('b', 60) + "/" + new string('p', 60),
            PathPattern = "{BackupType}/{ServerName}/{DatabaseName}/{FileName}"
        };
        store.Config.BlobContainers.Add(container);
        store.SaveSasToken(container, "AKID:secret");

        var services = new CliServices(
            store, new FakeSqlServerService(), new FakeBlobStorageService(),
            new FakeRestoreHistoryStore(), new FakeRunNotifier());

        var (exit, output, _) = await Run(BackupVerb.RunAsync, BackupVerb.Spec,
            ["--server", "SRV01", "--container", "backups",
             "--database", new string('d', 60), "--type", "full", "--json"],
            services);

        Assert.Equal(ExitCodes.Usage, exit);

        // Parseable, and it says why - not a T-SQL script, not an empty stream.
        var parsed = JsonDocument.Parse(output).RootElement;
        Assert.Equal("Refused", parsed.GetProperty("outcome").GetString());
        Assert.True(parsed.GetProperty("refusals").GetArrayLength() > 0);
    }

    // ── the switch that spans every verb ────────────────────────────────────────

    /// <summary>
    /// Every other option is matched without regard to case; this one was not, so --EPHEMERAL
    /// came back as an unknown argument on a switch the overview says works anywhere.
    /// </summary>
    [Theory]
    [InlineData("--ephemeral")]
    [InlineData("--EPHEMERAL")]
    [InlineData("--Ephemeral")]
    public void TheEphemeralSwitchIsRecognisedWhateverItsCase(string spelling)
    {
        var recognised = typeof(Program)
            .GetMethod("IsEphemeral", System.Reflection.BindingFlags.NonPublic
                                      | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [spelling]);

        Assert.True((bool)recognised!);
    }
}
