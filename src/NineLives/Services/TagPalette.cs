namespace Blackcat.NineLives.Services;

/// <summary>
/// Assigns a colour to a tag name, using GitHub's own label palette.
///
/// Two rules, in order:
///
/// 1. Well-known environment names get a SEMANTIC colour. This is the point of the feature:
///    Nine Lives restores databases with WITH REPLACE on by default, and the worst mistake
///    available is restoring into the wrong environment. A red "prod" pill is a pre-attentive
///    warning; a pastel one hashed from the letters p-r-o-d is decoration. Hostnames are a poor
///    signal on their own - blackcatsvr01 and blackcatsvr02 differ by one character.
///
/// 2. Everything else hashes deterministically into the palette, so a given tag is always the
///    same colour on every machine and across restarts, with no colour picker to manage.
///
/// Colours are GitHub's label swatches. Rendering follows GitHub's DARK theme treatment rather
/// than its light one - a solid #B60205 pill is punishing against this app's background, whereas
/// a tinted fill with a lightened foreground reads cleanly and is what GitHub itself shows in
/// dark mode.
/// </summary>
public static class TagPalette
{
    /// <summary>GitHub's label colour swatches (the saturated row).</summary>
    private static readonly string[] Swatches =
    [
        "#B60205", // red
        "#D93F0B", // orange
        "#FBCA04", // yellow
        "#0E8A16", // green
        "#006B75", // teal
        "#1D76DB", // blue
        "#0052CC", // dark blue
        "#5319E7"  // purple
    ];

    /// <summary>
    /// Environment names that carry meaning. Matched case-insensitively on the whole tag, so a
    /// tag like "prod-eu" falls through to hashing rather than being wrongly reassured about.
    /// </summary>
    private static readonly Dictionary<string, string> Semantic = new(StringComparer.OrdinalIgnoreCase)
    {
        ["prod"] = "#B60205",
        ["production"] = "#B60205",
        ["live"] = "#B60205",

        ["dr"] = "#D93F0B",
        ["disaster-recovery"] = "#D93F0B",

        ["staging"] = "#FBCA04",
        ["stage"] = "#FBCA04",
        ["uat"] = "#FBCA04",
        ["preprod"] = "#FBCA04",

        ["test"] = "#0E8A16",
        ["testing"] = "#0E8A16",
        ["qa"] = "#0E8A16",

        ["dev"] = "#1D76DB",
        ["development"] = "#1D76DB",
        ["sandbox"] = "#1D76DB"
    };

    /// <summary>Base colour for a tag name, as #RRGGBB.</summary>
    public static string ColorFor(string? tag)
    {
        var name = (tag ?? string.Empty).Trim();
        if (name.Length == 0) return Swatches[0];

        if (Semantic.TryGetValue(name, out var semantic)) return semantic;

        return Swatches[StableIndex(name, Swatches.Length)];
    }

    /// <summary>True when this tag denotes a production-like environment.</summary>
    public static bool IsProductionLike(string? tag)
    {
        var name = (tag ?? string.Empty).Trim();
        return Semantic.TryGetValue(name, out var c) && c == "#B60205";
    }

    /// <summary>
    /// FNV-1a over the lowercased name. Deliberately not string.GetHashCode(), which is
    /// randomised per process in .NET Core - a tag would change colour every launch.
    /// </summary>
    private static int StableIndex(string name, int buckets)
    {
        unchecked
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;

            var hash = offset;
            foreach (var c in name.ToLowerInvariant())
            {
                hash ^= c;
                hash *= prime;
            }
            return (int)(hash % (uint)buckets);
        }
    }

    /// <summary>
    /// Splits and normalises a user-entered tag list. Trims, drops blanks, removes duplicates
    /// case-insensitively while keeping the first spelling the user chose, and preserves order.
    /// </summary>
    public static List<string> ParseTags(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var part in input.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var tag = part.Trim();
            if (tag.Length == 0) continue;
            if (tag.Length > 32) tag = tag[..32];
            if (seen.Add(tag)) result.Add(tag);
        }

        return result;
    }

    public static string FormatTags(IEnumerable<string>? tags)
        => tags == null ? string.Empty : string.Join(", ", tags);
}
