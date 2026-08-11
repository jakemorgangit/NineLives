namespace Blackcat.NineLives.Services;

/// <summary>
/// The S3 key pair as one string (#51): <c>AccessKeyId:SecretKey</c> - which is literally the
/// engine's CREATE CREDENTIAL secret format, so the pair rides the entire existing SAS
/// pipeline unchanged: same vault slot, same in-memory-only member, same export-strips-secrets
/// rule, same environment variable. The price of that reuse is a shape rule enforced at entry.
/// </summary>
public static class S3Credentials
{
    /// <summary>
    /// Why the value cannot be used as an S3 credential secret - null when it can. The engine
    /// requires exactly <c>AccessKeyId:SecretKey</c>, and a colon inside the secret key is
    /// documented as unsupported, so a value with two colons is ambiguous and refused rather
    /// than guessed at.
    /// </summary>
    public static string? Validate(string? pair)
    {
        if (string.IsNullOrWhiteSpace(pair))
            return "An S3 credential is the pair AccessKeyId:SecretKey.";

        var colon = pair.IndexOf(':');
        if (colon <= 0)
            return "An S3 credential is the pair AccessKeyId:SecretKey - the colon separates " +
                   "the two, and both sides are required.";
        if (colon == pair.Length - 1)
            return "The secret key is missing after the colon.";
        if (pair.IndexOf(':', colon + 1) >= 0)
            return "The secret key cannot contain a colon - SQL Server's S3 credential format " +
                   "does not support one.";

        return null;
    }

    /// <summary>The two halves, for callers that need them separately (the listing client signs
    /// requests with them individually). Null when the shape is wrong.</summary>
    public static (string KeyId, string Secret)? Split(string? pair)
    {
        if (Validate(pair) != null) return null;

        var colon = pair!.IndexOf(':');
        return (pair[..colon], pair[(colon + 1)..]);
    }
}
