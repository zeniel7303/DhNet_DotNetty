using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace Common.Logging;

public static class GameLogger
{
    private static readonly Channel<LogEntry> _pipe = Channel.CreateUnbounded<LogEntry>(
        new UnboundedChannelOptions { SingleReader = true });

    private static readonly string LogDir;

    static GameLogger()
    {
        LogDir = FindLogDir();
        Directory.CreateDirectory(LogDir);
        _ = Task.Run(ProcessAsync);
    }

    public static void Info(string tag, string msg) => Enqueue(LogLevel.Info, tag, msg);
    public static void Warn(string tag, string msg) => Enqueue(LogLevel.Warning, tag, msg);
    public static void Error(string tag, string msg, Exception? ex = null) => Enqueue(LogLevel.Error, tag, msg, ex);

    [Conditional("DEBUG")]
    public static void Debug(string tag, string msg) => Enqueue(LogLevel.Debug, tag, msg);

    /// <summary>
    /// 설정 시 조건을 만족하는 항목만 콘솔에 출력합니다. 파일 기록은 항상 전체 수행됩니다.
    /// </summary>
    public static Func<LogEntry, bool>? ConsoleFilter { get; set; }

    public static async Task FlushAsync()
    {
        _pipe.Writer.Complete();
        await _pipe.Reader.Completion;
    }

    private static void Enqueue(LogLevel level, string tag, string msg, Exception? ex = null)
        => _pipe.Writer.TryWrite(new LogEntry(DateTime.Now, level, tag, msg, ex));

    private static async Task ProcessAsync()
    {
        var appName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name?.ToLowerInvariant() ?? "app";
        var path = Path.Combine(LogDir, $"{appName}-{DateTime.Now:yyyy-MM-dd}.log");
        await using var writer = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true };

        await foreach (var entry in _pipe.Reader.ReadAllAsync())
        {
            var line = Format(entry);
            if (ConsoleFilter == null || ConsoleFilter(entry))
            {
                Console.WriteLine(line);
            }
            
            await writer.WriteLineAsync(line);
        }
    }

    private static string Format(LogEntry e)
    {
        var levelStr = e.Level switch
        {
            LogLevel.Info    => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error   => "ERROR",
            LogLevel.Debug   => "DEBUG",
            _                => "INFO "
        };

        var line = $"[{e.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{levelStr}] [{e.Tag}] {e.Message}";
        if (e.Ex != null)
        {
            line += $"\n  Exception: {e.Ex.GetType().Name}: {e.Ex.Message}";
            if (e.Ex.InnerException != null)
                line += $"\n  InnerException: {e.Ex.InnerException.GetType().Name}: {e.Ex.InnerException.Message}";
            if (e.Ex.StackTrace != null)
                line += $"\n  StackTrace: {e.Ex.StackTrace}";
        }
        return line;
    }

    private static string FindLogDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
            {
                return Path.Combine(dir.FullName, "log");
            }
            dir = dir.Parent;
        }
        // 솔루션을 못 찾으면 실행 파일 옆에 log/ 생성
        return Path.Combine(AppContext.BaseDirectory, "log");
    }
}
