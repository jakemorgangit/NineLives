using System.Text.Json;

namespace Blackcat.NineLives.Cli;

/// <summary>
/// One serializer configuration for every --json verb, so the shapes stay consistent for the
/// tooling on the other end of the pipe: camelCase names, indented for humans who cat it,
/// and dates in ISO 8601 because that is what System.Text.Json does and what jq expects.
/// </summary>
internal static class JsonOut
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
