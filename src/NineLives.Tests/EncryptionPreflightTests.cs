using System.Collections.ObjectModel;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The certificate question, asked before the restore instead of answered by error 33111 (#222).
///
/// A TDE database's backups are encrypted with a key protected by a certificate in the SOURCE
/// server's master; a backup taken WITH ENCRYPTION is protected by one directly. Either way,
/// restoring on a server without that certificate fails - and without a preflight it fails
/// mid-DR, staring at a thumbprint, with the source server possibly gone.
/// </summary>
public class EncryptionPreflightTests
{
    private static readonly byte[] Thumbprint = Convert.FromHexString("AABBCCDD");
    private static readonly DateTime T0 = new(2026, 8, 7, 22, 0, 0);

    // ── the guidance ────────────────────────────────────────────────────────────

    [Fact]
    public void TheRefusalNamesTheThumbprintTheErrorAndTheRoute()
    {
        var text = EncryptionGuidance.ExplainMissingCertificate(
            isTde: true, Thumbprint, "SRV02", certificateNameOnSource: null, sourceServerName: null);

        Assert.Contains("0xAABBCCDD", text);
        Assert.Contains("33111", text);
        Assert.Contains("BACKUP CERTIFICATE", text);
        Assert.Contains("Nothing has been changed", text);
    }

    /// <summary>
    /// When the source instance could be asked, the refusal names the certificate - BACKUP
    /// CERTIFICATE takes a name, not a thumbprint, and mid-incident is the wrong time to go
    /// looking for which one.
    /// </summary>
    [Fact]
    public void KnowingTheSourceNamesTheCertificateAndBothServers()
    {
        var text = EncryptionGuidance.ExplainMissingCertificate(
            isTde: true, Thumbprint, "SRV02", "TDE_Cert_2026", "SRV01");

        Assert.Contains("[TDE_Cert_2026]", text);
        Assert.Contains("On SRV01", text);
        Assert.Contains("On SRV02, in master", text);
        // The password is whoever-runs-it's to choose, never invented here.
        Assert.DoesNotContain("PASSWORD = 'P", text);
    }

    [Fact]
    public void ProtectionIsDescribedForWhatTheHeaderActuallySaid()
    {
        Assert.Empty(EncryptionGuidance.DescribeProtection(null, null));
        Assert.Contains("TDE-encrypted database", EncryptionGuidance.DescribeProtection(Thumbprint, null));
        Assert.Contains("file is encrypted", EncryptionGuidance.DescribeProtection(null, Thumbprint));

        var both = EncryptionGuidance.DescribeProtection(Thumbprint, Thumbprint);
        Assert.Contains("TDE", both);
        Assert.Contains("encrypted", both);
    }

    /// <summary>
    /// Part 4 of #222: what protects a backup is said where the header is shown, and absence is
    /// stated too - "not encrypted" is information, not the lack of it.
    /// </summary>
    [Fact]
    public void TheMetadataInspectorNamesTheProtection()
    {
        Assert.Contains("TDE-encrypted",
            EncryptionGuidance.DescribeProtection(Thumbprint, null));
        Assert.Empty(EncryptionGuidance.DescribeProtection(null, null));
    }

    // ── the restore preflight ───────────────────────────────────────────────────

    private static ServerConnection Server(string name = "SRV02") =>
        new() { Id = ServerConnection.NewId(), Name = name, ServerName = name };

    /// <summary>A loaded ad-hoc chain whose header carries the given thumbprints.</summary>
    private static async Task<(RestoreViewModel vm, FakeSqlServerService sql, ServerConnection target)>
        LoadedAsync(byte[]? tde = null, byte[]? encryptor = null)
    {
        var target = Server();
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(target);

        var sql = new FakeSqlServerService
        {
            FileHeaders =
            {
                [@"D:\drop\MyDb.bak"] =
                [
                    new BackupHistoryEntry
                    {
                        DatabaseName = "MyDb",
                        Type = BackupType.Full,
                        StartedAt = T0,
                        FinishedAt = T0.AddMinutes(1),
                        CheckpointLsn = 100m,
                        Position = 1,
                        Files = [@"D:\drop\MyDb.bak"]
                    }
                ]
            },
            Header = new BackupFileInfo
            {
                DatabaseName = "MyDb",
                Type = BackupType.Full,
                BackupTypeCode = 1,
                SoftwareVersionMajor = 16,
                TdeThumbprint = tde,
                EncryptorThumbprint = encryptor
            },
            ProductMajorVersion = 16
        };

        var vm = new RestoreViewModel(
            new FakeBlobStorageService(), sql, new BackupChainBuilder(),
            new RestoreScriptGenerator(), store, new FakeOperationHistoryStore(),
            TestLogs.Temp(), TestAuditStores.Temp())
        {
            Mode = AppMode.Pro
        };
        vm.RefreshContainers();

        vm.SelectedMedium = BackupMedium.AdHocFile;
        vm.SourceServer = vm.SourceServers[0];
        vm.AdHocPathsText = @"D:\drop\MyDb.bak";
        await vm.Inventory.LoadAsync(vm.CurrentLocation!);
        vm.Inventory.SelectedDatabaseName = "MyDb";
        vm.Timeline.SelectedPoint = vm.Timeline.Points.Last();

        return (vm, sql, target);
    }

    /// <summary>The one this exists for: TDE backup, no certificate on the target, refused by name.</summary>
    [Fact]
    public async Task ATdeBackupWithoutTheCertificateRefusesBeforeAnythingRuns()
    {
        var (vm, _, target) = await LoadedAsync(tde: Thumbprint);

        var result = await vm.PreflightAsync(target, _ => { });

        Assert.False(result.CanProceed);
        Assert.Contains("0xAABBCCDD", result.Refusal);
        Assert.Contains("33111", result.Refusal);
    }

    [Fact]
    public async Task ATdeBackupWithTheCertificatePresentProceedsAndSaysSo()
    {
        var (vm, sql, target) = await LoadedAsync(tde: Thumbprint);
        sql.CertificatesByThumbprint["SRV02"] = new() { ["AABBCCDD"] = "TDE_Cert_2026" };

        var log = new List<string>();
        var result = await vm.PreflightAsync(target, log.Add);

        Assert.True(result.CanProceed);
        Assert.Contains(log, l => l.Contains("[TDE_Cert_2026]"));
    }

    /// <summary>An encrypted backup file follows the same rule via its own thumbprint.</summary>
    [Fact]
    public async Task AnEncryptedBackupFileIsCheckedTheSameWay()
    {
        var (vm, _, target) = await LoadedAsync(encryptor: Thumbprint);

        var result = await vm.PreflightAsync(target, _ => { });

        Assert.False(result.CanProceed);
        Assert.Contains("backup file is encrypted", result.Refusal);
    }

    [Fact]
    public async Task AnUnprotectedBackupIsNotSlowedDown()
    {
        var (vm, sql, target) = await LoadedAsync();

        var result = await vm.PreflightAsync(target, _ => { });

        Assert.True(result.CanProceed);
        // No certificate question was ever asked of the target.
        Assert.Empty(sql.CertificatesByThumbprint);
    }

    // ── the copy screen's early warning ─────────────────────────────────────────

    private static (CopyDatabaseViewModel vm, FakeSqlServerService sql) Copy()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(Server("SRV01"));
        store.Config.Servers.Add(Server("SRV02"));
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = "c1",
            Name = "backups",
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        });

        var sql = new FakeSqlServerService
        {
            DatabaseFiles =
            [
                new FileMoveOption
                {
                    LogicalName = "MyDb",
                    PhysicalName = @"D:\SQL\MyDb.mdf",
                    NewPhysicalName = @"D:\SQL\MyDb.mdf",
                    SizeBytes = 1024
                }
            ],
            VolumeFreeSpace = new Dictionary<string, long> { [@"D:\"] = long.MaxValue / 2 }
        };

        var vm = new CopyDatabaseViewModel(store, sql, TestLogs.Temp());
        vm.SourceServer = vm.Servers[0];
        vm.TargetServer = vm.Servers[1];
        vm.SourceDatabases = new ObservableCollection<string>(["MyDb"]);
        vm.SourceDatabase = "MyDb";
        vm.TargetDatabaseName = "MyDb";
        vm.Container = vm.Containers[0];

        return (vm, sql);
    }

    /// <summary>
    /// Known before the backup half runs: a backup the target can never read should not be taken
    /// on the strength of this screen.
    /// </summary>
    [Fact]
    public async Task CopyingATdeDatabaseWarnsWhenTheTargetLacksTheCertificate()
    {
        var (vm, sql) = Copy();
        sql.TdeByDatabase["MyDb"] = (true, "TDE_Cert_2026");
        sql.CertificatesByThumbprint["SRV01"] = new() { ["AABBCCDD"] = "TDE_Cert_2026" };
        // SRV02 has nothing.

        vm.GenerateCommand.Execute(null);
        await Task.Delay(50);

        Assert.True(vm.HasEncryptionWarning);
        Assert.Contains("TDE", vm.EncryptionWarning);
        Assert.Contains("[TDE_Cert_2026]", vm.EncryptionWarning);
        Assert.Contains("33111", vm.EncryptionWarning);
    }

    [Fact]
    public async Task CopyingATdeDatabaseStaysQuietWhenTheTargetHoldsTheCertificate()
    {
        var (vm, sql) = Copy();
        sql.TdeByDatabase["MyDb"] = (true, "TDE_Cert_2026");
        sql.CertificatesByThumbprint["SRV01"] = new() { ["AABBCCDD"] = "TDE_Cert_2026" };
        sql.CertificatesByThumbprint["SRV02"] = new() { ["AABBCCDD"] = "TDE_Cert_2026" };

        vm.GenerateCommand.Execute(null);
        await Task.Delay(50);

        Assert.False(vm.HasEncryptionWarning);
    }

    /// <summary>
    /// #210's law on the copy screen: the restore half runs outside the restore preflights, so
    /// without this the version refusal arrived AFTER the backup half had already run.
    /// </summary>
    [Fact]
    public async Task CopyingOntoAnOlderServerWarnsThatItCannotWork()
    {
        var (vm, sql) = Copy();
        sql.MajorVersionByServer["SRV01"] = 17;   // 2025 source
        sql.MajorVersionByServer["SRV02"] = 16;   // 2022 target

        vm.GenerateCommand.Execute(null);
        await Task.Delay(50);

        Assert.True(vm.HasVersionWarning);
        Assert.Contains("cannot work", vm.VersionWarning);
        Assert.Contains("SQL Server 2025 (17.x)", vm.VersionWarning);
        Assert.Contains("SQL Server 2022 (16.x)", vm.VersionWarning);
    }

    /// <summary>The legal direction, and silence, both stay quiet.</summary>
    [Fact]
    public async Task ALegalCopyDirectionRaisesNoVersionAlarm()
    {
        var (vm, sql) = Copy();
        sql.MajorVersionByServer["SRV01"] = 15;
        sql.MajorVersionByServer["SRV02"] = 16;

        vm.GenerateCommand.Execute(null);
        await Task.Delay(50);

        Assert.False(vm.HasVersionWarning);
    }

    [Fact]
    public async Task CopyingAPlainDatabaseAsksNoCertificateQuestions()
    {
        var (vm, _) = Copy();

        vm.GenerateCommand.Execute(null);
        await Task.Delay(50);

        Assert.False(vm.HasEncryptionWarning);
    }
}
