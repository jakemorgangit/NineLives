using System.Text;
using Blackcat.NineLives.Cli.Verbs;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.Cli;

/// <summary>
/// Thin on purpose: compose the real services, pick the verb, hand over. Everything worth
/// testing lives in the verbs and the parser, which take their services and writers as
/// arguments - this file is the only one that touches the real console or the real vault.
/// </summary>
internal static class Program
{
    private static readonly VerbSpec[] Verbs =
    [
        ListVerb.Spec, PointsVerb.Spec, ScriptVerb.Spec, ValidateVerb.Spec, ExposureVerb.Spec,
        RestoreVerb.Spec, RehearseVerb.Spec
    ];

    private static async Task<int> Main(string[] rawArgs)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (rawArgs.Length == 0
            || rawArgs[0] is "--help" or "-h" or "help" or "-?" or "/?")
        {
            WriteHelp(Console.Out);
            return rawArgs.Length == 0 ? ExitCodes.Usage : ExitCodes.Ok;
        }

        if (rawArgs[0] is "--version")
        {
            Console.Out.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?");
            return ExitCodes.Ok;
        }

        var spec = Verbs.FirstOrDefault(
            v => string.Equals(v.Name, rawArgs[0], StringComparison.OrdinalIgnoreCase));
        if (spec == null)
        {
            Console.Error.WriteLine($"Unknown verb '{rawArgs[0]}'.");
            WriteHelp(Console.Error);
            return ExitCodes.Usage;
        }

        var args = CliArguments.Parse(rawArgs.Skip(1).ToArray(), spec);
        if (!args.Ok)
        {
            foreach (var error in args.Errors)
                Console.Error.WriteLine(error);
            Console.Error.WriteLine($"Usage: {spec.Usage}");
            return ExitCodes.Usage;
        }

        // The same store, the same services, the same configuration the desktop app maintains:
        // configure once there, script against it here. History and webhooks too - an executed
        // run lands in the app's History screen and the same Teams channel hears about it.
        var store = new CredentialStore();
        var services = new CliServices(
            store, new SqlServerService(store), new BlobStorageService(store),
            new RestoreHistoryStore(), new WebhookRunNotifier(store, new OperationLog()));

        try
        {
            return spec.Name switch
            {
                "list" => await ListVerb.RunAsync(args, services, Console.Out, Console.Error),
                "points" => await PointsVerb.RunAsync(args, services, Console.Out, Console.Error),
                "script" => await ScriptVerb.RunAsync(args, services, Console.Out, Console.Error),
                "validate" => await ValidateVerb.RunAsync(args, services, Console.Out, Console.Error),
                "exposure" => await ExposureVerb.RunAsync(args, services, Console.Out, Console.Error),
                "restore" => await RestoreVerb.RunAsync(args, services, Console.Out, Console.Error),
                "rehearse" => await RehearseVerb.RunAsync(args, services, Console.Out, Console.Error),
                _ => ExitCodes.Usage
            };
        }
        catch (Exception ex)
        {
            // A verb that cannot answer must say so in the exit code - "I don't know" exiting 0
            // is how monitoring sleeps through the outage it exists for.
            Console.Error.WriteLine($"Could not answer: {ex.Message}");
            return ExitCodes.CouldNotAnswer;
        }
    }

    private static void WriteHelp(TextWriter output)
    {
        output.WriteLine("Nine Lives CLI - the restore engine from a terminal.");
        output.WriteLine("Reads the configuration the Nine Lives app maintains: its containers,");
        output.WriteLine("its servers, its credentials. Nothing executes without --execute, and");
        output.WriteLine("overwriting a database is said with --with-replace, deliberately.");
        output.WriteLine();
        output.WriteLine("Verbs:");
        foreach (var verb in Verbs)
            output.WriteLine($"  {verb.Name,-10} {verb.Summary}");
        output.WriteLine();
        output.WriteLine("Usage:");
        foreach (var verb in Verbs)
            output.WriteLine($"  {verb.Usage}");
        output.WriteLine();
        output.WriteLine("Exit codes: 0 fine; 1 warnings; 2 the thing checked is broken or");
        output.WriteLine("unreachable-by-chain; 3 the question could not be answered; 64 usage.");
        output.WriteLine();
        output.WriteLine("Examples:");
        output.WriteLine("  9lives exposure                          the estate, judged, in one exit code");
        output.WriteLine("  9lives list --container backups");
        output.WriteLine("  9lives points --container backups --database Sales");
        output.WriteLine("  9lives script --container backups --database Sales --at \"2026-08-02 19:00\" --out restore.sql");
        output.WriteLine("  9lives validate --server SRV01 --json");
        output.WriteLine("  9lives rehearse --container backups --database Sales --target SRV02 --execute");
        output.WriteLine("  9lives restore --container backups --database Sales --target SRV02 --with-replace --execute");
    }
}
