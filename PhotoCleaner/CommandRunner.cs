namespace PhotoCleaner;

internal static class CommandRunner
{
    internal static async Task<int> RunAsync(string commandName, Func<Task<int>> work)
    {
        Log.Information("{CommandName} started", commandName);
        int exitCode;
        try
        {
            exitCode = await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log.Information("{CommandName} cancelled", commandName);
            return ExitCode.Error;
        }
        catch (Exception ex) when (Log.Logger.LogAndHandle(ex))
        {
            return ExitCode.Error;
        }

        // Each outcome is named rather than reading everything that is not Failed as success.
        // Nothing returns Error from the work itself today, and the catch blocks return it directly.
        // This branch is what keeps a future one from reporting completion.
        if (exitCode == ExitCode.Success)
        {
            Log.Information("{CommandName} complete", commandName);
        }
        else if (exitCode == ExitCode.Failed)
        {
            Log.Warning("{CommandName} complete with failures", commandName);
        }
        else
        {
            Log.Error(
                "{CommandName} did not complete, exit code {ExitCode}",
                commandName,
                exitCode
            );
        }
        return exitCode;
    }
}
