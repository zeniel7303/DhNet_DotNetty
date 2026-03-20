using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace Common.Logging;

public static class GameLogger
{
    private static readonly Channel<LogEntry> _pipe = Channel.CreateUnbounded<LogEntry>(
        new UnboundedChannelOptions { SingleReader = true });

    private static readonly string LogDir;

    // BOM 없는 UTF-8 — 파일 크기 추적 오차 제거 및 범용 호환성
    private static readonly Encoding LogEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

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

    /// <summary>
    /// 채널을 완료하고 남은 로그가 모두 기록될 때까지 대기합니다.
    /// Shutdown 전용 — 호출 후에는 Enqueue가 무시됩니다. 한 번만 호출해야 합니다.
    /// </summary>
    public static async Task FlushAsync()
    {
        _pipe.Writer.Complete();
        await _pipe.Reader.Completion;
    }

    private static void Enqueue(LogLevel level, string tag, string msg, Exception? ex = null)
        => _pipe.Writer.TryWrite(new LogEntry(DateTime.Now, level, tag, msg, ex));

    // 단일 로그 파일 최대 크기 (기본 100MB). 초과 시 같은 날짜에 인덱스 증가(-1, -2, ...).
    private const long MaxFileSizeBytes = 100L * 1024 * 1024;

    private static string BuildLogPath(string appName, DateOnly date, int index)
    {
        var fileName = index == 0
            ? $"{appName}-{date:yyyy-MM-dd}.log"
            : $"{appName}-{date:yyyy-MM-dd}-{index}.log";
        return Path.Combine(LogDir, fileName);
    }

    // 프로세스 재시작 시에도 마지막으로 쓰던 파일을 찾아 이어씀.
    // MaxFileSizeBytes 미만인 파일을 찾거나, 없으면 새 인덱스를 반환.
    private static (string path, int index, long size) FindCurrentFile(string appName, DateOnly date)
    {
        var index = 0;
        while (true)
        {
            var path = BuildLogPath(appName, date, index);
            if (!File.Exists(path))
                return (path, index, 0L);
            var size = new FileInfo(path).Length;
            if (size <= MaxFileSizeBytes)
                return (path, index, size);
            index++;
        }
    }

    private static async Task ProcessAsync()
    {
        var appName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name?.ToLowerInvariant() ?? "app";
        var currentDate = DateOnly.FromDateTime(DateTime.Now);

        var (currentPath, fileIndex, currentSize) = FindCurrentFile(appName, currentDate);
        var writer = new StreamWriter(currentPath, append: true, LogEncoding) { AutoFlush = true };

        try
        {
            await foreach (var entry in _pipe.Reader.ReadAllAsync())
            {
                try
                {
                    // 날짜 롤링 기준: entry.Timestamp (소비 시점이 아닌 생성 시점 기준)
                    var entryDate = DateOnly.FromDateTime(entry.Timestamp);

                    if (entryDate != currentDate)
                    {
                        // 날짜 롤링 — 새 날짜 파일로 전환
                        await writer.DisposeAsync();
                        currentDate = entryDate;
                        (currentPath, fileIndex, currentSize) = FindCurrentFile(appName, currentDate);
                        writer = new StreamWriter(currentPath, append: true, LogEncoding) { AutoFlush = true };
                    }
                    else if (currentSize > MaxFileSizeBytes)
                    {
                        // 크기 롤링 — 동일 날짜 내 인덱스 증가
                        await writer.DisposeAsync();
                        fileIndex++;
                        currentPath = BuildLogPath(appName, currentDate, fileIndex);
                        currentSize = 0L;
                        writer = new StreamWriter(currentPath, append: true, LogEncoding) { AutoFlush = true };
                    }

                    var line = Format(entry);

                    // ConsoleFilter를 로컬 변수에 캡처 — null 체크와 호출 사이 race condition 방지
                    var filter = ConsoleFilter;
                    if (filter == null || filter(entry))
                    {
                        Console.WriteLine(line);
                    }

                    await writer.WriteLineAsync(line);
                    currentSize += LogEncoding.GetByteCount(line) + Environment.NewLine.Length;
                }
                catch (Exception ex)
                {
                    // 개별 로그 항목 실패가 전체 루프를 중단시키지 않도록 격리
                    Console.Error.WriteLine($"[GameLogger] 로그 기록 실패: {ex.Message}");
                }
            }
        }
        finally
        {
            await writer.DisposeAsync();
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
