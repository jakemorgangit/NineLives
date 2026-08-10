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
    private static async Task<int> Main(string[] rawArgs)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (rawArgs.Length == 0
            || rawArgs[0] is "--help" or "-h" or "-?" or "/?")
        {
            HelpWriter.WriteOverview(VerbCatalogue.All, Console.Out);
            return rawArgs.Length == 0 ? ExitCodes.Usage : ExitCodes.Ok;
        }

        // "9lives help" and "9lives help restore" - the reference lives in the exe, so the
        // exe is where it is read.
        if (string.Equals(rawArgs[0], "help", StringComparison.OrdinalIgnoreCase))
        {
            if (rawArgs.Length == 1)
            {
                HelpWriter.WriteOverview(VerbCatalogue.All, Console.Out);
                return ExitCodes.Ok;
            }

            var asked = VerbCatalogue.Find(rawArgs[1]);
            if (asked == null)
            {
                Console.Error.WriteLine($"No verb called '{rawArgs[1]}'.");
                HelpWriter.WriteOverview(VerbCatalogue.All, Console.Error);
                return ExitCodes.Usage;
            }

            HelpWriter.WriteVerb(asked, Console.Out);
            return ExitCodes.Ok;
        }

        if (rawArgs[0] is "--version")
        {
            Console.Out.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?");
            return ExitCodes.Ok;
        }

        var spec = VerbCatalogue.Find(rawArgs[0]);
        if (spec == null)
        {
            Console.Error.WriteLine($"Unknown verb '{rawArgs[0]}'.");
            HelpWriter.WriteOverview(VerbCatalogue.All, Console.Error);
            return ExitCodes.Usage;
        }

        // "--help" anywhere after the verb wins over parsing: someone asking how to hold the
        // tool must never be told off for holding it wrongly mid-question.
        if (rawArgs.Skip(1).Any(a => a is "--help" or "-h" or "-?"))
        {
            HelpWriter.WriteVerb(spec, Console.Out);
            return ExitCodes.Ok;
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
}
