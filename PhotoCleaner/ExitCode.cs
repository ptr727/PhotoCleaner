namespace PhotoCleaner;

// Callers script against these values, so each code's meaning is part of the contract.
internal static class ExitCode
{
    internal const int Success = 0;

    // The command could not complete, so it reports nothing about the files.
    internal const int Error = 1;

    // The command completed, but at least one file failed.
    internal const int Failed = 2;
}
