namespace PhotoCleaner;

internal static class CommandRunner
{
    internal static async Task<int> RunAsync(string commandName, Func<Task> work)
    {
        Log.Information("{CommandName} started", commandName);
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log.Information("{CommandName} cancelled", commandName);
            return 1;
        }
        catch (Exception ex) when (Log.Logger.LogAndHandle(ex))
        {
            return 1;
        }
        Log.Information("{CommandName} complete", commandName);
        return 0;
    }
}
