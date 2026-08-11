using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The signer against AWS's own published answers (#51). SigV4 is hand-rolled here instead of
/// carried in by the SDK, and the entire safety case for that is these pins: the worked example
/// in the Signature Version 4 documentation (the IAM ListUsers request, whose every
/// intermediate value AWS publishes) and the get-vanilla case from the official
/// aws-sig-v4-test-suite. Every stage is pinned separately - canonical request, derived key,
/// final signature, whole header - so when a refactor drifts a byte, the failure names the
/// stage that moved rather than just "signature wrong".
/// </summary>
public class S3SignerTests
{
    // The suite's shared credential scope. The secret is AWS's published test secret, not a
    // real one.
    private const string KeyId = "AKIDEXAMPLE";
    private const string Secret = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
    private const string AmzDate = "20150830T123600Z";

    // ── the documented IAM example, stage by stage ──────────────────────────────

    private static readonly (string, string)[] IamQuery =
        [("Action", "ListUsers"), ("Version", "2010-05-08")];

    private static readonly (string, string)[] IamHeaders =
    [
        ("content-type", "application/x-www-form-urlencoded; charset=utf-8"),
        ("host", "iam.amazonaws.com"),
        ("x-amz-date", AmzDate)
    ];

    [Fact]
    public void TheCanonicalRequestHashesToTheDocumentedValue()
    {
        var canonical = S3RequestSigner.CanonicalRequest(
            "GET", "/", S3RequestSigner.CanonicalQuery(IamQuery), IamHeaders,
            S3RequestSigner.EmptyPayloadHash);

        Assert.Equal(
            "f536975d06c0309214f805bb90ccff089219ecd68b2577efef23edd43b7e1a59",
            S3RequestSigner.HexSha256(canonical));
    }

    [Fact]
    public void TheSigningKeyDerivesToTheDocumentedValue()
    {
        Assert.Equal(
            "c4afb1cc5771d871763a393e44b703571b55cc28424d1a5e86da6ed3c154a4b9",
            Convert.ToHexStringLower(
                S3RequestSigner.SigningKey(Secret, "20150830", "us-east-1", "iam")));
    }

    [Fact]
    public void TheWholeHeaderMatchesTheDocumentedValue()
    {
        var authorization = S3RequestSigner.Authorization(
            "GET", "/", IamQuery, IamHeaders, S3RequestSigner.EmptyPayloadHash,
            KeyId, Secret, "us-east-1", "iam", AmzDate);

        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/iam/aws4_request, " +
            "SignedHeaders=content-type;host;x-amz-date, " +
            "Signature=5d672d79c15b13162d9279b0855cfba6789a8edb4c82c400e06b5924a6f2b5d7",
            authorization);
    }

    // ── the official test suite's plainest case ─────────────────────────────────

    [Fact]
    public void GetVanillaSignsToTheSuitesAnswer()
    {
        var authorization = S3RequestSigner.Authorization(
            "GET", "/", [],
            [("host", "example.amazonaws.com"), ("x-amz-date", AmzDate)],
            S3RequestSigner.EmptyPayloadHash,
            KeyId, Secret, "us-east-1", "service", AmzDate);

        Assert.EndsWith(
            "Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31",
            authorization);
    }

    // ── the encoding rules the signature stands on ──────────────────────────────

    [Theory]
    [InlineData("FULL/SRV01 backups", false, "FULL%2FSRV01%20backups")]
    [InlineData("FULL/SRV01 backups", true, "FULL/SRV01%20backups")]
    [InlineData("A-b_c.d~e", false, "A-b_c.d~e")]
    [InlineData("key:pair+x", false, "key%3Apair%2Bx")]
    public void UriEncodingIsTheStrictRfcForm(string raw, bool keepSlash, string expected)
    {
        Assert.Equal(expected, S3RequestSigner.UriEncode(raw, keepSlash));
    }

    [Fact]
    public void UriEncodingSpeaksUtf8PerByte()
    {
        // ü is 0xC3 0xBC in UTF-8: two escapes, uppercase hex - the form the server rebuilds
        // before it re-derives the signature.
        Assert.Equal("%C3%BC", S3RequestSigner.UriEncode("ü"));
    }

    [Fact]
    public void TheCanonicalQuerySortsByEncodedName()
    {
        // prefix > list-type ordinally, and the value's slash encodes; the sort happens after
        // encoding, which is the detail the specification is picky about.
        Assert.Equal(
            "list-type=2&prefix=FULL%2Fa%20b",
            S3RequestSigner.CanonicalQuery([("prefix", "FULL/a b"), ("list-type", "2")]));
    }

    [Fact]
    public void TheEmptyPayloadHashIsSha256OfNothing()
    {
        Assert.Equal(S3RequestSigner.EmptyPayloadHash, S3RequestSigner.HexSha256(""));
    }
}
