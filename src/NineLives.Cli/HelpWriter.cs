namespace Blackcat.NineLives.Cli;

/// <summary>
/// Renders the documentation the specs carry (#63): the overview for bare --help, and the full
/// per-verb page for "9lives help verb" or "9lives verb --help". The reference lives INSIDE
/// the exe on purpose - a markdown file beside it would drift, and a test holds these pages to
/// the parser's own option lists so the help cannot describe flags that do not exist or omit
/// ones that do.
/// </summary>
internal static class HelpWriter
{
    private const int Width = 78;

    public static void WriteOverview(IReadOnlyList<VerbSpec> verbs, TextWriter output)
    {
        output.WriteLine("Nine Lives CLI - the restore engine from a terminal.");
        output.WriteLine();
        Wrapped(output,
            "Reads the configuration the Nine Lives app maintains - its containers, its " +
            "servers, its Credential Manager entries - from %LOCALAPPDATA%\\NineLives. " +
            "Configure once in the app; script against it here, as the user who configured it.");
        output.WriteLine();
        output.WriteLine("Verbs (9lives help VERB for the full story on each):");
        foreach (var verb in verbs)
            output.WriteLine($"  {verb.Name,-10} {verb.Summary}");
        output.WriteLine();
        output.WriteLine("Usage:");
        foreach (var verb in verbs)
            output.WriteLine($"  {verb.Usage}");
        output.WriteLine();
        output.WriteLine("Exit codes: 0 fine; 1 warnings; 2 the thing checked is broken or");
        output.WriteLine("refused; 3 the question could not be answered; 64 usage.");
        output.WriteLine();
        Wrapped(output,
            "Ephemeral mode: pass --ephemeral on any verb to resolve names against " +
            "zero-persistence definitions from environment variables, consulted before the " +
            "profile's config and written nowhere - for locked-down service accounts and CI " +
            "agents where nothing may outlive the job. NINELIVES_SERVER (address, resolvable " +
            "as the name NINELIVES_SERVER_NAME or 'env'), NINELIVES_SQL_USER and " +
            "NINELIVES_SQL_PASSWORD (SQL auth; omit both for Windows auth), " +
            "NINELIVES_CONTAINER_URL (resolvable as NINELIVES_CONTAINER_NAME or 'env') and " +
            "NINELIVES_SAS. Secrets stay in process memory; history receipts still write and " +
            "webhooks still fire - they are the point.");
        output.WriteLine();
        Wrapped(output,
            "Two disciplines every verb keeps: data goes to stdout and everything human goes " +
            "to stderr, so output pipes and redirects cleanly; and timestamps parse exact " +
            "invariant formats only (yyyy-MM-dd HH:mm:ss, yyyy-MM-dd HH:mm, yyyy-MM-dd) - a " +
            "string that means different moments on two machines is refused.");
        output.WriteLine();
        output.WriteLine("Examples:");
        output.WriteLine("  9lives exposure                          the estate, judged, in one exit code");
        output.WriteLine("  9lives list --container backups");
        output.WriteLine("  9lives points --container backups --database Sales");
        output.WriteLine("  9lives script --container backups --database Sales --at \"2026-08-02 19:00\" --out restore.sql");
        output.WriteLine("  9lives rehearse --container backups --database Sales --target SRV02 --execute");
        output.WriteLine("  9lives restore --container backups --database Sales --target SRV02 --with-replace --execute");
        output.WriteLine();
        output.WriteLine("Provisioning a fresh machine to a point in time - three lines, each");
        output.WriteLine("validated, chained on exit codes; the only variable is the moment:");
        output.WriteLine("  9lives add-server --name target --address localhost --user svc_restore --password %SQL_PW%");
        output.WriteLine("  9lives add-container --name backups --url https://acct.blob.core.windows.net/sqlbackups --sas %SAS%");
        output.WriteLine("  9lives restore --container backups --database Sales --target target --at \"%POINT_IN_TIME%\" --execute");
    }

    public static void WriteVerb(VerbSpec verb, TextWriter output)
    {
        output.WriteLine($"9lives {verb.Name} - {verb.Summary}");
        output.WriteLine();
        output.WriteLine("Usage:");
        output.WriteLine($"  {verb.Usage}");

        if (verb.Options.Length > 0)
        {
            output.WriteLine();
            output.WriteLine("Options:");
            var width = verb.Options.Max(o => o.Term.Length);
            foreach (var (term, help) in verb.Options)
                WrappedIndented(output, $"  {term.PadRight(width + 2)}", help);
        }

        if (verb.Notes.Length > 0)
        {
            output.WriteLine();
            output.WriteLine("Behaviour:");
            foreach (var note in verb.Notes)
            {
                Wrapped(output, note, indent: "  ");
                output.WriteLine();
            }
        }

        if (verb.ExitCodes.Length > 0)
        {
            output.WriteLine("Exit codes:");
            foreach (var code in verb.ExitCodes)
                output.WriteLine($"  {code}");
        }

        if (verb.Examples.Length > 0)
        {
            output.WriteLine();
            output.WriteLine("Examples:");
            foreach (var (command, meaning) in verb.Examples)
            {
                output.WriteLine($"  {command}");
                output.WriteLine($"      {meaning}");
            }
        }
    }

    /// <summary>Plain greedy word-wrap. Terminals reflow nothing; this text has to arrive wrapped.</summary>
    private static void Wrapped(TextWriter output, string text, string indent = "")
    {
        var line = indent;
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > indent.Length && line.Length + word.Length + 1 > Width)
            {
                output.WriteLine(line);
                line = indent;
            }

            line += line.Length > indent.Length ? " " + word : word;
        }

        if (line.Length > indent.Length) output.WriteLine(line);
    }

    /// <summary>First line carries the option term; continuations align under the help text.</summary>
    private static void WrappedIndented(TextWriter output, string head, string help)
    {
        var indent = new string(' ', head.Length);
        var line = head;
        foreach (var word in help.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > head.Length && line.Length + word.Length + 1 > Width)
            {
                output.WriteLine(line);
                line = indent;
            }

            line += line == head || line == indent ? word : " " + word;
        }

        if (line != indent) output.WriteLine(line);
    }
}
