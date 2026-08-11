using System.Text.Json;

namespace Blackcat.NineLives.Cli;

/// <summary>
/// The execution verbs' --json ending (#303): one machine-shaped object on stdout - outcome,
/// duration, chain, point reached, refusals, history id - where the prose ending goes to
/// stderr as always. The read verbs were already composable; this makes the execution verbs
/// composable too, and a rehearsal's proof becomes a JSON artefact a pipeline can archive.
/// </summary>
internal static class CliRunResult
{
    public static void Write(TextWriter output, object result) =>
        output.WriteLine(JsonSerializer.Serialize(result, JsonOut.Options));
}
