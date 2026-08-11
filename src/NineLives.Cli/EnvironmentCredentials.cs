using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Cli;

/// <summary>
/// Zero-persistence definitions from environment variables (#302), activated by --ephemeral.
///
/// add-server and add-container closed most of the service-account gap, but they PERSIST:
/// config in the profile, secrets in that user's Credential Manager. Two cases want neither -
/// locked-down service accounts that must not own vault state, and CI agents where the
/// machine is shared and nothing should outlive the job. The secrets ride on the objects
/// (UnsavedPassword, UnsavedSasToken), which every service already prefers over the vault,
/// so nothing is written anywhere. History receipts still write - they are the point - and
/// the configured webhooks still fire.
/// </summary>
internal static class EnvironmentCredentials
{
    /// <summary>
    /// The ephemeral server, or null when NINELIVES_SERVER is not set. SQL auth when
    /// NINELIVES_SQL_USER is present (password from NINELIVES_SQL_PASSWORD, in memory only);
    /// Windows auth otherwise. Resolvable by the name NINELIVES_SERVER_NAME, default "env".
    /// </summary>
    public static ServerConnection? Server(Func<string, string?> env)
    {
        var address = env("NINELIVES_SERVER");
        if (string.IsNullOrWhiteSpace(address)) return null;

        var user = env("NINELIVES_SQL_USER");
        return new ServerConnection
        {
            Id = "env-server",
            Name = env("NINELIVES_SERVER_NAME") ?? "env",
            ServerName = address,
            AuthMode = string.IsNullOrWhiteSpace(user) ? AuthMode.WindowsAuth : AuthMode.SqlAuth,
            Username = string.IsNullOrWhiteSpace(user) ? null : user,
            UnsavedPassword = env("NINELIVES_SQL_PASSWORD")
        };
    }

    /// <summary>
    /// The ephemeral container, or null when NINELIVES_CONTAINER_URL is not set. The SAS from
    /// NINELIVES_SAS rides in memory only. Resolvable by NINELIVES_CONTAINER_NAME, default "env".
    /// </summary>
    public static BlobContainerConfig? Container(Func<string, string?> env)
    {
        var url = env("NINELIVES_CONTAINER_URL");
        if (string.IsNullOrWhiteSpace(url)) return null;

        // The credential comes from NINELIVES_SAS for either provider - it is one string
        // in both cases (a SAS token, or the S3 AccessKeyId:SecretKey pair) and rides the
        // same in-memory member (#51). No separate S3 variable is needed.
        var secret = env("NINELIVES_SAS");
        return new BlobContainerConfig
        {
            Id = "env-container",
            Name = env("NINELIVES_CONTAINER_NAME") ?? "env",
            ContainerUrl = url,
            S3Region = url.StartsWith("s3://", StringComparison.OrdinalIgnoreCase)
                ? env("NINELIVES_S3_REGION")
                : null,
            UnsavedSasToken = string.IsNullOrWhiteSpace(secret) ? null : secret
        };
    }
}
