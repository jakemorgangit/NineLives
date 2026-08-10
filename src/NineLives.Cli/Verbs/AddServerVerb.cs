using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.Cli.Verbs;

/// <summary>
/// Configuration from nothing: a freshly provisioned VM has no app config and nobody at a
/// screen to create one, so the CLI creates it - which is what turns a Terraform template into
/// a working restore. Add-or-update by name, so a re-run converges instead of erroring; and
/// validation is the DEFAULT, because a template that records a dead server should fail at
/// the add, not twenty minutes later at the restore.
/// </summary>
internal static class AddServerVerb
{
    public static readonly VerbSpec Spec = new(
        "add-server",
        "Add or update a configured server, and prove it answers",
        "9lives add-server --name NAME --address SERVER[\\INSTANCE] [--user USER --password PW] [--no-validate]",
        Valued: ["name", "address", "user", "password"],
        Switches: ["no-validate"],
        Options:
        [
            ("--name NAME", "The configured name the other verbs refer to (--target NAME, " +
                "--server NAME). An existing entry with this name is updated in place - " +
                "re-running a provisioning script converges rather than failing."),
            ("--address SERVER", "What the connection string gets: host, host\\instance, or " +
                "host,port."),
            ("--user USER", "SQL authentication username. With it, --password (or the " +
                "environment variable) is required; without it, the connection uses " +
                "Windows authentication as the account running the CLI."),
            ("--password PW", "SQL authentication password, stored in the Windows " +
                "Credential Manager exactly as the app stores it. Prefer the " +
                "NINELIVES_SQL_PASSWORD environment variable in scripts - a variable " +
                "stays out of shell history and process listings; a flag does not."),
            ("--no-validate", "Record without connecting. For preparing config the server " +
                "will grow into - the default is to prove the server answers.")
        ],
        Notes:
        [
            "Writes the same configuration the desktop app maintains, in " +
            "%LOCALAPPDATA%\\NineLives, with the password in the Windows Credential " +
            "Manager - so everything the app would show is exactly what the CLI recorded, " +
            "and either front end can carry on from here.",
            "Validation connects and asks the server its version, which proves the address, " +
            "the credentials and the permission to answer in one round trip - the answer is " +
            "printed by name. A failed validation still records the entry (a re-run " +
            "overwrites it) but exits 3, so a script chained on exit codes stops before " +
            "anything downstream trusts a dead server.",
            "Secrets are never echoed back, and the config file holds no secret - the " +
            "password lives in the vault, keyed to this entry, as the app's own entries are."
        ],
        ExitCodes:
        [
            "0   saved - and, unless --no-validate, proven to answer",
            "3   saved, but the server did not answer - fix and re-run",
            "64  usage"
        ],
        Examples:
        [
            ("9lives add-server --name target --address localhost --user svc_restore --password %SQL_PW%",
                "a provisioning script's first line: the freshly built VM's own instance"),
            ("9lives add-server --name SRV01 --address SRV01\\PROD",
                "Windows auth as the account running the CLI, validated")
        ]);

    public static async Task<int> RunAsync(
        CliArguments args, CliServices services, TextWriter output, TextWriter errors)
    {
        var name = args.Get("name");
        var address = args.Get("address");
        if (name == null || address == null)
        {
            errors.WriteLine("add-server needs --name and --address.");
            return ExitCodes.Usage;
        }

        var user = args.Get("user");
        var password = args.Get("password")
                       ?? Environment.GetEnvironmentVariable("NINELIVES_SQL_PASSWORD");
        if (user == null && args.Get("password") != null)
        {
            errors.WriteLine("--password without --user makes no sense - SQL authentication " +
                             "needs both.");
            return ExitCodes.Usage;
        }

        if (user != null && string.IsNullOrEmpty(password))
        {
            errors.WriteLine("--user needs a password: --password, or the " +
                             "NINELIVES_SQL_PASSWORD environment variable (preferred in " +
                             "scripts - it stays out of process listings).");
            return ExitCodes.Usage;
        }

        // Add or update by name: a provisioning script that runs twice must converge.
        var config = services.Store.LoadConfig();
        var existing = config.Servers.FirstOrDefault(
            s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        var server = existing ?? new ServerConnection { Id = ServerConnection.NewId() };

        server.Name = name;
        server.ServerName = address;
        server.AuthMode = user != null ? AuthMode.SqlAuth : AuthMode.WindowsAuth;
        server.Username = user;

        if (existing == null) config.Servers.Add(server);
        services.Store.SaveConfig(config);
        if (user != null)
            services.Store.SaveSqlPassword(server, password!);

        errors.WriteLine($"{(existing == null ? "Added" : "Updated")} server '{name}' -> " +
                         $"{address} ({server.AuthMode.Describe()}).");

        if (args.Has("no-validate"))
        {
            errors.WriteLine("Not validated, as asked.");
            return ExitCodes.Ok;
        }

        try
        {
            var major = await services.Sql.GetProductMajorVersionAsync(server);
            errors.WriteLine(major is { } m
                ? $"Validated: {address} answers as {VersionCompatibility.Describe(m)}."
                : $"Validated: {address} answers.");
            return ExitCodes.Ok;
        }
        catch (Exception ex)
        {
            errors.WriteLine($"VALIDATION FAILED: {ex.Message}");
            errors.WriteLine("The entry is saved - fix the address or credentials and " +
                             "re-run; a script chained on exit codes stops here.");
            return ExitCodes.CouldNotAnswer;
        }
    }
}
