using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Conductor.Models;

namespace Conductor.Core;

public sealed class AgentEvent
{
    public DateTime Utc { get; } = DateTime.UtcNow;
    public string Kind { get; init; } = "raw"; // system | text | tool | result | stderr | raw
    public string Text { get; init; } = "";
}

/// <summary>
/// One headless agent run (claude -p / opencode run). Parses stream-json when configured,
/// tracks last-activity for stall detection, tees the raw stream to a log file.
/// </summary>
public sealed class AgentSession : IDisposable
{
    private readonly Process _proc;
    private readonly StreamWriter _raw;
    private readonly ConcurrentQueue<AgentEvent> _events = new();
    private readonly bool _streamJson;
    private readonly object _gate = new();
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;

    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public DateTime LastActivityUtc => new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);
    public string? ResultText { get; private set; }
    public bool ResultIsError { get; private set; }
    public decimal? CostUsd { get; private set; }
    public int? NumTurns { get; private set; }
    public bool WasKilled { get; private set; }

    private AgentSession(Process proc, StreamWriter raw, bool streamJson)
    {
        _proc = proc;
        _raw = raw;
        _streamJson = streamJson;
    }

    public static AgentSession Start(AgentConfig cfg, string cwd, string prompt, string sessionId, string? resumeClaudeId, string rawLogPath)
    {
        var template = (resumeClaudeId != null && cfg.ResumeArgs is { Count: > 0 }) ? cfg.ResumeArgs : cfg.Args;
        var args = template.Select(a => a
            .Replace("{prompt}", prompt)
            .Replace("{sessionId}", sessionId)
            .Replace("{claudeSessionId}", resumeClaudeId ?? sessionId)).ToList();

        var psi = new ProcessStartInfo(cfg.Command)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Directory.CreateDirectory(Path.GetDirectoryName(rawLogPath)!);
        var raw = new StreamWriter(rawLogPath, append: false, Encoding.UTF8) { AutoFlush = true };

        var proc = new Process { StartInfo = psi };
        var session = new AgentSession(proc, raw, cfg.Output.Equals("stream-json", StringComparison.OrdinalIgnoreCase));
        proc.OutputDataReceived += (_, e) => session.OnLine(e.Data, stderr: false);
        proc.ErrorDataReceived += (_, e) => session.OnLine(e.Data, stderr: true);
        proc.Start();
        try { proc.StandardInput.Close(); } catch { /* agent may not read stdin */ }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return session;
    }

    private void OnLine(string? line, bool stderr)
    {
        if (line == null) return;
        Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
        lock (_gate) { try { _raw.WriteLine((stderr ? "[stderr] " : "") + line); } catch { } }

        if (stderr) { Push("stderr", Trunc(line, 220)); return; }
        var t = line.TrimStart();
        if (!_streamJson || !t.StartsWith('{')) { Push("raw", Trunc(line, 220)); return; }

        try
        {
            using var doc = JsonDocument.Parse(t);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var ty) ? ty.GetString() : null;
            switch (type)
            {
                case "system":
                    Push("system", root.TryGetProperty("subtype", out var st) ? st.GetString() ?? "system" : "system");
                    break;
                case "assistant":
                    if (root.TryGetProperty("message", out var msg) &&
                        msg.TryGetProperty("content", out var content) &&
                        content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var block in content.EnumerateArray())
                        {
                            var bt = block.TryGetProperty("type", out var b) ? b.GetString() : null;
                            if (bt == "text" && block.TryGetProperty("text", out var txt))
                            {
                                var s = (txt.GetString() ?? "").Trim();
                                if (s.Length > 0) Push("text", Trunc(s, 220));
                            }
                            else if (bt == "tool_use")
                            {
                                var name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "tool" : "tool";
                                var input = block.TryGetProperty("input", out var inp) ? Trunc(inp.GetRawText(), 150) : "";
                                Push("tool", $"{name} {input}");
                            }
                        }
                    }
                    break;
                case "result":
                    if (root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True) ResultIsError = true;
                    if (root.TryGetProperty("subtype", out var sub) && (sub.GetString() ?? "").StartsWith("error")) ResultIsError = true;
                    if (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String) ResultText = res.GetString();
                    if (root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number) CostUsd = c.GetDecimal();
                    if (root.TryGetProperty("num_turns", out var nt) && nt.ValueKind == JsonValueKind.Number) NumTurns = nt.GetInt32();
                    Push("result", ResultIsError ? "ERROR result: " + Trunc(ResultText ?? "", 160) : "result received");
                    break;
                default:
                    Push("raw", Trunc(line, 180));
                    break;
            }
        }
        catch (JsonException)
        {
            Push("raw", Trunc(line, 180));
        }
    }

    private void Push(string kind, string text) => _events.Enqueue(new AgentEvent { Kind = kind, Text = text });

    private static string Trunc(string s, int max)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length <= max ? s : s[..max] + "…";
    }

    public bool HasExited
    {
        get { try { return _proc.HasExited; } catch { return true; } }
    }

    public bool TryDequeue(out AgentEvent ev) => _events.TryDequeue(out ev!);

    public void Kill()
    {
        WasKilled = true;
        try { _proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }

    public int WaitForExitCode()
    {
        try { _proc.WaitForExit(); return _proc.ExitCode; } catch { return -1; }
    }

    public void Dispose()
    {
        lock (_gate) { try { _raw.Dispose(); } catch { } }
        try { _proc.Dispose(); } catch { }
    }
}
