namespace Blackcat.NineLives.Services;

/// <summary>
/// Whether an instance can authenticate to blob storage with a managed identity, and what it said
/// when asked (#147).
///
/// The readings are kept, not just the verdict. Telling somebody "your instance cannot do this" is
/// far less useful than telling them it is SQL Server 2019 and the feature needs 2022 - especially
/// for somebody who has just been told their organisation forbids SAS tokens and is now being told
/// the alternative is unavailable too.
/// </summary>
/// <param name="IsSupported">Whether the option should be offered at all.</param>
/// <param name="ProductMajorVersion">SERVERPROPERTY('ProductMajorVersion'), or null if it did not say.</param>
/// <param name="EngineEdition">SERVERPROPERTY('EngineEdition'), or null if it did not say.</param>
public readonly record struct ManagedIdentitySupport(
    bool IsSupported, int? ProductMajorVersion, int? EngineEdition)
{
    /// <summary>Why not, in terms of what to go and change.</summary>
    public string Explain() =>
        IsSupported
            ? string.Empty
            : BlobCredentialStatement.ExplainUnsupported(ProductMajorVersion);
}
