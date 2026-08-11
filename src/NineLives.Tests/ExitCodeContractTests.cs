using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Blackcat.NineLives.Cli;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The exit codes a verb documents are the ones it can actually return (#370).
///
/// The exit code IS the contract - a script cannot read prose, so the WHY of a failure has to be
/// in the number. Two bugs got through precisely because nothing checked that contract against
/// the code: "this source has no backups for database X" exited 64, which a pipeline reads as
/// "my invocation is wrong" rather than as the alarm the verb exists to raise; and `list --json`
/// returned 0 on an empty source while the human branch returned 2, so adding --json to a check
/// flipped its verdict.
///
/// Both were single-line fixes. What let them survive was that the help page and the code were
/// two independent descriptions of the same thing, and only one of them ran.
///
/// This checks the direction that matters: every code a verb can return must be documented.
/// The reverse is deliberately allowed - a verb returns its loader's and preflight's codes too,
/// and those are not visible in its own source - so a documented-but-not-locally-returned code is
/// not an error.
/// </summary>
public class ExitCodeContractTests
{
    /// <summary>The name each constant is written under, and the number it stands for.</summary>
    private static readonly Dictionary<string, int> Defined =
        typeof(ExitCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
            .Where(f => f.IsLiteral && f.FieldType == typeof(int))
            .ToDictionary(f => f.Name, f => (int)f.GetRawConstantValue()!);

    public static TheoryData<string> VerbNames()
    {
        var data = new TheoryData<string>();
        foreach (var verb in VerbCatalogue.All) data.Add(verb.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(VerbNames))]
    public void EveryDocumentedCodeIsOneTheCliDefines(string verbName)
    {
        var verb = VerbCatalogue.All.Single(v => v.Name == verbName);
        var documented = Documented(verb).ToList();

        Assert.NotEmpty(documented);

        foreach (var code in documented)
            Assert.True(Defined.ContainsValue(code),
                $"{verbName} documents exit code {code}, which is not one of " +
                $"[{string.Join(", ", Defined.Values.Order())}].");
    }

    /// <summary>Success is always a possible outcome, and always worth stating.</summary>
    [Theory]
    [MemberData(nameof(VerbNames))]
    public void EveryVerbDocumentsSuccess(string verbName)
    {
        var verb = VerbCatalogue.All.Single(v => v.Name == verbName);
        Assert.Contains(ExitCodes.Ok, Documented(verb));
    }

    [Theory]
    [MemberData(nameof(VerbNames))]
    public void NoCodeIsDocumentedTwice(string verbName)
    {
        var verb = VerbCatalogue.All.Single(v => v.Name == verbName);
        var documented = Documented(verb).ToList();

        Assert.Equal(documented.Count, documented.Distinct().Count());
    }

    /// <summary>
    /// The one that would have caught both bugs: read the verb's own source and check every
    /// <c>ExitCodes.X</c> it returns is on its help page.
    ///
    /// Source-reading rather than reflection because a constant is inlined by the compiler -
    /// there is nothing left in the IL to ask. Skipped rather than failed when the source is not
    /// beside the test run, so this cannot break a packaging arrangement it has no opinion about.
    /// </summary>
    [Theory]
    [MemberData(nameof(VerbNames))]
    public void EveryCodeAVerbReturnsIsDocumented(string verbName)
    {
        var source = SourceFor(verbName);
        if (source == null) return;

        var documented = Documented(VerbCatalogue.All.Single(v => v.Name == verbName)).ToHashSet();

        var returned = Regex.Matches(source, @"ExitCodes\.(\w+)")
            .Select(m => m.Groups[1].Value)
            .Where(Defined.ContainsKey)
            .Select(name => Defined[name])
            .Distinct()
            .ToList();

        Assert.NotEmpty(returned);

        foreach (var code in returned)
            Assert.True(documented.Contains(code),
                $"{verbName} can return {code} but does not say so on its help page. " +
                $"A pipeline branching on the documented contract would read it as something " +
                $"else - which is exactly how #370's two exit-code bugs survived.");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    /// <summary>The leading number on each ExitCodes line, e.g. "2   no chain covers ...".</summary>
    private static IEnumerable<int> Documented(VerbSpec verb) =>
        verb.ExitCodes
            .Select(line => Regex.Match(line, @"^\s*(\d+)"))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value));

    private static string? SourceFor(string verbName)
    {
        // "add-container" -> "AddContainerVerb.cs"
        var file = string.Concat(verbName.Split('-')
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..])) + "Verb.cs";

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "NineLives.Cli", "Verbs", file);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }

        return null;
    }
}

/// <summary>
/// The environment variables the CLI reads are the ones its help page names (#370).
///
/// The same shape as the exit-code contract above: the code and the help are two independent
/// descriptions of one thing, and only one of them runs. NINELIVES_S3_REGION was read by
/// EnvironmentCredentials and appeared in no help text at all, so the only way to discover it was
/// to read the source - which is not a discovery route a CI agent's author has.
/// </summary>
public class EnvironmentVariableHelpTests
{
    [Fact]
    public void EveryEnvironmentVariableTheCliReadsIsNamedInTheHelp()
    {
        var source = SourceOfEnvironmentCredentials();
        if (source == null) return;

        var read = Regex.Matches(source, @"""(NINELIVES_[A-Z0-9_]+)""")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Order()
            .ToList();

        Assert.NotEmpty(read);

        var help = new StringWriter();
        HelpWriter.WriteOverview(VerbCatalogue.All, help);
        var text = help.ToString();

        var missing = read.Where(name => !text.Contains(name, StringComparison.Ordinal)).ToList();

        Assert.True(missing.Count == 0,
            $"Read by the CLI but named in no help text: {string.Join(", ", missing)}. " +
            "The only way to discover these would be to read the source.");
    }

    private static string? SourceOfEnvironmentCredentials()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(
                dir.FullName, "src", "NineLives.Cli", "EnvironmentCredentials.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }

        return null;
    }
}
