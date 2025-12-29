using System.CommandLine;

namespace PhotoCleaner;

internal static class CommandLine
{
    public static async Task<int> Invoke(string[] args)
    {
        RootCommand rootCommand = CreateRootCommand();
        ParseResult parseResult = rootCommand.Parse(args);
        return await parseResult.InvokeAsync();
    }

    internal static RootCommand CreateRootCommand()
    {
        Option<List<DirectoryInfo>> pathOption = new("--path", "-p")
        {
            Description = "The directory path to process.",
            Required = true,
        };
        _ = pathOption.AcceptExistingOnly();

        Option<bool> dryRunOption = new("--dryrun", "-d")
        {
            Description = "Perform a dry run without making changes (default: false).",
        };

        Option<int> threadsOption = new("--threads", "-t")
        {
            Description = "Number of parallel threads (default: Max(ProcessorCount, 4)).",
            DefaultValueFactory = _ => Math.Max(Environment.ProcessorCount, 4),
        };
        threadsOption.Validators.Add(result =>
        {
            int value = result.GetValue(threadsOption);
            if (value <= 0)
            {
                result.AddError("Thread count must be greater than 0.");
            }
            if (value > Environment.ProcessorCount)
            {
                result.AddError(
                    $"Thread count must be less than or equal to {Environment.ProcessorCount}."
                );
            }
        });

        RootCommand rootCommand = new(
            "PhotoCleaner - Pre-process media files for photo management systems."
        )
        {
            pathOption,
            dryRunOption,
            threadsOption,
        };
        rootCommand.SetAction(parseResult =>
        {
            Program program = new(
                parseResult.GetValue(threadsOption),
                parseResult.GetValue(dryRunOption)
            );
            return program.Execute(parseResult.GetValue(pathOption) ?? []);
        });

        return rootCommand;
    }
}
