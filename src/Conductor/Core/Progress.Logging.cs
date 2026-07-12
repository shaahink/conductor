namespace Conductor.Core;

public enum LogSeverity
{
    Info,
    Warn,
    Error,
    Success,
    Waiting,
    Human,
}

public readonly record struct LogEntry(string Text, DateTime Utc, LogSeverity Severity = LogSeverity.Info);

public readonly record struct ToastMessage(string Text, LogSeverity Severity);
