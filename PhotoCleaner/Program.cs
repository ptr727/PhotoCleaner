using System.CommandLine;
using System.Runtime.InteropServices;

namespace PhotoCleaner;

internal sealed partial class Program
{
    internal static async Task<int> Main(string[] args)
    {
        PosixSignal? receivedSignal = null;
        int exitCode;
        using (CancellationTokenSource terminationSource = new())
        {
            void OnSignal(PosixSignalContext context) =>
                OnTerminationSignal(context, terminationSource, signal => receivedSignal = signal);
            using PosixSignalRegistration sigIntRegistration = PosixSignalRegistration.Create(
                PosixSignal.SIGINT,
                OnSignal
            );
            using PosixSignalRegistration sigTermRegistration = PosixSignalRegistration.Create(
                PosixSignal.SIGTERM,
                OnSignal
            );
            exitCode = await RunAsync(args, terminationSource.Token).ConfigureAwait(false);
        }

        // The log is flushed and the handlers are gone, so the signal now takes its default action.
        return receivedSignal is PosixSignal signal ? ExitBySignal(signal) : exitCode;
    }

    private static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            // Parse commandline
            CommandLine commandLine = new(args);
            ConfigureInvocation(commandLine.Result);

            // Bypass startup for errors or help and version commands
            if (CommandLine.BypassStartup(commandLine.Result))
            {
                return await commandLine
                    .Result.InvokeAsync(cancellationToken: cancellationToken)
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
                .Result.InvokeAsync(cancellationToken: cancellationToken)
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
        CancellationTokenSource terminationSource,
        Action<PosixSignal> recordSignal
    )
    {
        if (terminationSource.IsCancellationRequested)
        {
            return;
        }
        context.Cancel = true;
        // Recorded before the cancel, since the cancel can run the rest of Program.Main on this thread.
        recordSignal(context.Signal);
        Log.Warning("{Signal} received, cancelling", context.Signal.ToString());
        terminationSource.Cancel();
    }

    internal static (int Number, int ExitCode) SignalExit(PosixSignal signal) =>
        signal switch
        {
            PosixSignal.SIGINT => (2, ExitCode.Interrupted),
            PosixSignal.SIGTERM => (15, ExitCode.Terminated),
            _ => throw new ArgumentOutOfRangeException(nameof(signal), signal, null),
        };

    // Dying by the signal, rather than exiting with a code, is what stops a calling shell loop.
    // As PID 1 the kernel drops the default action, and Windows has no kill, so both return the code.
    private static int ExitBySignal(PosixSignal signal)
    {
        (int number, int exitCode) = SignalExit(signal);
        if (!OperatingSystem.IsWindows())
        {
            _ = SetSignalHandler(number, DefaultSignalHandler);
            _ = Kill(Environment.ProcessId, number);
        }
        return exitCode;
    }

    private const nint DefaultSignalHandler = 0;

    [LibraryImport("libc", EntryPoint = "signal")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint SetSignalHandler(int signalNumber, nint handler);

    [LibraryImport("libc", EntryPoint = "kill")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int Kill(int processId, int signalNumber);
}
