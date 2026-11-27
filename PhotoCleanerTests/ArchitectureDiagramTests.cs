using System.Reflection;
using System.Text.RegularExpressions;
using PhotoCleaner;

namespace PhotoCleanerTests;

public partial class ArchitectureDiagramTests
{
    private const string CommandLogicHeading = "## Command Logic";

    [GeneratedRegex(@"[\[{(]""(\w+)\.(\w+)\(\)")]
    private static partial Regex MethodLabelRegex();

    [GeneratedRegex(@"\bS(\d+)""[\]})]*:::suspect")]
    private static partial Regex SuspectNodeRegex();

    [GeneratedRegex(@"^- \*\*S(\d+)\.\*\*", RegexOptions.Multiline)]
    private static partial Regex SuspectEntryRegex();

    [Fact]
    public void CommandDiagrams_NameExistingMethods()
    {
        List<Match> labels =
        [
            .. GetMermaidBlocks(CommandLogicHeading)
                .SelectMany(block => MethodLabelRegex().Matches(block)),
        ];
        _ = labels.Should().NotBeEmpty();

        List<string> missing =
        [
            .. labels
                .Where(match => !MethodExists(match.Groups[1].Value, match.Groups[2].Value))
                .Select(match => match.Value[2..])
                .Distinct(),
        ];
        _ = missing.Should().BeEmpty(string.Join(", ", missing));
    }

    [Fact]
    public void CommandDiagrams_SuspectsMatchTheList()
    {
        List<int> marked =
        [
            .. GetMermaidBlocks(CommandLogicHeading)
                .SelectMany(block => SuspectNodeRegex().Matches(block))
                .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
                .Distinct()
                .Order(),
        ];
        List<int> listed =
        [
            .. SuspectEntryRegex()
                .Matches(GetSection("### Suspect Branches"))
                .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
                .Order(),
        ];

        _ = listed.Should().Equal(marked);
    }

    private static bool MethodExists(string typeName, string method)
    {
        const BindingFlags flags =
            BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Static
            | BindingFlags.Instance
            | BindingFlags.DeclaredOnly;
        Type? type = typeof(ProcessTask).Assembly.GetType($"PhotoCleaner.{typeName}");
        return type != null
            && type.GetMethods(flags)
                .Any(info => info.Name.Equals(method, StringComparison.Ordinal));
    }

    private static string GetRepoDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (
            directory != null && !File.Exists(Path.Combine(directory.FullName, "PhotoCleaner.slnx"))
        )
        {
            directory = directory.Parent;
        }
        _ = directory.Should().NotBeNull("the tests run from inside the repository");
        return directory!.FullName;
    }

    private static string[] ReadArchitectureLines() =>
        File.ReadAllText(Path.Combine(GetRepoDirectory(), "ARCHITECTURE.md"))
            .ReplaceLineEndings("\n")
            .Split('\n');

    private static string GetSection(string heading) => string.Join('\n', SectionLines(heading));

    // Returns the lines under a heading, up to the next heading of the same or a higher level.
    private static IEnumerable<string> SectionLines(string heading)
    {
        int level = heading.IndexOf(' ', StringComparison.Ordinal);
        bool inSection = false;
        bool inFence = false;
        foreach (string line in ReadArchitectureLines())
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
            }
            else if (!inFence && line.StartsWith('#'))
            {
                int lineLevel = line.IndexOf(' ', StringComparison.Ordinal);
                if (line == heading)
                {
                    inSection = true;
                    continue;
                }
                if (inSection && lineLevel > 0 && lineLevel <= level)
                {
                    yield break;
                }
            }
            if (inSection)
            {
                yield return line;
            }
        }
    }

    private static List<string> GetMermaidBlocks(string heading)
    {
        List<string> blocks = [];
        List<string>? block = null;
        foreach (string line in SectionLines(heading))
        {
            if (line == "```mermaid")
            {
                block = [];
            }
            else if (line.StartsWith("```", StringComparison.Ordinal) && block != null)
            {
                blocks.Add(string.Join('\n', block));
                block = null;
            }
            else
            {
                block?.Add(line);
            }
        }
        _ = blocks.Should().NotBeEmpty($"ARCHITECTURE.md must hold Mermaid under {heading}");
        return blocks;
    }
}
