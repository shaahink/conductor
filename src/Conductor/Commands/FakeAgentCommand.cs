using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Conductor.Core;

using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// The agent <c>conductor demo</c> drives — a token-free stand-in for a real coding CLI, built into
/// the binary so the demo needs no script, no interpreter and no credentials on any platform.
///
/// It is the in-process twin of <c>tools/w5/agent.ps1</c> and behaves the same way: it is a
/// WELL-BEHAVED agent on the W1/W2 claim path, not the M4.1 rigged-agent case. It reads which stage
/// it is on out of its own prompt, picks the next open row from the GENERATED tracker, does a token
/// of real work, commits, and then reports through <c>conductor task --done</c> — the one claim
/// path. Hand-editing a tracker row would claim nothing, by design.
///
/// The claim is made by spawning THIS executable again, deliberately with no <c>-p</c>, so a demo
/// run only advances if <c>CONDUCTOR_PLAN</c> really reaches the child environment. That makes the
/// demo a test of the real plumbing rather than a puppet show.
///
/// Hidden from <c>--help</c>: it is an implementation detail of `demo`, not a verb to reach for.
/// </summary>
public sealed class FakeAgentCommand : AsyncCommand<FakeAgentCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--repo <DIR>")]
        [Description("The repository the session is working in.")]
        public string Repo { get; init; } = "";

        [CommandOption("--prompt <TEXT>")]
        [Description("The session prompt, as the engine compiled it.")]
        public string Prompt { get; init; } = "";

        [CommandOption("--session <ID>")]
        [Description("Session id, echoed back on every event.")]
        public string SessionId { get; init; } = "demo";
    }

    private static readonly TimeSpan RxTimeout = TimeSpan.FromSeconds(2);

    // opencode's nd-JSON: payload nested under "part". AgentSession.ParseOpencode tolerates a flat
    // shape too, but emitting the real one keeps this honest about the wire format it stands in for.
    private static void Emit(string type, string sessionId, object? part)
    {
        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["type"] = type,
            ["session_id"] = sessionId,
        };
        if (part is not null) payload["part"] = part;
        Console.WriteLine(JsonSerializer.Serialize(payload));
        Console.Out.Flush();
    }

    private static void Text(string sessionId, string text) =>
        Emit("text", sessionId, new Dictionary<string, object>(StringComparer.Ordinal) { ["text"] = text });

    private static void Step(string sessionId, string title, double cost) =>
        Emit("step_finish", sessionId, new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["cost"] = cost,
            ["tokens"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["input"] = 120,
                ["output"] = 60,
                ["reasoning"] = 0,
                ["cache"] = new Dictionary<string, object>(StringComparer.Ordinal) { ["read"] = 0 },
            },
            ["state"] = new Dictionary<string, object>(StringComparer.Ordinal) { ["title"] = title },
        });

    private static void Tool(string sessionId, string tool, string title) =>
        Emit("tool_use", sessionId, new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["tool"] = tool,
            ["state"] = new Dictionary<string, object>(StringComparer.Ordinal) { ["title"] = title },
        });

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var repo = string.IsNullOrWhiteSpace(settings.Repo) ? Directory.GetCurrentDirectory() : settings.Repo;
        var sid = settings.SessionId;

        Emit("step_start", sid, null);

        // A Verify prompt demands a single {"score":…} object and nothing else; anything else is an
        // AgentError that burns a stage attempt. Play the verifier so the loop can keep advancing.
        if (settings.Prompt.Contains("VERIFICATION session", StringComparison.Ordinal))
        {
            Text(sid, "Re-checking the claims against the diff (demo verifier).");
            Step(sid, "verifying", 0.0001);
            Text(sid, """{"score":95,"findings":[],"verdict":"PASS"}""");
            return 0;
        }

        var stageId = StageFromPrompt(settings.Prompt);
        var checkpoint = await NextOpenCheckpointAsync(repo, stageId).ConfigureAwait(false);
        if (checkpoint is null)
        {
            Text(sid, "The tracker shows no incomplete checkpoint for this stage — nothing to deliver.");
            Step(sid, "idle", 0.0001);
            Text(sid, "SESSION-RESULT: nothing left open for this stage.");
            return 0;
        }

        Text(sid, $"Reading the tracker. Next open checkpoint for stage {stageId}: {checkpoint}.");

        // Deliver something real, so the verdict has an actual diff to judge.
        var stamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        await File.AppendAllTextAsync(Path.Combine(repo, "work.txt"),
            $"{stamp} delivered {checkpoint}{Environment.NewLine}").ConfigureAwait(false);
        await GitAsync(repo, "add", "-A").ConfigureAwait(false);
        await GitAsync(repo, "commit", "-m", $"feat(demo): deliver {checkpoint}", "--no-gpg-sign", "--quiet").ConfigureAwait(false);
        var head = await GitAsync(repo, "rev-parse", "--short", "HEAD").ConfigureAwait(false);
        var sha = head.Output.Trim();
        Tool(sid, "Bash", "git add -A; git commit");
        Step(sid, "committing", 0.0002);

        // The one claim path. No -p on purpose: CONDUCTOR_PLAN has to carry the plan to get here.
        var exe = Environment.ProcessPath ?? "conductor";
        var claim = await ProcessRunner.RunAsync(
            exe,
            ["task", "--done", checkpoint, "-c", sha, "-e", "delivered by the built-in demo agent"],
            repo,
            TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        Tool(sid, "Bash", $"conductor task --done {checkpoint}");
        if (claim.ExitCode != 0)
            Text(sid, $"could not claim {checkpoint}: {claim.Output.Trim()} {claim.StdErr.Trim()}");
        Step(sid, "claiming", 0.0001);

        Text(sid, $"SESSION-RESULT: delivered {checkpoint} at {sha}; gates should be green.");
        return 0;
    }

    private static Task<ProcResult> GitAsync(string repo, params string[] args) =>
        ProcessRunner.RunAsync("git", args, repo, TimeSpan.FromSeconds(30));

    /// <summary>Which stage is this session for? Exactly what the prompt instructs.</summary>
    internal static string StageFromPrompt(string prompt)
    {
        var m = Regex.Match(prompt, @"checkpoint\(s\) of stage\s+(?<s>[A-Za-z]{1,4}\d+)", RegexOptions.None, RxTimeout);
        if (m.Success) return m.Groups["s"].Value;
        m = Regex.Match(prompt, @"(?m)^\s*##\s+Stage\s+(?<s>[A-Za-z]{1,4}\d+)", RegexOptions.None, RxTimeout);
        return m.Success ? m.Groups["s"].Value : "";
    }

    /// <summary>First TODO / IN PROGRESS row of this stage in the generated tracker, or null.</summary>
    internal static async Task<string?> NextOpenCheckpointAsync(string repo, string stageId)
    {
        foreach (var file in Directory.EnumerateFiles(repo, "*.md", SearchOption.TopDirectoryOnly)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            string text;
            try { text = await File.ReadAllTextAsync(file).ConfigureAwait(false); }
            catch (IOException) { continue; }

            var found = FirstOpenRow(text, stageId);
            if (found is not null) return found;
        }
        return null;
    }

    internal static string? FirstOpenRow(string trackerText, string stageId)
    {
        foreach (var line in trackerText.Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*\|\s*(?<id>[A-Za-z]{1,4}\d+\.[A-Za-z0-9]+)\s*\|[^|]*\|\s*(?<st>[^|]*?)\s*\|",
                RegexOptions.None, RxTimeout);
            if (!m.Success) continue;
            var status = m.Groups["st"].Value.Trim();
            if (!status.Equals("TODO", StringComparison.OrdinalIgnoreCase)
                && !status.Equals("IN PROGRESS", StringComparison.OrdinalIgnoreCase)) continue;
            var id = m.Groups["id"].Value;
            if (stageId.Length > 0 && !id.StartsWith(stageId + ".", StringComparison.OrdinalIgnoreCase)) continue;
            return id;
        }
        return null;
    }
}
