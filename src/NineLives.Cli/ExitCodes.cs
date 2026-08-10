namespace Blackcat.NineLives.Cli;

/// <summary>
/// The contract a pipeline branches on (#63's third design question). A script cannot read
/// prose, so the WHY of a failure has to be in the number - "the chain is broken" and "the
/// server would not answer" call for different next steps, and collapsing them into 1 forces
/// every caller to parse output instead.
/// </summary>
internal static class ExitCodes
{
    /// <summary>Everything asked for was done, and nothing found was wrong.</summary>
    public const int Ok = 0;

    /// <summary>The check ran and found warnings - exposure rows turning amber, say.</summary>
    public const int Warnings = 1;

    /// <summary>
    /// The check ran and the answer is bad: alarms on the exposure sweep, a broken chain, a
    /// moment no chain covers. The verb did its job; the estate failed it.
    /// </summary>
    public const int Failed = 2;

    /// <summary>
    /// The question could not be answered at all - a server unreachable mid-verb, a container
    /// listing refused. Distinct from <see cref="Failed"/> because "I don't know" must never
    /// read as "all clear", and retrying is a reasonable response where it never is for a
    /// genuinely broken chain.
    /// </summary>
    public const int CouldNotAnswer = 3;

    /// <summary>Bad invocation: unknown verb, missing option, malformed time. BSD's EX_USAGE.</summary>
    public const int Usage = 64;
}
