using System.CommandLine;
using System.Runtime.InteropServices;

namespace PhotoCleaner;

internal sealed class Program
{
    internal static async Task<int> Main(string[] args)
    {
        using CancellationTokenSource terminationSource = new();
        using PosixSignalRegistration sigIntRegistration = PosixSignalRegistration.Create(
            PosixSignal.SIGINT,
            context => OnTerminationSignal(context, terminationSource)
        );
        using PosixSignalRegistration sigTermRegistration = PosixSignalRegistration.Create(
            PosixSignal.SIGTERM,
            context => OnTerminationSignal(context, terminationSource)
        );
        try
        {
            // Parse commandline
            CommandLine commandLine = new(args);
            ConfigureInvocation(commandLine.Result);

            // Bypass startup for errors or help and version commands
            if (CommandLine.BypassStartup(commandLine.Result))
            {
                return await commandLine
                    .Result.InvokeAsync(cancellationToken: terminationSource.Token)
                    .ConfigureAwait(false);
            }

            // Create logger
            Log.Logger = LoggerFactory.Create(
                commandLine.CreateOptions(commandLine.Result).LogOptions
            );
            ptr727.Utilities.LogOptions.SetFactory(LoggerFactory.CreateLoggerFactory());

            // Invoke command
            Log.Logger.LogOverrideContext().Information("Starting: {Args}", args);
            return await commandLine
                .Result.InvokeAsync(cancellationToken: terminationSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 1;
        }
        catch (Exception ex) when (Log.Logger.LogAndHandle(ex))
        {
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    internal static void ConfigureInvocation(ParseResult parseResult)
    {
        parseResult.InvocationConfiguration.EnableDefaultExceptionHandler = false;
        parseResult.InvocationConfiguration.ProcessTerminationTimeout = null;
    }

    internal static void OnTerminationSignal(
        PosixSignalContext context,
        CancellationTokenSource terminationSource
    )
    {
        if (terminationSource.IsCancellationRequested)
        {
            return;
        }
        context.Cancel = true;
        Log.Warning("{Signal} received, cancelling", context.Signal.ToString());
        terminationSource.Cancel();
    }
}
