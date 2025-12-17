using System.CommandLine;

namespace PhotoCleaner;

internal class CommandLine
{
    public static async Task<int> Invoke(string[] args)
    {
        RootCommand rootCommand = CreateRootCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        return parseResult.Invoke();
    }

    internal static RootCommand CreateRootCommand()
    {
        Option<DirectoryInfo> pathOption = new("--path", "-p")
        {
            Description = "The directory path to process.",
            Required = true,
        };
        pathOption.AcceptExistingOnly();

        Option<bool> dryRunOption = new("--dryrun", "-d")
        {
            Description = "Perform a dry run without making changes.",
        };

        RootCommand rootCommand = new(
            "PhotoCleaner - Pre-process media files for photo management systems."
        )
        {
            pathOption,
            dryRunOption,
        };
        rootCommand.SetAction(async parseResult =>
        {
            Program program = new();
            return await program.Execute(
                parseResult.GetValue(pathOption)!.FullName,
                parseResult.GetValue(dryRunOption)
            );
        });

        return rootCommand;
    }
}
