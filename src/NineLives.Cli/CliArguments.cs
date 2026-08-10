namespace Blackcat.NineLives.Cli;

/// <summary>
/// What a verb accepts: which options take a value, which are bare switches. The parser
/// validates against this rather than guessing, so "--json 2026-08-01" is an error about an
/// unexpected value instead of a silently swallowed timestamp.
/// </summary>
/// <param name="Name">The verb itself, e.g. "points".</param>
/// <param name="Summary">One line for the help screen.</param>
/// <param name="Usage">The synopsis line shown on usage errors and in help.</param>
/// <param name="Valued">Options that take a value: "container", "at", ...</param>
/// <param name="Switches">Options that stand alone: "json", ...</param>
internal sealed record VerbSpec(
    string Name,
    string Summary,
    string Usage,
    string[] Valued,
    string[] Switches);

/// <summary>
/// One parsed command line, validated against a <see cref="VerbSpec"/>. Pure and small on
/// purpose: no dependency on a parsing package, and every rule it enforces is visible here.
/// </summary>
internal sealed class CliArguments
{
    private readonly Dictionary<string, string> _options;
    private readonly HashSet<string> _switches;

    public IReadOnlyList<string> Errors { get; }

    private CliArguments(
        Dictionary<string, string> options, HashSet<string> switches, List<string> errors)
    {
        _options = options;
        _switches = switches;
        Errors = errors;
    }

    public bool Ok => Errors.Count == 0;

    public string? Get(string name) => _options.GetValueOrDefault(name);

    public bool Has(string switchName) => _switches.Contains(switchName);

    /// <summary>
    /// Everything after the verb, checked against the spec. Errors accumulate rather than
    /// stopping at the first, so one bad invocation is one round of fixes, not several.
    /// </summary>
    public static CliArguments Parse(IReadOnlyList<string> args, VerbSpec spec)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var switches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];

            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                errors.Add($"Unexpected value '{token}'. Options are named: --option value.");
                continue;
            }

            var name = token[2..];

            if (spec.Switches.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                switches.Add(name);
                continue;
            }

            if (spec.Valued.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    errors.Add($"--{name} needs a value.");
                    continue;
                }

                if (!options.TryAdd(name.ToLowerInvariant(), args[i + 1]))
                    errors.Add($"--{name} was given twice.");
                i++;
                continue;
            }

            errors.Add($"'{spec.Name}' does not take --{name}.");
        }

        return new CliArguments(options, switches, errors);
    }

    /// <summary>
    /// The timestamp formats accepted, strictly. A pipeline wants the same string to mean the
    /// same moment on every machine, so this parses exact invariant formats rather than
    /// whatever the host's culture feels like - "01/02/2026" swapping day and month between
    /// two servers is precisely the ambiguity a restore tool cannot afford.
    /// </summary>
    public static DateTime? ParseTime(string value)
    {
        string[] formats = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd"];
        return DateTime.TryParseExact(
            value, formats, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }
}
