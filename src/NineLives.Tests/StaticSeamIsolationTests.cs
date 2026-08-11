using System.Reflection;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The test classes that set process-wide static seams do not run beside each other (#348).
///
/// xUnit runs collections in parallel and the classes within one in sequence. Two seams in Core -
/// BlobStorageService.CredentialFactoryForTests and S3ListingClient.SenderForTests - are plain
/// static fields, set by one class and cleared in its Dispose. A second class reading them
/// concurrently is served the first one's fake, or has the seam nulled underneath it mid-test.
///
/// That failure is the expensive kind: perhaps one run in ten, on CI rather than locally, looking
/// like a network flake rather than a fixture problem. It has never happened, because only one
/// class drives each seam today - which is exactly the state that makes it easy to break by
/// writing an ordinary second test.
///
/// This pins the arrangement rather than the reason. It cannot see a NEW class that starts using
/// a seam, and there is no compiler-enforced way to make it - so the rule lives in
/// WpfCollection's own comment, and this catches the attribute being dropped from a class that
/// already has it.
/// </summary>
public class StaticSeamIsolationTests
{
    [Theory]
    [InlineData(typeof(S3ListingTests))]
    [InlineData(typeof(EntraBlobAuthTests))]
    [InlineData(typeof(EntraSignInThreadTests))]
    [InlineData(typeof(EntraWindowHandleTests))]
    public void AClassThatOwnsAStaticSeamIsSerialisedWithTheOthers(Type testClass)
    {
        var collection = testClass.GetCustomAttribute<CollectionAttribute>();

        Assert.True(collection != null,
            $"{testClass.Name} sets a process-wide static seam and must join " +
            $"'{WpfCollection.Name}', or it can run beside another class that sets the same one.");

        // xUnit v3 does not expose the name off the attribute instance, so read the argument
        // the class actually declared.
        var declared = testClass
            .GetCustomAttributesData()
            .Single(a => a.AttributeType == typeof(CollectionAttribute))
            .ConstructorArguments[0].Value as string;

        Assert.Equal(WpfCollection.Name, declared);
    }

    /// <summary>
    /// And the seams are still static, so the rule above still has something to protect. If one
    /// of them ever becomes per-instance - which would be the better fix - this fails and says so,
    /// rather than leaving a collection membership nobody can explain.
    /// </summary>
    [Fact]
    public void TheSeamsThisProtectsAreStillStatic()
    {
        var blob = typeof(Blackcat.NineLives.Services.BlobStorageService)
            .GetProperty("CredentialFactoryForTests",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        var s3 = typeof(Blackcat.NineLives.Services.S3ListingClient)
            .GetField("SenderForTests",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.True(blob != null || s3 != null,
            "Neither seam is static any more - if they are now per-instance, the collection " +
            "membership added for #348 can be reconsidered.");
    }
}
