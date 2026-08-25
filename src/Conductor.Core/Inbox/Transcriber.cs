using System.Diagnostics;
using System.Globalization;
using System.Text;

using Conductor.Models;

namespace Conductor.Core.Inbox;

/// <summary>DV3.3 — speech to text, as a seam. One implementation shells out to a configured local
/// command; tests substitute their own rather than requiring a 3 GB model to be present, which is
/// also what lets the untranscribed and failed paths be exercised deterministically.</summary>
public interface ITranscriber
{
    /// <summary>The confidence below which a segment is marked in the stored note. On the interface
    /// rather than the implementation because the CALLER stores the note, and a mark drawn at one
    /// threshold on the wire and another on disk would be two different claims about the same
    /// words.</summary>
    double ConfidenceFloor { get; }

    /// <summary>Whether a command is configured at all. Checked before the audio is even looked at,
    /// because "not configured" is answered instantly and the answer is different.</summary>
    bool Configured { get; }

    /// <summary>Transcribe one file. NEVER THROWS: a transcriber that throws takes the poll loop
    /// down with it, and the note it was called about is worth more than the transcript.</summary>
    Task<TranscriptionOutcome> TranscribeAsync(string audioPath, CancellationToken ct);
}

/// <summary>DV3.3 / findings §1.6 — the configured local command, run over one audio file.
///
/// <para>Everything about it is deliberately dumb: one process, stdout parsed, a timeout, no
/// retries. The intelligence is in the command the owner chose, which on this machine is
/// faster-whisper on the GPU with a model that took three iterations to get right (see
/// <c>tools/transcribe/whisper-json.py</c>). An engine that tried to be clever here would be
/// second-guessing a component it cannot see.</para>
///
/// <para><b>It cannot fail loudly.</b> Every exception this can raise — a missing executable, a
/// killed process, a command that prints a stack trace — comes back as a
/// <see cref="TranscriptionStatus.Failed"/> outcome carrying a sentence. The note has already been
/// filed by the time this runs and the audio is on disk either way; a transcription failure must
/// cost the transcript and nothing else.</para></summary>
public sealed class LocalCommandTranscriber : ITranscriber
{
    /// <summary>Where the audio path goes in the command line.</summary>
    public const string AudioPlaceholder = "{audio}";

    private readonly TranscribeConfig _cfg;
    private readonly Action<string>? _log;

    public LocalCommandTranscriber(TranscribeConfig? cfg, Action<string>? log = null)
    {
        _cfg = cfg ?? new TranscribeConfig();
        _log = log;
    }

    public bool Configured => _cfg.ResolvedCommand() is { Length: > 0 };

    /// <summary>The floor this transcriber's marks are drawn at — carried here so the caller that
    /// stores the note does not have to reach back into the plan for it.</summary>
    public double ConfidenceFloor => _cfg.ConfidenceFloor;

    public async Task<TranscriptionOutcome> TranscribeAsync(string audioPath, CancellationToken ct)
    {
        if (_cfg.ResolvedCommand() is not { Length: > 0 } command)
            return new TranscriptionOutcome(TranscriptionStatus.NotConfigured, null, null);

        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            return new TranscriptionOutcome(TranscriptionStatus.Failed, null,
                "the audio file was not on disk when transcription started");

        var (exe, args) = Split(command, audioPath);
        var started = Stopwatch.StartNew();
        try
        {
            var (exit, stdout, stderr, timedOut) = await RunAsync(exe, args, ct).ConfigureAwait(false);
            _log?.Invoke(string.Create(CultureInfo.InvariantCulture,
                $"transcribe: {exe} exited {exit} after {started.Elapsed.TotalSeconds:0.0}s, {stdout.Length} chars out"));

            if (timedOut)
                return new TranscriptionOutcome(TranscriptionStatus.Failed, null,
                    string.Create(CultureInfo.InvariantCulture,
                        $"the transcribe command did not finish within {_cfg.TimeoutSeconds}s and was stopped"));

            if (exit != 0)
                return new TranscriptionOutcome(TranscriptionStatus.Failed, null,
                    string.Create(CultureInfo.InvariantCulture,
                        $"the transcribe command exited {exit}{Tail(stderr)}"));

            var transcript = Transcript.Parse(stdout);
            return transcript.Text.Trim().Length == 0
                ? new TranscriptionOutcome(TranscriptionStatus.NoSpeech, transcript,
                    "the transcribe command ran but heard no speech in it")
                : new TranscriptionOutcome(TranscriptionStatus.Ok, transcript, null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                      or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Named exceptions only, but every one of them ends here rather than in the poll loop.
            _log?.Invoke("transcribe: " + ex.Message);
            return new TranscriptionOutcome(TranscriptionStatus.Failed, null,
                "the transcribe command could not be run (" + ex.Message.Trim() + ")");
        }
    }

    /// <summary>The command line, with the audio in it. A path with a space in it is the normal case
    /// on Windows (<c>C:\Users\…\Local Settings\…</c>), so the substitution quotes unless the
    /// author already did.</summary>
    internal static (string Exe, string Args) Split(string command, string audioPath)
    {
        var (exe, rest) = FirstToken(command.Trim());
        var quoted = audioPath.Contains(' ', StringComparison.Ordinal) ? "\"" + audioPath + "\"" : audioPath;

        if (rest.Contains(AudioPlaceholder, StringComparison.Ordinal))
        {
            // An author who wrote "{audio}" meant the quotes; don't nest them.
            var already = rest.Replace("\"" + AudioPlaceholder + "\"", AudioPlaceholder, StringComparison.Ordinal);
            var same = !string.Equals(already, rest, StringComparison.Ordinal);
            return (exe, already.Replace(AudioPlaceholder, same ? "\"" + audioPath + "\"" : quoted,
                StringComparison.Ordinal));
        }

        return (exe, rest.Length == 0 ? quoted : rest + " " + quoted);
    }

    /// <summary>The executable, honouring one level of quoting — <c>"C:\Program Files\x\y.exe" -q</c>
    /// is one token and a space, not two tokens.</summary>
    private static (string Exe, string Tail) FirstToken(string command)
    {
        if (command.StartsWith('"'))
        {
            var close = command.IndexOf('"', 1);
            if (close > 0) return (command[1..close], command[(close + 1)..].TrimStart());
        }

        var space = command.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (command, "") : (command[..space], command[(space + 1)..].TrimStart());
    }

    private async Task<(int Exit, string Stdout, string Stderr, bool TimedOut)> RunAsync(
        string exe, string args, CancellationToken ct)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };

        proc.Start();
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _cfg.TimeoutSeconds)));
        try
        {
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(proc);
            return (-1, "", "", true);
        }

        return (proc.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false),
            false);
    }

    private static void TryKill(Process proc)
    {
        try { proc.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                      or System.ComponentModel.Win32Exception)
        {
            // Already gone, or not ours to kill. Either way the outcome is the same timeout.
        }
    }

    /// <summary>The last line of stderr, clipped — enough to name the failure in a chat message
    /// without pasting a Python traceback into somebody's phone.</summary>
    private static string Tail(string stderr)
    {
        var line = stderr.Replace("\r", "", StringComparison.Ordinal)
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                         .LastOrDefault();
        if (line is not { Length: > 0 }) return "";
        if (line.Length > 160) line = line[..159] + "…";
        return ": " + line;
    }
}
