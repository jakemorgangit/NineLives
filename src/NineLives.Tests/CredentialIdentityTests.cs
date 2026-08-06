using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Reading a credential's identity, and saying what was found (#145).
///
/// This used to be one bool - "is the identity SHARED ACCESS SIGNATURE" - which put a managed
/// identity, a Windows account and a typo in the same bucket. The restore path then acted on that
/// bucket by overwriting it, so the classification is what the destructive decision hangs off.
/// </summary>
public class CredentialIdentityTests
{
    [Theory]
    [InlineData("SHARED ACCESS SIGNATURE")]
    [InlineData("shared access signature")]
    public void ASasIdentityIsRecognisedWhateverItsCase(string identity)
        => Assert.Equal(
            BlobCredentialIdentity.SharedAccessSignature,
            SqlServerService.ClassifyIdentity(identity));

    /// <summary>
    /// Case is not guaranteed here either: the docs write it both ways, and whoever created the
    /// credential typed one of them. Getting this wrong is not a cosmetic miss - an unrecognised
    /// managed identity is exactly the input that used to be overwritten.
    /// </summary>
    [Theory]
    [InlineData("Managed Identity")]
    [InlineData("MANAGED IDENTITY")]
    [InlineData("managed identity")]
    public void AManagedIdentityIsRecognisedWhateverItsCase(string identity)
        => Assert.Equal(
            BlobCredentialIdentity.ManagedIdentity,
            SqlServerService.ClassifyIdentity(identity));

    [Theory]
    [InlineData("MYDOMAIN\\svc_sql")]
    [InlineData("mystorageaccount")]
    [InlineData("")]
    public void AnythingElseIsOther(string identity)
        => Assert.Equal(BlobCredentialIdentity.Other, SqlServerService.ClassifyIdentity(identity));

    [Theory]
    [InlineData(BlobCredentialIdentity.SharedAccessSignature, true)]
    [InlineData(BlobCredentialIdentity.ManagedIdentity, true)]
    [InlineData(BlobCredentialIdentity.Other, false)]
    [InlineData(BlobCredentialIdentity.Missing, false)]
    public void OnlyTheTwoIdentitiesARestoreCanUseCountAsUsable(
        BlobCredentialIdentity kind, bool expected)
        => Assert.Equal(expected, new BlobCredentialStatus(kind, "x").CanRestoreFromUrl);

    [Fact]
    public void OnlyAMissingCredentialReportsThatItIsAbsent()
    {
        Assert.False(BlobCredentialStatus.Missing.Exists);
        Assert.True(new BlobCredentialStatus(BlobCredentialIdentity.Other, "x").Exists);
    }

    /// <summary>
    /// The panel's wording. A managed identity must not be described as a problem: the old message
    /// said "restore may fail" about a credential that would have worked, which is the sentence
    /// that invited somebody to press the button and convert it.
    /// </summary>
    [Fact]
    public void AManagedIdentityIsDescribedAsValid()
    {
        var message = RestoreViewModel.DescribeCredential(
            new BlobCredentialStatus(BlobCredentialIdentity.ManagedIdentity, "Managed Identity"));

        Assert.Contains("valid", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Managed Identity", message);
        Assert.DoesNotContain("may fail", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An unusable one is named. "Not a SAS credential" was true of every identity that is not a
    /// SAS credential, which left no way to tell a mistake from somebody's deliberate arrangement.
    /// </summary>
    [Fact]
    public void AnUnusableCredentialIsNamedRatherThanJustRejected()
    {
        var message = RestoreViewModel.DescribeCredential(
            new BlobCredentialStatus(BlobCredentialIdentity.Other, "MYDOMAIN\\svc_sql"));

        Assert.Contains("MYDOMAIN\\svc_sql", message);
    }

    [Fact]
    public void AMissingCredentialSaysSo()
    {
        var message = RestoreViewModel.DescribeCredential(BlobCredentialStatus.Missing);

        Assert.Contains("not present", message, StringComparison.OrdinalIgnoreCase);
    }
}
