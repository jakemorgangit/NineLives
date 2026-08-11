namespace Blackcat.NineLives.Services;

/// <summary>
/// Which IDENTITY a server-side credential was created with, as far as RESTORE FROM URL cares (#145).
///
/// This used to be a bool - "is it SHARED ACCESS SIGNATURE" - which made a managed-identity
/// credential indistinguishable from a Windows account or a typo. Those need opposite treatment:
/// one authenticates a restore perfectly well, the others cannot, and the app was overwriting
/// both on the strength of the same false answer.
/// </summary>
public enum BlobCredentialIdentity
{
    /// <summary>No credential of that name exists on the instance.</summary>
    Missing,

    /// <summary>WITH IDENTITY = 'SHARED ACCESS SIGNATURE' - the only kind this app creates.</summary>
    SharedAccessSignature,

    /// <summary>
    /// WITH IDENTITY = 'Managed Identity' - the instance authenticating to storage as itself, on
    /// SQL Server 2022+ or Azure SQL MI. Valid for a restore, and not something this app can
    /// create yet, so wherever one is found it belongs to somebody else's deliberate setup.
    /// </summary>
    ManagedIdentity,

    /// <summary>
    /// WITH IDENTITY = 'S3 Access Key' - the S3 connector's identity (#51), SQL Server 2022+.
    /// The secret is the literal pair 'AccessKeyId:SecretKey'. Only meaningful under an
    /// s3:// credential name, which is the only place this app writes one.
    /// </summary>
    S3AccessKey,

    /// <summary>Anything else: a Windows account, a storage account key, a mistake. Not usable.</summary>
    Other
}

/// <summary>
/// What a credential lookup found. The identity string is carried alongside so an unusable
/// credential can be named rather than merely rejected - the same reasoning as the Entra sign-in
/// diagnostics (#29): being told WHAT is there is usually enough to see why it is wrong.
///
/// It is never the secret. SQL Server does not return credential secrets, and
/// <c>credential_identity</c> for a SAS credential is the literal text 'SHARED ACCESS SIGNATURE'.
/// </summary>
public readonly record struct BlobCredentialStatus(BlobCredentialIdentity Kind, string? Identity)
{
    /// <summary>A credential of that name is on the instance, whatever it turned out to be.</summary>
    public bool Exists => Kind != BlobCredentialIdentity.Missing;

    /// <summary>Whether a RESTORE FROM URL can authenticate with it as it stands.</summary>
    public bool CanRestoreFromUrl =>
        Kind is BlobCredentialIdentity.SharedAccessSignature
             or BlobCredentialIdentity.ManagedIdentity
             or BlobCredentialIdentity.S3AccessKey;

    /// <summary>Nothing of that name on the server.</summary>
    public static BlobCredentialStatus Missing => new(BlobCredentialIdentity.Missing, null);
}
