using System.Text;
using System.Text.Json;
using Azure;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Making an Azure permission failure answerable (#29).
///
/// Azure refuses a data-plane request with 403 AuthorizationPermissionMismatch and nothing else -
/// one message covering "the role is on the management plane", "the role is on the wrong scope" and
/// "this is not the account you think it is". The response carries a request id, the original XML
/// and eleven headers, and says nothing about what to change.
///
/// Same instinct as the recovery guidance after a failed restore (#14): the moment it fails is the
/// moment to say what to do about it.
/// </summary>
public class EntraDiagnosticsTests
{
    private static BlobContainerConfig Container(BlobAuthMode mode = BlobAuthMode.EntraInteractive) => new()
    {
        Id = BlobContainerConfig.NewId(),
        Name = "backups",
        ContainerUrl = "https://mystorageaccount.blob.core.windows.net/sqlbackups",
        AuthMode = mode
    };

    private static RequestFailedException Failure(string errorCode, int status = 403) =>
        new(status, "This request is not authorized to perform this operation using this permission.\n"
            + "RequestId:d0733940-001e-000f-16ae-255e25000000\n"
            + "Time:2026-08-06T14:20:06.5131329Z", errorCode, innerException: null);

    // ── who was refused ─────────────────────────────────────────────────────────

    /// <summary>
    /// A token is a JWT: three base64url segments, the middle one a JSON claims object. Reading the
    /// account out of it is the only way the app can say WHICH identity Azure turned down.
    /// </summary>
    private static string Jwt(object payload)
    {
        static string Segment(string json) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{Segment("{\"alg\":\"RS256\"}")}.{Segment(JsonSerializer.Serialize(payload))}.signature";
    }

    [Fact]
    public void AUserTokenIsDescribedByTheAccountAndTenant()
    {
        var token = Jwt(new { upn = "dba@example.com", tid = "11111111-2222-3333-4444-555555555555" });

        Assert.Equal(
            "dba@example.com (tenant 11111111-2222-3333-4444-555555555555)",
            EntraIdentity.Describe(token));
    }

    /// <summary>Not every token carries upn - a guest or a personal account often has only this.</summary>
    [Fact]
    public void PreferredUsernameIsUsedWhenThereIsNoUpn()
    {
        var token = Jwt(new { preferred_username = "guest@other.com", tid = "abc" });

        Assert.Equal("guest@other.com (tenant abc)", EntraIdentity.Describe(token));
    }

    /// <summary>
    /// A managed identity or service principal has no user at all. Its object id is still the thing
    /// a role assignment is checked against, so it is worth naming.
    /// </summary>
    [Fact]
    public void AManagedIdentityIsDescribedByItsObjectId()
    {
        var token = Jwt(new { oid = "99999999-0000-0000-0000-000000000000", tid = "abc" });

        Assert.Equal("99999999-0000-0000-0000-000000000000 (tenant abc)", EntraIdentity.Describe(token));
    }

    /// <summary>
    /// A bearer token is a live credential for as long as it lasts, and this text goes on screen
    /// and into the log. None of it may come back.
    /// </summary>
    [Fact]
    public void NoPartOfTheTokenIsEverReturned()
    {
        var token = Jwt(new { upn = "dba@example.com", tid = "abc" });

        var described = EntraIdentity.Describe(token);

        Assert.NotNull(described);
        foreach (var segment in token.Split('.'))
            Assert.DoesNotContain(segment, described);
    }

    /// <summary>
    /// This only ever runs to improve an error message. Failing to improve one must not replace it
    /// with a different error.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("only.two")]
    [InlineData("aaa.!!!not-base64!!!.ccc")]
    [InlineData("aaa.eyJub3QiOiJhbiBvYmplY3QifQ.ccc")]
    public void AnUnreadableTokenIsNotAnError(string token)
    {
        var ex = Record.Exception(() => EntraIdentity.Describe(token));

        Assert.Null(ex);
    }

    [Fact]
    public void ATokenWithNothingIdentifyingGivesNothing()
    {
        Assert.Null(EntraIdentity.Describe(Jwt(new { aud = "https://storage.azure.com" })));
    }

    // ── what to do about it ─────────────────────────────────────────────────────

    /// <summary>
    /// The one worth spelling out. Blob DATA access is a separate set of roles from the ones that
    /// administer the account, so the reflex of "but I'm Owner" is exactly the wrong fix.
    /// </summary>
    [Fact]
    public void APermissionMismatchUnderEntraNamesTheRoleThatIsMissing()
    {
        var guidance = BlobFailureExplainer.Explain(
            Failure("AuthorizationPermissionMismatch"), Container());

        Assert.NotNull(guidance);
        Assert.Contains("Storage Blob Data Reader", guidance);
        Assert.Contains("Owner and Contributor do not include it", guidance);
        // Names the container, so it is clear what scope the assignment goes on.
        Assert.Contains("sqlbackups", guidance);
    }

    /// <summary>
    /// The other tool "working" is the most misleading evidence there is, and it comes up first.
    /// Storage Explorer commonly falls back to the account key - which Owner CAN fetch - so it
    /// never exercises the role at all.
    /// </summary>
    [Fact]
    public void ItExplainsWhyStorageExplorerDisagrees()
    {
        var guidance = BlobFailureExplainer.Explain(
            Failure("AuthorizationPermissionMismatch"), Container());

        Assert.Contains("Storage Explorer", guidance);
        Assert.Contains("account key", guidance);
    }

    /// <summary>The same code under SAS means something completely different.</summary>
    [Fact]
    public void APermissionMismatchUnderSasTalksAboutTheToken()
    {
        var guidance = BlobFailureExplainer.Explain(
            Failure("AuthorizationPermissionMismatch"), Container(BlobAuthMode.SasToken));

        Assert.NotNull(guidance);
        Assert.Contains("List and Read", guidance);
        Assert.DoesNotContain("Storage Blob Data Reader", guidance);
    }

    [Theory]
    [InlineData("ContainerNotFound", "case-sensitive")]
    [InlineData("AccountIsDisabled", "disabled")]
    [InlineData("InvalidResourceName", "legal Azure container name")]
    public void TheOtherCommonFailuresAreExplainedToo(string code, string expected)
    {
        Assert.Contains(expected, BlobFailureExplainer.Explain(Failure(code, 404), Container()));
    }

    /// <summary>
    /// Only where there is something worth adding. Inventing guidance for an unrecognised failure
    /// would bury Azure's own message under a guess.
    /// </summary>
    [Fact]
    public void AnUnrecognisedFailureGetsNoGuidance()
    {
        Assert.Null(BlobFailureExplainer.Explain(Failure("SomethingNewFromAzure", 500), Container()));
    }

    [Fact]
    public void ANonAzureFailureGetsNoGuidance()
    {
        Assert.Null(BlobFailureExplainer.Explain(
            new InvalidOperationException("No SAS token found."), Container()));
    }

    // ── what the user ends up reading ───────────────────────────────────────────

    /// <summary>
    /// All three parts, in the order that answers the question fastest: what failed, who was
    /// refused, then what to change. The identity comes before the guidance because it settles the
    /// most common confusion - a permission error against an account that demonstrably has access
    /// usually means a different account was used than the one being thought about.
    /// </summary>
    [Fact]
    public async Task TheTestResultSaysWhatFailedWhoWasRefusedAndWhatToChange()
    {
        var blob = new FakeBlobStorageService
        {
            ListThrows = Failure("AuthorizationPermissionMismatch"),
            SignedInIdentity = "someone.else@example.com (tenant abc)"
        };
        var vm = new BlobConfigViewModel(new FakeCredentialStore(), blob);

        vm.AddNewCommand.Execute(null);
        vm.EditName = "backups";
        vm.EditContainerUrl = "https://mystorageaccount.blob.core.windows.net/sqlbackups";
        vm.EditAuthMode = BlobAuthMode.EntraInteractive;

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.False(vm.TestSuccess);
        Assert.Contains("Connection failed", vm.TestResult);
        Assert.Contains("someone.else@example.com", vm.TestResult);
        Assert.Contains("Storage Blob Data Reader", vm.TestResult);
    }

    /// <summary>
    /// Azure appends the request id, the timestamp, the original XML and every response header to
    /// its exception message. Only the first line is worth putting at the top.
    /// </summary>
    [Fact]
    public async Task TheHeadersAndRequestIdDoNotGetInTheWay()
    {
        var blob = new FakeBlobStorageService { ListThrows = Failure("AuthorizationPermissionMismatch") };
        var vm = new BlobConfigViewModel(new FakeCredentialStore(), blob);

        vm.AddNewCommand.Execute(null);
        vm.EditName = "backups";
        vm.EditContainerUrl = "https://mystorageaccount.blob.core.windows.net/sqlbackups";
        vm.EditAuthMode = BlobAuthMode.EntraInteractive;

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.DoesNotContain("RequestId:", vm.TestResult);
        Assert.DoesNotContain("Time:2026", vm.TestResult);
    }

    /// <summary>A SAS container has no signed-in account, so nothing is claimed about one.</summary>
    [Fact]
    public async Task ASasFailureDoesNotClaimAnIdentity()
    {
        var blob = new FakeBlobStorageService
        {
            ListThrows = Failure("AuthorizationPermissionMismatch"),
            SignedInIdentity = "should-not-be-used@example.com"
        };
        var vm = new BlobConfigViewModel(new FakeCredentialStore(), blob);

        vm.AddNewCommand.Execute(null);
        vm.EditName = "backups";
        vm.EditContainerUrl = "https://mystorageaccount.blob.core.windows.net/sqlbackups";
        vm.EditSasToken = "sv=2024-01-01&sig=x";

        await vm.TestConnectionCommand.ExecuteAsync(null);

        Assert.DoesNotContain("Signed in as", vm.TestResult);
        Assert.DoesNotContain("should-not-be-used", vm.TestResult);
    }
}
