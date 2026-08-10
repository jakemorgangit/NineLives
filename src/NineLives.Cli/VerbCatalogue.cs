using Blackcat.NineLives.Cli.Verbs;

namespace Blackcat.NineLives.Cli;

/// <summary>
/// Every verb the exe knows, in the order help lists them. Its own class rather than a private
/// table in Program so the tests can hold the WHOLE catalogue to its promises - every declared
/// option documented, every verb carrying notes, exit codes and examples.
/// </summary>
internal static class VerbCatalogue
{
    public static readonly VerbSpec[] All =
    [
        ListVerb.Spec, PointsVerb.Spec, ScriptVerb.Spec, ValidateVerb.Spec, ExposureVerb.Spec,
        RestoreVerb.Spec, RehearseVerb.Spec
    ];

    public static VerbSpec? Find(string name) => All.FirstOrDefault(
        v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
}
