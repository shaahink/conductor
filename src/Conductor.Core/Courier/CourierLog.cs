using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

namespace Conductor.Core.Courier;

/// <summary>
/// Bug #93 — the courier's own log file, so an outage is diagnosable after the fact.
///
/// <para>Measured at CH5.2: the real courier's scheduled task had <c>LastTaskResult 1</c>, its
/// restart-on-failure had not brought it back, and the courier home held exactly two files —
/// <c>courier.json</c> and <c>courier.secret</c>. The task action redirects nothing, the process
/// logged to a console nobody was attached to, and the machine's Task Scheduler operational log is
/// disabled. Nothing has been polling Telegram since it died, and nothing could say why.</para>
///
/// <para>This is the smallest thing that answers "why": an append-only file beside the settings,
/// one line per event, written by <c>courier run</c> from before its first refusal to after its last
/// exception. Rotation is by size and keeps one previous generation, because a daemon that runs for
/// months writes a line every poll error and a log that grows without bound is a disk full in the
/// state home. <c>courier status</c> prints the tail, which is what a person reads first.</para>
/// </summary>
public sealed class CourierLog
{
    /// <summary>Rotate above this size. A megabyte is months of poll errors; the previous generation
    /// keeps the lines around the rotation so a death at the boundary is not lost in it.</summary>
    public const long RotateAtBytes = 1_000_000;

    /// <summary>How many lines <c>courier status</c> shows.</summary>
    public const int StatusTailLines = 5;

    private readonly Lock _gate = new();

    public CourierLog(string path) => Path = path ?? throw new ArgumentNullException(nameof(path));

    public string Path { get; }

    /// <summary>The courier home's log, at the resolved state home or the one named.</summary>
    public static CourierLog At(string? stateHomeRoot = null) => new(CourierHome.LogPathFor(stateHomeRoot));

    /// <summary>One line, timestamped, appended. Never throws: a log that can kill the daemon it
    /// exists to explain would be the wrong trade, so an unwritable file is a lost line and nothing
    /// else. Rotates first when the file has outgrown <see cref="RotateAtBytes"/>.</summary>
    public void Append(string line)
    {
        var stamped = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z " +
                      (line ?? "").Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ') + "\n";
        lock (_gate)
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                RotateIfLarge();
                File.AppendAllText(Path, stamped, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Lost line. The daemon goes on; the console (if any) still had it.
            }
        }
    }

    /// <summary>The last <paramref name="lines"/> lines, oldest first. Empty when there is no file —
    /// which <c>courier status</c> says out loud, because "no log" on a machine where the task has
    /// run is itself the finding.</summary>
    public IReadOnlyList<string> Tail(int lines = StatusTailLines)
    {
        try
        {
            if (!File.Exists(Path)) return [];
            var all = File.ReadAllLines(Path);
            return all.Length <= lines ? all : all[^lines..];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>When the file was last written, or null when there is none.</summary>
    public DateTime? LastWriteUtc()
    {
        try { return File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A provider that mirrors every logger line into this file, so the daemon's existing
    /// <c>ILogger</c> lines — listener state, poll conflicts, secret complaints — reach the record
    /// without a second phrasing anywhere.</summary>
    public ILoggerProvider AsProvider() => new Provider(this);

    private void RotateIfLarge()
    {
        if (!File.Exists(Path)) return;
        if (new FileInfo(Path).Length < RotateAtBytes) return;
        var previous = Path + ".1";
        if (File.Exists(previous)) File.Delete(previous);
        File.Move(Path, previous);
    }

    private sealed class Provider(CourierLog log) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new FileLogger(log, categoryName);

        public void Dispose() { }
    }

    private sealed class FileLogger(CourierLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            ArgumentNullException.ThrowIfNull(formatter);
            var text = formatter(state, exception);
            if (exception is not null) text += " :: " + exception.GetType().Name + ": " + exception.Message;
            log.Append($"[{Level(logLevel)}] {category}: {text}");
        }

        private static string Level(LogLevel l) => l switch
        {
            LogLevel.Warning => "warn",
            LogLevel.Error => "error",
            LogLevel.Critical => "fatal",
            _ => "info",
        };
    }
}
