using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// Runs a read-only analysis lane (B12.1 Tier A). Spawns an agent in a scratch temp directory
/// so it can never modify the working tree — the same safety-by-construction pattern as
/// <see cref="StatusAgent"/>. Artifacts are stored under <c>.conductor/lanes/</c> and injected
/// into the next session's prompt via <see cref="LaneArtifactBattery"/>.
/// </summary>
public static class LaneRunner
{
    /// <summary>Builds the full lane prompt from the lane config and run context.</summary>
    public static string BuildPrompt(AnalysisLaneConfig lane, string planName, string stageId, string stageTitle,
        string? handoff, string? gitSummary)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"You are a read-only {lane.Kind} analyst for an autonomous engineering run.");
        sb.AppendLine("Do NOT edit files or run commands that modify state. Your output will be injected");
        sb.AppendLine("into the next engineering session's prompt as an advisory artifact.");
        sb.AppendLine();
        sb.AppendLine("## Context");
        sb.AppendLine($"Plan: {planName}, stage: {stageId} ({stageTitle})");
        if (!string.IsNullOrWhiteSpace(gitSummary))
        {
            sb.AppendLine("### Git");
            sb.AppendLine(gitSummary);
        }
        if (!string.IsNullOrWhiteSpace(handoff))
        {
            sb.AppendLine("### Session handoff");
            sb.AppendLine(handoff);
        }
        sb.AppendLine();
        sb.AppendLine("## Analysis task");
        sb.AppendLine(lane.Prompt);
        sb.AppendLine();
        sb.AppendLine("Produce a concise, actionable analysis. Use markdown. Be specific about risks,");
        sb.AppendLine("gaps, and recommendations. The next session reads this directly.");
        return sb.ToString();
    }

    /// <summary>Run a lane in a scratch temp directory and return the captured output.</summary>
    public static async Task<LaneResult> RunAsync(AnalysisLaneConfig lane, AgentConfig agent,
        string planName, string stageId, string stageTitle, string stateDir,
        string? handoff, string? gitSummary, CancellationToken ct)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"conductor-lane-{lane.Id}-{Guid.NewGuid():N}"[..48]);
        Directory.CreateDirectory(scratch);
        try
        {
            var prompt = BuildPrompt(lane, planName, stageId, stageTitle, handoff, gitSummary);
            var promptPath = Path.Combine(scratch, "prompt.md");
            await File.WriteAllTextAsync(promptPath, prompt, ct).ConfigureAwait(false);

            var start = DateTime.UtcNow;
            var args = agent.Args.Select(a =>
                a.Replace("{prompt}", prompt)
                 .Replace("{sessionId}", lane.Id));
            var result = await ProcessRunner.RunAsync(agent.Command, args, scratch,
                TimeSpan.FromMinutes(lane.TimeoutMinutes), ct).ConfigureAwait(false);

            var elapsed = (DateTime.UtcNow - start).TotalSeconds;
            var output = result.Output;
            if (!string.IsNullOrWhiteSpace(output))
            {
                var lines = output.Split('\n');
                if (lines.Length > lane.MaxOutputLines)
                    output = string.Join('\n', lines.Take(lane.MaxOutputLines)) +
                             $"\n\n_(truncated — {lines.Length - lane.MaxOutputLines} more lines)_";
            }
            else
            {
                output = $"(lane {lane.Id} produced no output — exit {result.ExitCode}" +
                         (result.TimedOut ? ", timed out" : "") + ")";
            }

            // Write artifact to .conductor/lanes/ for prompt injection
            var lanesDir = Path.Combine(stateDir, "lanes");
            Directory.CreateDirectory(lanesDir);
            var artifactPath = Path.Combine(lanesDir, $"{lane.Id}.md");
            var header = $"# Analysis lane: {lane.Name}\n" +
                         $"kind: {lane.Kind} | stage: {stageId} | completed: {DateTime.UtcNow:u}\n" +
                         "_Read-only — produced in scratch dir, never touched the working tree._\n\n";
            await File.WriteAllTextAsync(artifactPath, header + output, ct).ConfigureAwait(false);

            return new LaneResult
            {
                LaneId = lane.Id,
                Kind = lane.Kind,
                ArtifactPath = artifactPath,
                Output = output,
                ExitCode = result.ExitCode,
                TimedOut = result.TimedOut,
                CompletedUtc = DateTime.UtcNow,
                ElapsedMs = (long)(elapsed * 1000),
            };
        }
        catch (OperationCanceledException)
        {
            return new LaneResult { LaneId = lane.Id, Kind = lane.Kind, Error = "cancelled", CompletedUtc = DateTime.UtcNow };
        }
        catch (Exception ex)
        {
            return new LaneResult { LaneId = lane.Id, Kind = lane.Kind, Error = ex.Message, CompletedUtc = DateTime.UtcNow };
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Result of running a single read-only analysis lane.</summary>
public sealed class LaneResult
{
    public string LaneId { get; init; } = "";
    public string Kind { get; init; } = "analysis";
    public string? ArtifactPath { get; init; }
    public string? Output { get; init; }
    public int ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public string? Error { get; init; }
    public DateTime? CompletedUtc { get; init; }
    public long ElapsedMs { get; init; }
    public bool IsSuccess => Error == null;
}
