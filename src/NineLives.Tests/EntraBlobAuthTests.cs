using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Entra ID authentication to Blob Storage (#29).
///
/// **Untested against a real tenant.** There is no Entra-enabled storage account to develop
/// against, so what is pinned here is the decision the app makes - which credential, and what it
/// stops storing - not that a token is accepted. The token flow belongs to Azure.Identity.
///
/// The reason it matters: many organisations now prohibit long-lived SAS tokens outright, which
/// made the tool unusable for them regardless of its merits.
/// </summary>
/// <remarks>Sets BlobStorageService.CredentialFactoryForTests, a process-wide static (#348).
/// The other two Entra classes are already here for the fixture; this one joins for the seam.</remarks>
[Collection(WpfCollection.Name)]
public class EntraBlobAuthTests
{
    private static BlobContainerConfig Container(BlobAuthMode mode) => new()
    {
        Id = BlobContainerConfig.NewId(),
        Name = "backups",
        ContainerUrl = "https://mystorageaccount.blob.core.windows.net/backups",
        AuthMode = mode
    };

    // ── no secret of ours ───────────────────────────────────────────────────────

    /// <summary>
    /// The SAS path refuses to work without a token, which is right - but that refusal must not
    /// apply to a mode that has no token by design. This is the whole feature in one assertion.
    /// </summary>
    [Theory]
    [InlineData(BlobAuthMode.EntraInteractive)]
    [InlineData(BlobAuthMode.EntraDefault)]
    public void AnEntraContainerNeedsNoStoredToken(BlobAuthMode mode)
    {
        var store = new FakeCredentialStore();
        var config = Container(mode);

        // No token stored anywhere, and no exception: the client is built from a credential.
        Assert.Null(store.GetSasToken(config));
        var ex = Record.Exception(() => new BlobStorageService(store).CreateClientForTests(config));

        Assert.Null(ex);
    }

    [Fact]
    public void ASasContainerStillSaysSoWhenItHasNoToken()
    {
        var store = new FakeCredentialStore();

        var ex = Assert.Throws<InvalidOperationException>(
            () => new BlobStorageService(store).CreateClientForTests(Container(BlobAuthMode.SasToken)));

        Assert.Contains("No SAS token", ex.Message);
    }

    /// <summary>
    /// The container URL is used as-is under Entra - no query string is appended, because there is
    /// no signature to append. A stray "?" would be sent to Azure as an empty SAS.
    /// </summary>
    [Theory]
    [InlineData(BlobAuthMode.EntraInteractive)]
    [InlineData(BlobAuthMode.EntraDefault)]
    public void TheContainerUrlIsUsedUntouched(BlobAuthMode mode)
    {
        var client = new BlobStorageService(new FakeCredentialStore())
            .CreateClientForTests(Container(mode));

        Assert.Equal(
            "https://mystorageaccount.blob.core.windows.net/backups", client.Uri.ToString());
        Assert.Empty(client.Uri.Query);
    }

    /// <summary>
    /// One credential per mode for the life of the process. A fresh one per operation means a fresh
    /// sign-in per operation - for interactive mode, a browser window every time the container is
    /// listed.
    /// </summary>
    [Fact]
    public void TheSignInIsReusedRatherThanRepeatedPerOperation()
    {
        // Static, so it is shared across service instances too - the token cache belongs to the
        // signed-in user, not to whichever service happened to ask for it first.
        Assert.Same(
            BlobStorageService.CredentialForTests(BlobAuthMode.EntraInteractive),
            BlobStorageService.CredentialForTests(BlobAuthMode.EntraInteractive));
    }

    [Fact]
    public void EachModeGetsItsOwnCredential()
    {
        Assert.NotSame(
            BlobStorageService.CredentialForTests(BlobAuthMode.EntraInteractive),
            BlobStorageService.CredentialForTests(BlobAuthMode.EntraDefault));
    }

    // ── expiry does not apply ───────────────────────────────────────────────────

    /// <summary>
    /// An Entra container has no token of ours to expire, so it must never report itself expired -
    /// otherwise the list shows a warning and a Refresh Token button for something that does not
    /// exist.
    /// </summary>
    [Theory]
    [InlineData(BlobAuthMode.EntraInteractive)]
    [InlineData(BlobAuthMode.EntraDefault)]
    public void AnEntraContainerIsNeverExpired(BlobAuthMode mode)
    {
        var config = Container(mode);

        // Even carrying a long-expired SAS from a previous life.
        config.CacheSasToken("sv=2024-01-01&se=2020-01-01T00%3A00%3A00Z&sig=x");

        Assert.False(config.IsExpired);
    }

    [Fact]
    public void ASasContainerStillReportsExpiry()
    {
        var config = Container(BlobAuthMode.SasToken);
        config.CacheSasToken("sv=2024-01-01&se=2020-01-01T00%3A00%3A00Z&sig=x");

        Assert.True(config.IsExpired);
    }

    // ── the config screen ───────────────────────────────────────────────────────

    private static BlobConfigViewModel NewVm(FakeCredentialStore store) =>
        new(store, new FakeBlobStorageService());

    [Fact]
    public void ANewEntraContainerSavesWithoutASasToken()
    {
        var store = new FakeCredentialStore();
        var vm = NewVm(store);

        vm.AddNewCommand.Execute(null);
        vm.EditName = "backups";
        vm.EditContainerUrl = "https://mystorageaccount.blob.core.windows.net/backups";
        vm.EditAuthMode = BlobAuthMode.EntraInteractive;
        vm.SaveCommand.Execute(null);

        Assert.False(vm.HasError);
        var saved = Assert.Single(store.Config.BlobContainers);
        Assert.Equal(BlobAuthMode.EntraInteractive, saved.AuthMode);
        Assert.Null(store.GetSasToken(saved));
    }

    [Fact]
    public void ANewSasContainerStillDemandsAToken()
    {
        var store = new FakeCredentialStore();
        var vm = NewVm(store);

        vm.AddNewCommand.Execute(null);
        vm.EditName = "backups";
        vm.EditContainerUrl = "https://mystorageaccount.blob.core.windows.net/backups";
        vm.SaveCommand.Execute(null);

        Assert.True(vm.HasError);
        Assert.Contains("SAS Token is required", vm.ErrorMessage);
    }

    /// <summary>
    /// Switching a container to Entra destroys its stored SAS token. An organisation that has
    /// banned long-lived SAS has banned it wherever it is sitting, including in this machine's
    /// Credential Manager - and the token is no longer used for anything.
    /// </summary>
    [Theory]
    [InlineData(BlobAuthMode.EntraInteractive)]
    [InlineData(BlobAuthMode.EntraDefault)]
    public void SwitchingToEntraDestroysTheStoredSasToken(BlobAuthMode to)
    {
        var store = new FakeCredentialStore();
        var existing = Container(BlobAuthMode.SasToken);
        store.Config.BlobContainers.Add(existing);
        store.SaveSasToken(existing, "sv=2024-01-01&sig=no-longer-needed");

        var vm = NewVm(store);
        vm.SelectedContainer = vm.Containers.Single();
        vm.EditCommand.Execute(null);

        vm.EditAuthMode = to;
        vm.SaveCommand.Execute(null);

        Assert.False(vm.HasError);
        Assert.Null(store.GetSasToken(vm.Containers.Single()));
    }

    /// <summary>
    /// And only once the mode change has reached the disk - the same ordering rule the rest of the
    /// save path follows. A refused save that had already destroyed the token would leave a SAS
    /// container in config.json with nothing behind it.
    /// </summary>
    [Fact]
    public void ARefusedSwitchToEntraKeepsTheToken()
    {
        var store = new FakeCredentialStore();
        var existing = Container(BlobAuthMode.SasToken);
        store.Config.BlobContainers.Add(existing);
        store.SaveSasToken(existing, "sv=2024-01-01&sig=still-needed");

        var vm = NewVm(store);
        vm.SelectedContainer = vm.Containers.Single();
        vm.EditCommand.Execute(null);

        store.SaveConfigThrows = new InvalidOperationException("config.json is in use by another process");

        vm.EditAuthMode = BlobAuthMode.EntraInteractive;
        vm.SaveCommand.Execute(null);

        Assert.True(vm.HasError);
        Assert.Equal(BlobAuthMode.SasToken, vm.Containers.Single().AuthMode);
        Assert.Equal("sv=2024-01-01&sig=still-needed", store.GetSasToken(vm.Containers.Single()));
    }

    [Fact]
    public void ChangingTheModeCountsAsAnUnsavedChange()
    {
        var store = new FakeCredentialStore();
        var existing = Container(BlobAuthMode.SasToken);
        store.Config.BlobContainers.Add(existing);

        var vm = NewVm(store);
        vm.SelectedContainer = vm.Containers.Single();
        vm.EditCommand.Execute(null);
        Assert.False(vm.HasUnsavedChanges);

        vm.EditAuthMode = BlobAuthMode.EntraDefault;

        Assert.True(vm.HasUnsavedChanges);
    }

    // ── Test Connection ─────────────────────────────────────────────────────────

    /// <summary>
    /// The regression. Test Connection builds its own config from the form rather than using the
    /// saved one, and it was not carrying the authentication mode - so an Entra container fell
    /// through to the SAS path and was refused with "No SAS token found. Please configure the SAS
    /// token for this container" for a container that is never going to have one.
    ///
    /// The Save path had tests. This one did not, and it is the button people press first.
    /// </summary>
    [Theory]
    [InlineData(BlobAuthMode.EntraInteractive)]
    [InlineData(BlobAuthMode.EntraDefault)]
    public async Task TestingANewEntraContainerUsesEntraRatherThanLookingForASasToken(BlobAuthMode mode)
    {
        var blob = new FakeBlobStorageService();
        var vm = new BlobConfigViewModel(new FakeCredentialStore(), blob);

        vm.AddNewCommand.Execute(null);
        vm.EditName = "backups";
        vm.EditContainerUrl = "https://mystorageaccount.blob.core.windows.net/backups";
        vm.EditAuthMode = mode;

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.False(vm.HasError, vm.ErrorMessage);
        Assert.NotNull(blob.LastConfig);
        Assert.Equal(mode, blob.LastConfig.AuthMode);
        Assert.Equal("https://mystorageaccount.blob.core.windows.net/backups", blob.LastConfig.ContainerUrl);
    }

    /// <summary>
    /// And switching an existing SAS container to Entra tests as Entra, before it is saved -
    /// otherwise Test Connection answers for the mode the container used to be in.
    /// </summary>
    [Fact]
    public async Task TestingAfterSwitchingAnExistingContainerUsesTheNewMode()
    {
        var store = new FakeCredentialStore();
        var existing = Container(BlobAuthMode.SasToken);
        store.Config.BlobContainers.Add(existing);
        store.SaveSasToken(existing, "sv=2024-01-01&sig=old");

        var blob = new FakeBlobStorageService();
        var vm = new BlobConfigViewModel(store, blob);
        vm.SelectedContainer = vm.Containers.Single();
        vm.EditCommand.Execute(null);

        vm.EditAuthMode = BlobAuthMode.EntraInteractive;
        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal(BlobAuthMode.EntraInteractive, blob.LastConfig!.AuthMode);
    }

    /// <summary>
    /// A SAS container still tests with the token being typed, unsaved - the #12 behaviour, which
    /// the Entra branch must not have disturbed.
    /// </summary>
    [Fact]
    public async Task TestingASasContainerStillUsesTheUnsavedToken()
    {
        var blob = new FakeBlobStorageService();
        var vm = new BlobConfigViewModel(new FakeCredentialStore(), blob);

        vm.AddNewCommand.Execute(null);
        vm.EditName = "backups";
        vm.EditContainerUrl = "https://mystorageaccount.blob.core.windows.net/backups";
        vm.EditSasToken = "sv=2024-01-01&sig=being-typed";

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.Equal(BlobAuthMode.SasToken, blob.LastConfig!.AuthMode);
        Assert.Equal("sv=2024-01-01&sig=being-typed", blob.LastConfig.UnsavedSasToken);
    }

    /// <summary>
    /// The stored values are what config.json holds. Reordering would silently repoint every saved
    /// container at a different authentication mode, and a container written before this existed
    /// has no value at all - which must land on SAS, the behaviour it already had.
    /// </summary>
    [Theory]
    [InlineData(BlobAuthMode.SasToken, 0)]
    [InlineData(BlobAuthMode.EntraInteractive, 1)]
    [InlineData(BlobAuthMode.EntraDefault, 2)]
    public void TheStoredValuesArePinned(BlobAuthMode mode, int expected)
    {
        Assert.Equal(expected, (int)mode);
    }

    [Fact]
    public void AContainerFromBeforeThisExistedIsASasContainer()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<BlobContainerConfig>(
            """{"Name":"backups","ContainerUrl":"https://mystorageaccount.blob.core.windows.net/backups"}""");

        Assert.NotNull(legacy);
        Assert.Equal(BlobAuthMode.SasToken, legacy.AuthMode);
    }
}
