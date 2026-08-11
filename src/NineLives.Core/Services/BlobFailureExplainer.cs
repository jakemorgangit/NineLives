using Azure;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Turns an Azure storage failure into something a DBA can act on (#29).
///
/// Azure's own messages are accurate and useless in equal measure. "This request is not authorized
/// to perform this operation using this permission" arrives with a request id, a timestamp, the
/// original XML and eleven headers, and says nothing about what to actually change.
///
/// The same instinct as the recovery guidance after a failed restore (#14): the moment something
/// fails is the moment to say what to do about it, not to hand over the raw wire response and let
/// someone go and search for it.
/// </summary>
internal static class BlobFailureExplainer
{
    /// <summary>
    /// Guidance for a failure, or null when there is nothing useful to add and the provider's own
    /// message should stand on its own.
    /// </summary>
    internal static string? Explain(Exception exception, BlobContainerConfig config)
    {
        // The S3 codes are part of the contract every compatible provider imitates, which makes
        // them the reliable thing to switch on - the message text gets rephrased per provider,
        // and it is already shown above this guidance anyway (#51).
        if (exception is S3RequestFailedException s3)
        {
            return s3.Code switch
            {
                "InvalidAccessKeyId" =>
                    "The endpoint does not recognise this access key id. Check it was pasted "
                    + "whole, and that the key belongs to this provider and account - a key from "
                    + "a different account looks identical.",
                "SignatureDoesNotMatch" =>
                    "The endpoint recognised the access key id, but the signature computed from "
                    + "the secret key is wrong. Re-enter the pair. If the pair is definitely "
                    + "right, check the Region field - some providers answer with this code when "
                    + "the request is signed for the wrong region.",
                "AccessDenied" =>
                    "The key pair is valid but not allowed to list this bucket. The key needs "
                    + "permission to list the bucket and read its objects (on AWS: s3:ListBucket "
                    + "and s3:GetObject) - listing is how backups are discovered here, and "
                    + "reading is how the engine restores them.",
                "RequestTimeTooSkewed" =>
                    "This machine's clock is too far from the provider's. Every signed request "
                    + "carries a timestamp the provider checks - correct the clock (Windows time "
                    + "synchronisation) and try again.",
                "NoSuchBucket" =>
                    "The endpoint answered, but the bucket does not exist there. The bucket is "
                    + "the first path segment of the s3:// URL - check its spelling, and that "
                    + "the endpoint host is the one serving the bucket's region.",
                "PermanentRedirect" or "AuthorizationHeaderMalformed" =>
                    "The bucket lives in a different region than the request was signed for. Set "
                    + "the Region field to the bucket's real region - the provider's message "
                    + "above usually names it.",
                _ => null
            };
        }

        if (exception is not RequestFailedException failure) return null;

        return failure.ErrorCode switch
        {
            "AuthorizationPermissionMismatch" when config.AuthMode.IsEntra() => EntraRoleMissing(config),
            "AuthorizationPermissionMismatch" => SasTooNarrow(),
            "AuthenticationFailed" when config.AuthMode.IsEntra() =>
                "The sign-in itself was rejected. Check the account has access to the tenant that "
                + "owns this storage account - an account from another tenant can sign in "
                + "successfully and still be unknown here.",
            "AuthenticationFailed" =>
                "The SAS token was rejected. It may be malformed, expired, or issued for a "
                + "different storage account.",
            "ContainerNotFound" =>
                "The account was reached but the container does not exist. Check the last segment "
                + "of the URL - it is the container name, and it is case-sensitive.",
            "AccountIsDisabled" => "The storage account is disabled.",
            "InvalidResourceName" =>
                "The container name in the URL is not a legal Azure container name.",
            _ => null
        };
    }

    /// <summary>
    /// The one worth spelling out, because the fix is not the one people reach for first.
    ///
    /// Blob DATA access is a separate set of roles from the ones that manage the account. Owner and
    /// Contributor - which is what someone who can see the container in the portal usually has -
    /// grant neither. And it is why other tools appear to disagree: Azure Storage Explorer often
    /// falls back to fetching the account KEY through the management plane, which Owner does allow,
    /// so it never exercises the role at all.
    /// </summary>
    private static string EntraRoleMissing(BlobContainerConfig config)
    {
        var scope = config.ContainerName is { Length: > 0 } name
            ? $"the \"{name}\" container (or the storage account above it)"
            : "the container or the storage account above it";

        return
            $"Signed in successfully, but this account has no permission to READ BLOB DATA in {scope}.\n\n"
            + "Blob data access is a separate set of roles from the ones that administer the account. "
            + "Owner and Contributor do not include it. Assign Storage Blob Data Reader (or Storage "
            + "Blob Data Contributor) to the account named above, at the container or storage "
            + "account scope.\n\n"
            + "If Azure Storage Explorer works with the same account, that is not a contradiction - "
            + "it commonly falls back to the account key, which Owner can fetch, so it never uses "
            + "the role at all.\n\n"
            + "Role assignments can take a few minutes to take effect.";
    }

    private static string SasTooNarrow() =>
        "The SAS token was accepted but does not permit this operation. It needs at least List and "
        + "Read permissions, and its resource type must include the container.";
}
