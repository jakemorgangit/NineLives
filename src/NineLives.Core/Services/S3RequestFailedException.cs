namespace Blackcat.NineLives.Services;

/// <summary>
/// An S3 endpoint's refusal with the provider's own code attached (#51).
///
/// InvalidOperationException underneath, so every existing catch treats it as the ordinary
/// operational failure it is. The code exists for BlobFailureExplainer: AccessDenied,
/// SignatureDoesNotMatch and RequestTimeTooSkewed each have a different next move, and the
/// code is the only reliable way to tell them apart - providers rephrase the message text,
/// but the code is part of the S3 contract they are all imitating.
/// </summary>
public sealed class S3RequestFailedException(string message, string? code, int status)
    : InvalidOperationException(message)
{
    /// <summary>The error code from the response body (AccessDenied, NoSuchBucket...), when
    /// the body carried the expected XML. Null for a bare status line.</summary>
    public string? Code { get; } = code;

    /// <summary>The HTTP status the refusal arrived with.</summary>
    public int Status { get; } = status;
}
