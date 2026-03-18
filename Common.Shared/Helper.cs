using DotNetty.Common.Internal.Logging;
using Microsoft.Extensions.Logging;

namespace Common;

public static class Helper
{
    public static void SetConsoleLogger() => InternalLoggerFactory.DefaultFactory = LoggerFactory.Create(builder => builder.AddConsole());
}
