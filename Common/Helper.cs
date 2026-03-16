using DotNetty.Common.Internal.Logging;
using Microsoft.Extensions.Logging;

namespace Common;

public static class Helper
{
    public static string ProcessDirectory
    {
        get
        {
#if NETSTANDARD2_0 || NETCOREAPP3_1_OR_GREATER || NET5_0_OR_GREATER
            return AppContext.BaseDirectory;
#else
            return AppDomain.CurrentDomain.BaseDirectory;
#endif
        }
    }

    public static void SetConsoleLogger() => InternalLoggerFactory.DefaultFactory = LoggerFactory.Create(builder => builder.AddConsole());
}
