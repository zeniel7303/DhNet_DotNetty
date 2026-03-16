namespace Common.Logging;

public record LogEntry(DateTime Timestamp, LogLevel Level, string Tag, string Message, Exception? Ex = null);
