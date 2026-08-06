using System.IO;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The server-side credential panel, on its own (#115 seam 5).
///
/// None of this had a test. Reaching it meant standing up a container, a listing and a connection
/// through RestoreViewModel, and the two things most worth pinning - the sequencing between two
/// overlapping checks, and what Execute decides before it runs - were the least reachable that way.
/// </summary>
public class ServerCredentialViewModelTests
{
    private static ServerConnection Server() => new()
    {
        Id = ServerConnection.NewId(),
        Name = "SRV01",
        ServerName = "SRV01"
    };

    private static BlobContainerConfig Container() => new()
    {
        Id = BlobContainerConfig.NewId(),
        Name = "backups",
        ContainerUrl = "https://mystorageaccount.blob.core.windows.net/backups"
    };

    /// <summary>A log in a temp directory, never the user's real one.</summary>
    private static OperationLog ThrowawayLog() => new(Path.Combine(
        Path.GetTempPath(), "ninelives-credential-tests", Guid.NewGuid().ToString("n")));

    private static (ServerCredentialViewModel vm, FakeSqlServerService sql, FakeCredentialStore store) New()
    {
        var sql = new FakeSqlServerService();
        var store = new FakeCredentialStore();
        var vm = new ServerCredentialViewModel(sql, store, ThrowawayLog(), new OperationCancellation());
        return (vm, sql, store);
    }

    // ── what the panel is pointed at ────────────────────────────────────────────

    [Fact]
    public async Task PointingAtAContainerNamesTheCredentialAfterItsUrl()
    {
        var (vm, _, _) = New();
        var container = Container();

        await vm.PointAtAsync(container);

        Assert.Equal(container.ContainerUrl, vm.Name);
        Assert.True(vm.SectionVisible);
    }

    /// <summary>
    /// Not connected yet, so there is no answer to give. Saying "not present on this server" here
    /// would be a verdict about a server nobody has asked.
    /// </summary>
    [Fact]
    public async Task WithNoServerThePanelOffersNoVerdict()
    {
        var (vm, _, _) = New();

        await vm.PointAtAsync(Container());

        Assert.Null(vm.ExistsOnServer);
        Assert.Equal(string.Empty, vm.StatusMessage);
        Assert.False(vm.IsUsable);
    }

    [Fact]
    public async Task AManagedIdentityOnTheServerReadsAsUsable()
    {
        var (vm, sql, _) = New();
        sql.Credential = new BlobCredentialStatus(BlobCredentialIdentity.ManagedIdentity, "Managed Identity");
        vm.Server = Server();

        await vm.PointAtAsync(Container());

        Assert.True(vm.IsUsable);
        Assert.True(vm.IsManagedIdentity);
        Assert.False(vm.IsSharedAccessSignature);
        Assert.Contains("Managed Identity", vm.StatusMessage);
    }

    // ── sequencing (#111) ───────────────────────────────────────────────────────

    /// <summary>
    /// The check races itself. Two overlapping checks used to leave the panel showing whichever
    /// finished LAST, which could be the container the user had already moved away from - and this
    /// panel is what somebody reads before deciding whether to overwrite a credential.
    ///
    /// So the newer check wins even when the older one finishes after it.
    /// </summary>
    [Fact]
    public async Task ASupersededCheckDoesNotGetTheLastWord()
    {
        var (vm, sql, _) = New();
        vm.Server = Server();
        vm.Container = Container();
        vm.Name = "first-container";

        var firstCheckReached = new TaskCompletionSource();
        var releaseFirstCheck = new TaskCompletionSource();

        sql.OnCredentialCheck = async (name, ct) =>
        {
            if (name == "first-container")
            {
                firstCheckReached.SetResult();
                await releaseFirstCheck.Task;

                // The real service translates a cancelled command into this, so the fake must too:
                // without it this test would pass against a viewmodel ignoring the token entirely.
                ct.ThrowIfCancellationRequested();
                return new BlobCredentialStatus(BlobCredentialIdentity.SharedAccessSignature, "SHARED ACCESS SIGNATURE");
            }

            return new BlobCredentialStatus(BlobCredentialIdentity.Other, "MYDOMAIN\\svc_sql");
        };

        var first = vm.RefreshAsync();
        await firstCheckReached.Task;

        // The user moves to another container while the first check is still in flight.
        vm.Name = "second-container";
        await vm.RefreshAsync();

        releaseFirstCheck.SetResult();
        await first;

        Assert.Equal(BlobCredentialIdentity.Other, vm.IdentityKind);
        Assert.Contains("MYDOMAIN\\svc_sql", vm.StatusMessage);
        Assert.False(vm.IsChecking);
    }

    /// <summary>A check that fails leaves no verdict behind, rather than the previous one.</summary>
    [Fact]
    public async Task AFailedCheckSaysSoInsteadOfKeepingAStaleAnswer()
    {
        var (vm, sql, _) = New();
        vm.Server = Server();
        await vm.PointAtAsync(Container());
        Assert.True(vm.IsUsable);   // the default fake answer: a SAS credential

        sql.OnCredentialCheck = (_, _) => throw new InvalidOperationException("Login failed for user.");
        await vm.RefreshAsync();

        Assert.Null(vm.ExistsOnServer);
        Assert.False(vm.IsUsable);
        Assert.Contains("Login failed for user.", vm.StatusMessage);
        Assert.False(vm.IsChecking);
    }

    // ── creating one ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreatingWithNoStoredTokenReportsItAndWritesNothing()
    {
        var (vm, sql, _) = New();
        vm.Server = Server();
        await vm.PointAtAsync(Container());

        var reported = new List<(string Message, bool IsError)>();
        vm.Reported += (m, e) => reported.Add((m, e));

        await vm.CreateOnServerCommand.ExecuteAsync(null);

        Assert.Empty(sql.CredentialWrites);
        var (message, isError) = Assert.Single(reported);
        Assert.True(isError);
        Assert.Contains("No SAS token stored", message);
    }

    /// <summary>
    /// Replacing a managed identity is allowed - it is what the button is for - but it changes how
    /// the whole instance reaches that container, so it must not be reported as a routine update
    /// (#145).
    /// </summary>
    [Fact]
    public async Task ReplacingAManagedIdentitySaysThatIsWhatHappened()
    {
        var (vm, sql, store) = New();
        sql.Credential = new BlobCredentialStatus(BlobCredentialIdentity.ManagedIdentity, "Managed Identity");
        vm.Server = Server();

        var container = Container();
        await vm.PointAtAsync(container);
        store.SaveSasToken(container, "sv=2024-01-01&sig=x");

        var reported = new List<string>();
        vm.Reported += (m, _) => reported.Add(m);

        await vm.CreateOnServerCommand.ExecuteAsync(null);

        Assert.Single(sql.CredentialWrites);
        Assert.Contains(reported, m => m.Contains("managed identity", StringComparison.OrdinalIgnoreCase));
    }

    // ── what Execute asks it ────────────────────────────────────────────────────

    /// <summary>
    /// An Entra container has no stored SAS token by design, so there is nothing this app could
    /// write. Whatever is on the server is what the restore uses - most likely the managed identity
    /// that pairs with browsing as Entra - and it is not this app's to guess at.
    /// </summary>
    [Fact]
    public async Task WithNoStoredTokenTheRestoreProceedsWithoutTouchingTheServer()
    {
        var (vm, sql, _) = New();
        vm.Server = Server();
        await vm.PointAtAsync(Container());

        var checksAfterwards = 0;
        sql.OnCredentialCheck = (_, _) =>
        {
            checksAfterwards++;
            return Task.FromResult(sql.Credential);
        };

        var preflight = await vm.PrepareForRestoreAsync(Server(), _ => { });

        Assert.True(preflight.CanProceed);
        Assert.Empty(sql.CredentialWrites);
        Assert.Equal(0, checksAfterwards);
    }

    [Fact]
    public async Task AnUnusableIdentityStopsTheRestoreAndNamesItself()
    {
        var (vm, sql, store) = New();
        sql.Credential = new BlobCredentialStatus(BlobCredentialIdentity.Other, "MYDOMAIN\\svc_sql");
        vm.Server = Server();

        var container = Container();
        await vm.PointAtAsync(container);
        store.SaveSasToken(container, "sv=2024-01-01&sig=x");

        var log = new List<string>();
        var preflight = await vm.PrepareForRestoreAsync(Server(), log.Add);

        Assert.False(preflight.CanProceed);
        Assert.Contains("MYDOMAIN\\svc_sql", preflight.Refusal);
        Assert.Contains(log, line => line.Contains("MYDOMAIN\\svc_sql", StringComparison.Ordinal));
        Assert.Empty(sql.CredentialWrites);
    }

    /// <summary>
    /// The SAS token is the one secret that crosses the wire during a restore, so an unverified
    /// certificate is worth saying at the moment it happens rather than only in settings (#17).
    /// </summary>
    [Fact]
    public async Task WritingAMissingCredentialWarnsWhenTheCertificateIsNotValidated()
    {
        var (vm, sql, store) = New();
        sql.Credential = BlobCredentialStatus.Missing;

        var server = Server();
        server.TrustServerCertificate = true;
        vm.Server = server;

        var container = Container();
        await vm.PointAtAsync(container);
        store.SaveSasToken(container, "sv=2024-01-01&sig=x");

        var log = new List<string>();
        var preflight = await vm.PrepareForRestoreAsync(server, log.Add);

        Assert.True(preflight.CanProceed);
        Assert.Single(sql.CredentialWrites);
        Assert.Contains(log, line => line.Contains("trusts the server certificate", StringComparison.Ordinal));
    }
}
