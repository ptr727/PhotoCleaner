using System.Runtime.CompilerServices;
using Serilog;

namespace PhotoCleaner;

public static class Extensions
{
    extension(ILogger logger)
    {
        public bool LogAndPropagate(
            Exception exception,
            [CallerMemberName] string function = "unknown"
        )
        {
            logger.Error(exception, "{Function}", function);
            return false;
        }

        public bool LogAndHandle(
            Exception exception,
            [CallerMemberName] string function = "unknown"
        )
        {
            logger.Error(exception, "{Function}", function);
            return true;
        }
    }
}
