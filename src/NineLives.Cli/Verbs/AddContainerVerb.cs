using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Cli.Verbs;

/// <summary>
/// The blob half of configuration-from-nothing: record a container and its SAS, and prove the
/// SAS can actually reach it - the proof being the point. A SAS is a string that LOOKS right
/// for weeks after it expired, and the worst place to discover that is the restore.
/// </summary>
internal static class AddContainerVerb
{
    public static readonly VerbSpec Spec = new(
        "add-container",
        "Add or update a blob container, and prove the SAS reaches it",
        "9lives add-container --name NAME --url CONTAINER_URL [--sas TOKEN] [--no-validate]",
        Valued: ["name", "url", "sas"],
        Switches: ["no-validate"],
        Options:
        [
            ("--name NAME", "The configured name the other verbs refer to (--container " +
                "NAME). An existing entry with this name is updated in place, so a " +
                "provisioning script re-runs cleanly - and a SAS refresh is just this verb " +
                "again with the new token."),
            ("--url CONTAINER_URL", "The container URL, no SAS in it: " +
                "https://account.blob.core.windows.net/container"),
            ("--sas TOKEN", "The SAS token, with or without its leading '?'. Stored in the " +
                "Windows Credential Manager exactly as the app stores it. Prefer the " +
                "NINELIVES_SAS environment variable in scripts - a variable stays out of " +
                "shell history and process listings; a flag does not."),
            ("--no-validate", "Record without reaching for the container. The default is " +
                "to prove the SAS works before anything downstream trusts it.")
        ],
        Notes:
        [
            "Writes the same configuration the desktop app maintains, secret in the vault, " +
            "so the app's browser and the CLI's verbs all see the container this recorded.",
            "Validation asks the container to answer using exactly the recorded SAS - " +
            "reachability, the account name, the container name and the token's validity " +
            "all proven in one call. Restore needs List and Read permissions on the SAS; " +
            "the app's SAS guidance in the README spells the grants out.",
            "A failed validation still records the entry (a re-run with a fresh token " +
            "overwrites it) but exits 3, so a chained script stops before the restore " +
            "discovers it the hard way. The SAS itself is never echoed back.",
            "Entra-authenticated containers are configured in the app, where the sign-in " +
            "can happen - this verb is the SAS route, which is the one a headless template " +
            "can use."
        ],
        ExitCodes:
        [
            "0   saved - and, unless --no-validate, the SAS proven against the container",
            "3   saved, but the container did not answer with this SAS - fix and re-run",
            "64  usage"
        ],
        Examples:
        [
            ("9lives add-container --name backups --url https://acct.blob.core.windows.net/sqlbackups --sas %SAS%",
                "a provisioning script's second line: where the backups live"),
            ("9lives add-container --name backups --url https://acct.blob.core.windows.net/sqlbackups",
                "same, with the token read from NINELIVES_SAS")
        ]);

    public static async Task<int> RunAsync(
        CliArguments args, CliServices services, TextWriter output, TextWriter errors)
    {
        var name = args.Get("name");
        var url = args.Get("url");
        if (name == null || url == null)
        {
            errors.WriteLine("add-container needs --name and --url.");
            return ExitCodes.Usage;
        }

        var sas = args.Get("sas") ?? Environment.GetEnvironmentVariable("NINELIVES_SAS");
        if (string.IsNullOrEmpty(sas))
        {
            errors.WriteLine("add-container needs the token: --sas, or the NINELIVES_SAS " +
                             "environment variable (preferred in scripts - it stays out of " +
                             "process listings).");
            return ExitCodes.Usage;
        }

        var config = services.Store.LoadConfig();
        var existing = config.BlobContainers.FirstOrDefault(
            c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        var container = existing ?? new BlobContainerConfig { Id = BlobContainerConfig.NewId() };

        container.Name = name;
        container.ContainerUrl = url.TrimEnd('/');
        container.AuthMode = BlobAuthMode.SasToken;

        if (existing == null) config.BlobContainers.Add(container);
        services.Store.SaveConfig(config);
        services.Store.SaveSasToken(container, sas.TrimStart('?'));

        errors.WriteLine($"{(existing == null ? "Added" : "Updated")} container '{name}' -> " +
                         $"{container.ContainerUrl}.");

        if (args.Has("no-validate"))
        {
            errors.WriteLine("Not validated, as asked.");
            return ExitCodes.Ok;
        }

        try
        {
            if (await services.Blobs.VerifyConnectionAsync(container))
            {
                errors.WriteLine("Validated: the SAS reaches the container.");
                return ExitCodes.Ok;
            }

            errors.WriteLine("VALIDATION FAILED: the container did not answer with this SAS.");
        }
        catch (Exception ex)
        {
            errors.WriteLine($"VALIDATION FAILED: {ex.Message}");
        }

        errors.WriteLine("The entry is saved - check the URL, the token's expiry and its " +
                         "List/Read permissions, then re-run; a script chained on exit " +
                         "codes stops here.");
        return ExitCodes.CouldNotAnswer;
    }
}
