using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The Blob Storage screen speaking S3 (#51). The scheme picks the provider everywhere else,
/// so the form reads it the same way: typing an s3:// URL swaps the SAS section for the
/// key-pair boxes with nothing to also select. What must hold: the two halves leave the form
/// only as the combined AccessKeyId:SecretKey string (shape-checked at entry, not at restore
/// time), the region rides the config, Test Connection tests what is on screen, and none of
/// the SAS-isms - expiry lines especially - leak onto a container that has no SAS.
/// </summary>
public class S3ConfigScreenTests
{
    private readonly FakeCredentialStore _store = new();
    private readonly FakeBlobStorageService _blobs = new();

    private BlobConfigViewModel NewViewModel() => new(_store, _blobs);

    private BlobContainerConfig SavedS3Container(string pair = "AKIDSTORED:stored-secret")
    {
        var container = new BlobContainerConfig
        {
            Id = BlobContainerConfig.NewId(),
            Name = "bucket",
            ContainerUrl = "s3://s3.eu-west-2.amazonaws.com/backups",
            S3Region = "eu-west-2"
        };
        _store.Config.BlobContainers.Add(container);
        _store.SaveSasToken(container, pair);
        return container;
    }

    // ── the provider choice and the scheme agree ────────────────────────────────

    [Fact]
    public void TypingAnS3UrlSelectsTheProviderAndSnapsTheAuthMode()
    {
        var vm = NewViewModel();
        vm.AddNewCommand.Execute(null);
        vm.EditAuthMode = BlobAuthMode.EntraInteractive;

        vm.EditContainerUrl = "s3://storage.example.com/backups";

        Assert.True(vm.IsS3Url);
        Assert.True(vm.EditIsS3);
        // Entra cannot reach a bucket - the mode snaps back rather than riding along silently.
        Assert.Equal(BlobAuthMode.SasToken, vm.EditAuthMode);
    }

    /// <summary>
    /// The reason the choice exists at all: on an empty Add Container form the scheme has not
    /// been typed yet, so without an explicit selection every section defaulted to its Azure
    /// shape and nothing said a bucket was even possible.
    /// </summary>
    [Fact]
    public void ChoosingS3ShowsTheKeyPairFormBeforeAnyUrlExists()
    {
        var vm = NewViewModel();
        vm.AddNewCommand.Execute(null);
        Assert.False(vm.EditIsS3);

        vm.EditProvider = StorageProviderChoice.S3;

        Assert.Equal(BlobAuthMode.SasToken, vm.EditAuthMode);
        // Typing the s3 URL afterwards leaves the choice standing.
        vm.EditContainerUrl = "s3://storage.example.com/backups";
        Assert.True(vm.EditIsS3);
    }

    [Fact]
    public void AnAzureUrlSnapsTheChoiceBack()
    {
        var vm = NewViewModel();
        vm.AddNewCommand.Execute(null);
        vm.EditProvider = StorageProviderChoice.S3;

        // The URL is what gets saved, so a typed scheme outranks the earlier selection.
        vm.EditContainerUrl = "https://acct.blob.core.windows.net/backups";

        Assert.False(vm.EditIsS3);
    }

    [Fact]
    public void AProviderUrlMismatchIsRefusedBothWays()
    {
        // Paste an Azure URL, then click back to S3: the mismatch the save must not guess at.
        var vm = NewViewModel();
        vm.AddNewCommand.Execute(null);
        vm.EditName = "bucket";
        vm.EditContainerUrl = "https://acct.blob.core.windows.net/backups";
        vm.EditProvider = StorageProviderChoice.S3;
        vm.EditS3KeyId = "AKIDEXAMPLE";
        vm.EditS3SecretKey = "secret";
        vm.SaveCommand.Execute(null);
        Assert.Contains("not an s3://", vm.ErrorMessage);
        Assert.Empty(_store.Config.BlobContainers);

        // The mirror: an s3:// URL with Azure re-selected over it.
        var vm2 = NewViewModel();
        vm2.AddNewCommand.Execute(null);
        vm2.EditName = "bucket";
        vm2.EditContainerUrl = "s3://storage.example.com/backups";
        vm2.EditProvider = StorageProviderChoice.Azure;
        vm2.EditSasToken = "sv=2026&sig=x";
        vm2.SaveCommand.Execute(null);
        Assert.Contains("S3-compatible", vm2.ErrorMessage);
        Assert.Empty(_store.Config.BlobContainers);
    }

    // ── saving ──────────────────────────────────────────────────────────────────

    [Fact]
    public void SavingComposesThePairAndCarriesTheRegion()
    {
        var vm = NewViewModel();
        vm.AddNewCommand.Execute(null);
        vm.EditName = "bucket";
        vm.EditContainerUrl = "s3://s3.eu-west-2.amazonaws.com/backups";
        vm.EditS3KeyId = "  AKIDEXAMPLE ";
        vm.EditS3SecretKey = "wJalrXUtnFEMIEXAMPLE";
        vm.EditS3Region = "eu-west-2";

        vm.SaveCommand.Execute(null);

        var saved = Assert.Single(_store.Config.BlobContainers);
        Assert.Equal("eu-west-2", saved.S3Region);
        Assert.Equal(BlobAuthMode.SasToken, saved.AuthMode);
        // One string, the engine's own secret format, clipboard whitespace trimmed away.
        Assert.Equal("AKIDEXAMPLE:wJalrXUtnFEMIEXAMPLE", _store.GetSasToken(saved));
        Assert.False(vm.IsEditing);
    }

    [Fact]
    public void HalfAPairIsRefusedAtTheForm()
    {
        var vm = NewViewModel();
        vm.AddNewCommand.Execute(null);
        vm.EditName = "bucket";
        vm.EditContainerUrl = "s3://storage.example.com/backups";
        vm.EditS3KeyId = "AKIDEXAMPLE";

        vm.SaveCommand.Execute(null);

        Assert.Contains("Both halves", vm.ErrorMessage);
        Assert.Empty(_store.Config.BlobContainers);
        Assert.True(vm.IsEditing);
    }

    [Fact]
    public void AColonInTheSecretIsRefusedAtTheForm()
    {
        // The one shape the engine's credential format cannot carry - refused where it can be
        // fixed, not discovered as an authentication failure at restore time.
        var vm = NewViewModel();
        vm.AddNewCommand.Execute(null);
        vm.EditName = "bucket";
        vm.EditContainerUrl = "s3://storage.example.com/backups";
        vm.EditS3KeyId = "AKIDEXAMPLE";
        vm.EditS3SecretKey = "se:cret";

        vm.SaveCommand.Execute(null);

        Assert.Contains("colon", vm.ErrorMessage);
        Assert.Empty(_store.Config.BlobContainers);
    }

    [Fact]
    public void AUrlWithoutABucketIsRefusedAtTheForm()
    {
        var vm = NewViewModel();
        vm.AddNewCommand.Execute(null);
        vm.EditName = "bucket";
        vm.EditContainerUrl = "s3://host-only.example.com";
        vm.EditS3KeyId = "AKIDEXAMPLE";
        vm.EditS3SecretKey = "secret";

        vm.SaveCommand.Execute(null);

        Assert.Contains("s3://endpoint[:port]/bucket", vm.ErrorMessage);
        Assert.Empty(_store.Config.BlobContainers);
    }

    [Fact]
    public void EditingKeepsTheStoredPairWhenTheBoxesStayEmpty()
    {
        var container = SavedS3Container();
        var vm = NewViewModel();
        vm.SelectedContainer = vm.Containers.Single();

        vm.EditCommand.Execute(null);
        // Editing an s3 container opens with the provider already selected.
        Assert.True(vm.EditIsS3);
        // The region is shown back (addressing, not a secret); the pair is not.
        Assert.Equal("eu-west-2", vm.EditS3Region);
        Assert.Equal(string.Empty, vm.EditS3KeyId);
        Assert.True(vm.HasStoredSasToken);

        vm.EditS3Region = "eu-west-1";
        vm.SaveCommand.Execute(null);

        Assert.Equal("eu-west-1", Assert.Single(_store.Config.BlobContainers).S3Region);
        Assert.Equal("AKIDSTORED:stored-secret", _store.GetSasToken(container));
    }

    // ── the SAS-isms stay off it ────────────────────────────────────────────────

    [Fact]
    public void NoExpiryLineAppearsForAKeyPair()
    {
        // The stored pair has no se= to read. The old fallback said "SAS token states no
        // expiry", which is an answer to a question nobody asked about a bucket.
        SavedS3Container();
        var vm = NewViewModel();

        vm.SelectedContainer = vm.Containers.Single();

        Assert.True(string.IsNullOrEmpty(vm.SasExpiryText));
        Assert.False(vm.IsSasExpired);
    }

    [Fact]
    public void TheDetailsPaneNamesTheKeyPairNotTheSas()
    {
        Assert.Equal("S3 access key pair", SavedS3Container().AuthDisplay);
        Assert.Equal("SAS token", new BlobContainerConfig
        {
            ContainerUrl = "https://acct.blob.core.windows.net/backups"
        }.AuthDisplay);
    }

    // ── test connection tests the screen ────────────────────────────────────────

    [Fact]
    public async Task TestConnectionCarriesThePairAndRegionFromTheForm()
    {
        var vm = NewViewModel();
        vm.AddNewCommand.Execute(null);
        vm.EditName = "bucket";
        vm.EditContainerUrl = "s3://storage.example.com/backups";
        vm.EditS3KeyId = "AKIDEXAMPLE";
        vm.EditS3SecretKey = "secret";
        vm.EditS3Region = "eu-central-2";

        await vm.TestConnectionCommand.ExecuteAsync(null);

        var tested = _blobs.LastConfig!;
        Assert.Equal("AKIDEXAMPLE:secret", tested.UnsavedSasToken);
        Assert.Equal("eu-central-2", tested.S3Region);
        // In memory only - nothing was persisted by a test (#12).
        Assert.Empty(_store.ListCredentialKeys("NineLives:Blob:"));
    }

    [Fact]
    public async Task TestConnectionFallsBackToTheStoredPairWithTheScreensRegion()
    {
        SavedS3Container();
        var vm = NewViewModel();
        vm.SelectedContainer = vm.Containers.Single();
        vm.EditCommand.Execute(null);

        // Fixing the region is the likeliest reason to re-test an S3 container - the test
        // must combine the stored secret with the region on screen, not the saved one.
        vm.EditS3Region = "eu-west-1";
        await vm.TestConnectionCommand.ExecuteAsync(null);

        var tested = _blobs.LastConfig!;
        Assert.Equal("AKIDSTORED:stored-secret", tested.UnsavedSasToken);
        Assert.Equal("eu-west-1", tested.S3Region);
    }

    // ── the refusals explain themselves ─────────────────────────────────────────

    [Theory]
    [InlineData("InvalidAccessKeyId", "does not recognise this access key id")]
    [InlineData("SignatureDoesNotMatch", "Re-enter the pair")]
    [InlineData("AccessDenied", "s3:ListBucket")]
    [InlineData("RequestTimeTooSkewed", "clock")]
    [InlineData("NoSuchBucket", "first path segment")]
    [InlineData("PermanentRedirect", "different region")]
    [InlineData("AuthorizationHeaderMalformed", "different region")]
    public void TheExplainerTurnsTheCodeIntoTheNextMove(string code, string expectFragment)
    {
        var guidance = BlobFailureExplainer.Explain(
            new S3RequestFailedException("refused", code, 403),
            new BlobContainerConfig { ContainerUrl = "s3://storage.example.com/backups" });

        Assert.NotNull(guidance);
        Assert.Contains(expectFragment, guidance);
    }

    [Fact]
    public void AnUnknownCodeAddsNothing()
    {
        // The provider's own message is already shown; guidance that merely rephrases it is
        // noise. Null means "let it stand".
        Assert.Null(BlobFailureExplainer.Explain(
            new S3RequestFailedException("refused", "SlowDown", 503),
            new BlobContainerConfig { ContainerUrl = "s3://storage.example.com/backups" }));
    }
}
