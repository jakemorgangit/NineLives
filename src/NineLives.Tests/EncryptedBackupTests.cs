using System.Collections.ObjectModel;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Taking backups WITH ENCRYPTION (#222, part 3).
///
/// An encrypted backup can only be restored where its certificate exists - which is the point,
/// and also the trap. The UI states the caution; these pin that the statement is right and that
/// encryption asked-for is never quietly dropped.
/// </summary>
public class EncryptedBackupTests
{
    // ── the statement ───────────────────────────────────────────────────────────

    private static BackupOptions Options(string? certificate) => new()
    {
        DatabaseName = "MyDb",
        Medium = BackupMedium.SharedPath,
        Destinations = [@"\\nas01\backups\MyDb.bak"],
        EncryptionCertificate = certificate
    };

    [Fact]
    public void TheClauseNamesAes256AndTheCertificate()
    {
        var script = new BackupScriptGenerator().Generate(Options("BackupCert2026"));

        Assert.Contains("ENCRYPTION (ALGORITHM = AES_256, SERVER CERTIFICATE = [BackupCert2026])", script);
    }

    [Fact]
    public void NoCertificateMeansNoClause()
    {
        var script = new BackupScriptGenerator().Generate(Options(null));

        Assert.DoesNotContain("ENCRYPTION", script);
    }

    /// <summary>Quoting goes through TSql - a bracket in a certificate name cannot break out.</summary>
    [Fact]
    public void TheCertificateNameIsQuoted()
    {
        var script = new BackupScriptGenerator().Generate(Options("Odd]Name"));

        Assert.Contains("SERVER CERTIFICATE = [Odd]]Name]", script);
    }

    // ── the screen ──────────────────────────────────────────────────────────────

    private static ServerConnection Server() =>
        new() { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" };

    private static (BackupViewModel vm, FakeSqlServerService sql) New()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(Server());
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });

        var sql = new FakeSqlServerService
        {
            DatabaseList = ["MyDb"],
            BackupCertificates = ["BackupCert2026"]
        };

        var vm = new BackupViewModel(store, sql, TestLogs.Temp());
        vm.Server = vm.Servers[0];
        vm.Container = vm.Containers[0];

        return (vm, sql);
    }

    /// <summary>The certificates ride along with the database list - same instance, same moment.</summary>
    [Fact]
    public async Task CertificatesLoadWithTheDatabases()
    {
        var (vm, _) = New();

        await vm.LoadDatabasesCommand.ExecuteAsync(null);

        Assert.Equal(["BackupCert2026"], vm.EncryptionCertificates);
    }

    [Fact]
    public async Task TheOnlyCertificateIsChosenWhenEncryptionIsTicked()
    {
        var (vm, _) = New();
        await vm.LoadDatabasesCommand.ExecuteAsync(null);
        vm.SelectedDatabase = "MyDb";
        vm.GenerateCommand.Execute(null);

        vm.Encrypt = true;

        Assert.Equal("BackupCert2026", vm.SelectedEncryptionCertificate);
        Assert.Contains("ENCRYPTION (ALGORITHM = AES_256", vm.GeneratedScript);
    }

    /// <summary>
    /// Encryption asked for with no certificate chosen generates nothing - a statement without the
    /// clause is not a weaker statement, it is a different one.
    /// </summary>
    [Fact]
    public async Task EncryptionWithoutACertificateRefusesToGenerate()
    {
        var (vm, sql) = New();
        sql.BackupCertificates = [];
        await vm.LoadDatabasesCommand.ExecuteAsync(null);
        vm.SelectedDatabase = "MyDb";

        vm.Encrypt = true;

        Assert.False(vm.CanGenerate);
        Assert.True(vm.EncryptWantedButNoCertificate);
    }

    [Fact]
    public async Task UntickingEncryptionDropsTheClause()
    {
        var (vm, _) = New();
        await vm.LoadDatabasesCommand.ExecuteAsync(null);
        vm.SelectedDatabase = "MyDb";
        vm.GenerateCommand.Execute(null);
        vm.Encrypt = true;
        Assert.Contains("ENCRYPTION", vm.GeneratedScript);

        vm.Encrypt = false;

        Assert.DoesNotContain("ENCRYPTION", vm.GeneratedScript);
    }
}
