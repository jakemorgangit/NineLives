using System.IO;
using Blackcat.NineLives.Cli;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The CLI's documentation lives inside the exe, on each verb's spec - and these tests are
/// what stops it drifting from the parser it describes. A flag the parser takes but the help
/// omits is documentation lying by silence; a flag the help describes but the parser refuses
/// is worse. Neither can merge while these hold.
/// </summary>
public class CliHelpTests
{
    public static TheoryData<string> VerbNames()
    {
        var data = new TheoryData<string>();
        foreach (var verb in VerbCatalogue.All) data.Add(verb.Name);
        return data;
    }

    private static VerbSpec Spec(string name) => VerbCatalogue.Find(name)!;

    /// <summary>Every option the parser accepts appears in the help, spelled as typed.</summary>
    [Theory]
    [MemberData(nameof(VerbNames))]
    public void EveryDeclaredOptionIsDocumented(string name)
    {
        var spec = Spec(name);
        var documented = spec.Options.Select(o => o.Term).ToList();

        foreach (var option in spec.Valued.Concat(spec.Switches))
            Assert.Contains(documented, term => term == $"--{option}"
                                                || term.StartsWith($"--{option} "));
    }

    /// <summary>And nothing else: help must not describe flags the parser would refuse.</summary>
    [Theory]
    [MemberData(nameof(VerbNames))]
    public void TheHelpDescribesNoFlagTheParserRefuses(string name)
    {
        var spec = Spec(name);
        var accepted = spec.Valued.Concat(spec.Switches).ToList();

        foreach (var (term, _) in spec.Options)
        {
            var flag = term[2..].Split(' ')[0];
            Assert.Contains(flag, accepted);
        }
    }

    /// <summary>A verb page without behaviour, exit codes and examples is a stub, not a reference.</summary>
    [Theory]
    [MemberData(nameof(VerbNames))]
    public void EveryVerbCarriesNotesExitCodesAndExamples(string name)
    {
        var spec = Spec(name);

        Assert.NotEmpty(spec.Notes);
        Assert.NotEmpty(spec.ExitCodes);
        Assert.NotEmpty(spec.Examples);
    }

    [Fact]
    public void TheOverviewListsEveryVerbAndTheHelpPointer()
    {
        var output = new StringWriter();
        HelpWriter.WriteOverview(VerbCatalogue.All, output);
        var text = output.ToString();

        foreach (var verb in VerbCatalogue.All)
            Assert.Contains(verb.Name, text);
        Assert.Contains("9lives help VERB", text);
    }

    /// <summary>
    /// The page for the destructive verb carries the safety story: consent, evidence, and the
    /// two error numbers the preflights exist to pre-empt.
    /// </summary>
    [Fact]
    public void TheRestorePageCarriesTheConsentAndEvidenceStory()
    {
        var output = new StringWriter();
        HelpWriter.WriteVerb(Spec("restore"), output);
        var text = output.ToString();

        Assert.Contains("--with-replace", text);
        Assert.Contains("--force", text);
        Assert.Contains("3169", text);
        Assert.Contains("33111", text);
        Assert.Contains("consent", text);
    }

    /// <summary>
    /// Prose must arrive wrapped - a terminal reflows nothing. Usage synopses and example
    /// commands are exempt on purpose: wrapping a command breaks copy-paste, which is the one
    /// thing an example command exists for. So the pin runs on a synthetic verb whose prose
    /// is guaranteed longer than a line, and checks the sections that ARE prose.
    /// </summary>
    [Fact]
    public void NotesAndOptionHelpWrapInsideEightyColumns()
    {
        var longText = string.Join(" ", Enumerable.Repeat("word", 60));
        var spec = new VerbSpec(
            "wrapcheck", "s", "usage", ["opt"], [],
            Options: [("--opt VALUE", longText)],
            Notes: [longText],
            ExitCodes: ["0   fine"],
            Examples: [("9lives wrapcheck", "example")]);

        var output = new StringWriter();
        HelpWriter.WriteVerb(spec, output);
        var lines = output.ToString().Split('\n').Select(l => l.TrimEnd()).ToList();

        var prose = lines.Where(l => l.Contains("word")).ToList();
        Assert.True(prose.Count > 2, "the long text should have wrapped across lines");
        foreach (var line in prose)
            Assert.True(line.Length <= 80, $"prose line exceeds 80 columns: '{line}'");
    }
}
