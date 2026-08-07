using System.IO;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The credential panel offering a managed identity (#147).
///
/// The gap this closes is visible rather than theoretical: on an Entra container the button failed
/// with "No SAS token stored for this container" - true, useless, and by design, because an Entra
/// container has no token to push. The SAS-free path #29 opened stopped exactly there.
/// </summary>
public class ManagedIdentityPanelTests
{
    private static ServerConnection Server() =>
        new() { Id = ServerConnection.NewId(), Name = "SRV01", ServerName = "SRV01" };

    private static BlobContainerConfig Container(BlobAuthMode mode = BlobAuthMode.SasToken) => new()
    {
        Id = BlobContainerConfig.NewId(),
        Name = "backups",
        ContainerUrl = "https://acct.blob.core.windows.net/backups",
        AuthMode = mode
    };

    private static (ServerCredentialViewModel vm, FakeSqlServerService sql, FakeCredentialStore store) New()
    {
        var store = new FakeCredentialStore();
        var sql = new FakeSqlServerService();

        var vm = new ServerCredentialViewModel(
            sql, store,
            new OperationLog(Path.Combine(Path.GetTempPath(), "ninelives-tests", Guid.NewGuid().ToString("n"))),
            new OperationCancellation());

        return (vm, sql, store);
    }

    // ── which identity gets offered ─────────────────────────────────────────────

    /// <summary>
    /// An Entra container defaults to managed identity because it is the only thing that CAN work
    /// there - there is no stored token, by design.
    /// </summary>
    [Theory]
    [InlineData(BlobAuthMode.EntraInteractive)]
    [InlineData(BlobAuthMode.EntraDefault)]
    public async Task AnEntraContainerDefaultsToManagedIdentity(BlobAuthMode mode)
    {
        var (vm, _, _) = New();
        vm.Server = Server();
        await vm.PointAtAsync(Container(mode));

        Assert.True(vm.CreatingManagedIdentity);
    }

    /// <summary>A SAS container keeps the SAS default, because that is what it has.</summary>
    [Fact]
    public async Task ASasContainerKeepsTheSasDefault()
    {
        var (vm, _, _) = New();
        vm.Server = Server();
        await vm.PointAtAsync(Container());

        Assert.False(vm.CreatingManagedIdentity);
    }

    /// <summary>
    /// And an instance that cannot use one is never defaulted to it, however the container is
    /// configured - a credential written there looks correct and fails at restore time.
    /// </summary>
    [Fact]
    public async Task AnOlderInstanceIsNeverDefaultedToManagedIdentity()
    {
        var (vm, sql, _) = New();
        sql.ManagedIdentity = new ManagedIdentitySupport(false, 15, 3);

        vm.Server = Server();
        await vm.PointAtAsync(Container(BlobAuthMode.EntraInteractive));

        Assert.False(vm.CreatingManagedIdentity);
        Assert.False(vm.ManagedIdentitySupported);
        Assert.Contains("version 15", vm.ManagedIdentityBlockedReason);
    }

    /// <summary>The button refuses rather than writing something that cannot work.</summary>
    [Fact]
    public async Task AnOlderInstanceCannotBeMadeToWriteOne()
    {
        var (vm, sql, _) = New();
        sql.ManagedIdentity = new ManagedIdentitySupport(false, 15, 3);

        vm.Server = Server();
        await vm.PointAtAsync(Container(BlobAuthMode.EntraInteractive));

        vm.IdentityToCreate = BlobCredentialIdentity.ManagedIdentity;

        Assert.False(vm.CreateOnServerCommand.CanExecute(null));
    }

    /// <summary>
    /// Not being able to ASK is not the same as the answer being no, but it has to have the same
    /// outcome: writing blind risks a credential that looks right and fails when it matters.
    /// </summary>
    [Fact]
    public async Task AnInstanceThatCannotBeAskedIsNotOfferedIt()
    {
        var (vm, sql, _) = New();
        sql.ManagedIdentityCheckThrows = new InvalidOperationException("no");

        vm.Server = Server();
        await vm.PointAtAsync(Container(BlobAuthMode.EntraInteractive));

        Assert.False(vm.ManagedIdentitySupported);
        Assert.Contains("Could not ask", vm.ManagedIdentityBlockedReason);
    }

    // ── writing it ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The gap this issue is about: on an Entra container the button used to fail outright, because
    /// it insisted on a token that container never has.
    /// </summary>
    [Fact]
    public async Task AnEntraContainerCanNowHaveItsCredentialCreated()
    {
        var (vm, sql, _) = New();
        vm.Server = Server();
        await vm.PointAtAsync(Container(BlobAuthMode.EntraInteractive));

        var reported = new List<(string Message, bool IsError)>();
        vm.Reported += (m, e) => reported.Add((m, e));

        await vm.CreateOnServerCommand.ExecuteAsync(null);

        Assert.Equal(BlobCredentialIdentity.ManagedIdentity, Assert.Single(sql.CredentialIdentitiesWritten));
        Assert.DoesNotContain(reported, r => r.IsError);
    }

    /// <summary>No token is sent, because there is none and none is wanted.</summary>
    [Fact]
    public async Task NoTokenIsSentForAManagedIdentity()
    {
        var (vm, sql, _) = New();
        vm.Server = Server();
        await vm.PointAtAsync(Container(BlobAuthMode.EntraInteractive));

        await vm.CreateOnServerCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, Assert.Single(sql.CredentialSecretsWritten));
    }

    /// <summary>
    /// What the credential alone does not buy, said when it is created rather than left to a 403 at
    /// restore time - the same trap #144 addressed for browsing.
    /// </summary>
    [Fact]
    public async Task CreatingOneSaysWhatItStillNeeds()
    {
        var (vm, sql, _) = New();
        sql.CredentialWriteResult = CredentialChange.Created;

        vm.Server = Server();
        await vm.PointAtAsync(Container(BlobAuthMode.EntraInteractive));

        var reported = new List<(string Message, bool IsError)>();
        vm.Reported += (m, e) => reported.Add((m, e));

        await vm.CreateOnServerCommand.ExecuteAsync(null);

        var (message, _) = Assert.Single(reported);
        Assert.Contains("Storage Blob Data Reader", message);
    }

    /// <summary>
    /// Converting a SAS credential to a managed identity discards the stored token on the server.
    /// That is a change to how the instance authenticates, not a routine update - #145 said the
    /// same about the opposite direction and it is no less true this way round.
    /// </summary>
    [Fact]
    public async Task ReplacingASasCredentialWithAManagedIdentityIsNamedAsAReplacement()
    {
        var (vm, sql, store) = New();
        sql.CredentialWriteResult = CredentialChange.Updated;
        sql.Credential = new BlobCredentialStatus(
            BlobCredentialIdentity.SharedAccessSignature, "SHARED ACCESS SIGNATURE");

        vm.Server = Server();
        await vm.PointAtAsync(Container(BlobAuthMode.EntraInteractive));

        var reported = new List<(string Message, bool IsError)>();
        vm.Reported += (m, e) => reported.Add((m, e));

        await vm.CreateOnServerCommand.ExecuteAsync(null);

        var (message, isError) = Assert.Single(reported);

        Assert.False(isError);
        Assert.Contains("held a SAS token", message);
        Assert.Contains("managed identity", message);
    }

    // ── the button says what the press would do ─────────────────────────────────

    /// <summary>
    /// The truth table. A label describing only what is ON the server is precisely the bug #145
    /// was - the same button read as a harmless refresh while it converted the instance's managed
    /// identity to a SAS token. Now that the identity is a choice, the reverse conversion needs the
    /// same treatment, and the render caught the button still offering to "refresh with stored SAS
    /// token" while managed identity was selected.
    /// </summary>
    [Theory]
    [InlineData(BlobCredentialIdentity.Missing, false, "Create credential on server")]
    [InlineData(BlobCredentialIdentity.Missing, true, "Create credential as the instance's managed identity")]
    [InlineData(BlobCredentialIdentity.SharedAccessSignature, false, "Refresh credential with stored SAS token")]
    [InlineData(BlobCredentialIdentity.SharedAccessSignature, true, "Replace SAS token with the instance's managed identity")]
    [InlineData(BlobCredentialIdentity.ManagedIdentity, false, "Replace Managed Identity with stored SAS token")]
    [InlineData(BlobCredentialIdentity.ManagedIdentity, true, "Refresh credential as the instance's managed identity")]
    public void TheButtonSaysWhatThePressWouldDo(
        BlobCredentialIdentity onServer, bool writingManagedIdentity, string expected)
    {
        var (vm, _, _) = New();

        vm.IdentityKind = onServer;
        vm.IdentityToCreate = writingManagedIdentity
            ? BlobCredentialIdentity.ManagedIdentity
            : BlobCredentialIdentity.SharedAccessSignature;

        Assert.Equal(expected, vm.CreateButtonText);
    }

    /// <summary>A SAS container with a token still writes a SAS credential, unchanged.</summary>
    [Fact]
    public async Task ASasContainerStillWritesASasCredential()
    {
        var (vm, sql, store) = New();
        var container = Container();
        store.SaveSasToken(container, "sv=2022&sig=abc");

        vm.Server = Server();
        await vm.PointAtAsync(container);

        await vm.CreateOnServerCommand.ExecuteAsync(null);

        Assert.Equal(BlobCredentialIdentity.SharedAccessSignature,
            Assert.Single(sql.CredentialIdentitiesWritten));
        Assert.Equal("sv=2022&sig=abc", Assert.Single(sql.CredentialSecretsWritten));
    }
}
