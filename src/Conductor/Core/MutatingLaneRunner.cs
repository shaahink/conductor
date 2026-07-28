using System.Diagnostics;
using Conductor.Core.Events;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// Runs a Tier B isolated-worktree mutating lane (B12.3). Each lane runs in its own
/// <c>git worktree</c> on a scratch branch so it can freely mutate files without touching
/// the primary tree. A merge gate runs the full battery on the integrated tree (base + scratch
/// merged) before the lane's changes are accepted — red battery → rejected, never merged.
/// </summary>
public static class MutatingLaneRunner
{
    /// <summary>Builds the lane prompt with context.</summary>
    public static string BuildPrompt(MutatingLaneConfig lane, string planName, string stageId)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"You are a {lane.Kind} agent working in an isolated git worktree.");
        sb.AppendLine("You can freely create, edit, and delete files. Stage and commit your work.");
        sb.AppendLine("Your changes will be verified by a merge gate before being accepted.");
        sb.AppendLine();
        sb.AppendLine("## Context");
        sb.AppendLine($"Plan: {planName}, stage: {stageId}");
        sb.AppendLine();
        sb.AppendLine("## Task");
        sb.AppendLine(lane.Prompt);
        sb.AppendLine();
        sb.AppendLine("Commit your work with clear, conventional commit messages.");
        sb.AppendLine("If you cannot complete the task, leave a note in .conductor/lane-failure.md");
        sb.AppendLine("and do not commit broken code.");
        return sb.ToString();
    }

    /// <summary>
    /// Run a Tier B mutating lane: create a <c>git worktree</c> on a scratch branch, run the
    /// agent, then verify via merge gate before accepting changes into the primary tree.
    /// </summary>
    public static async Task<MutatingLaneResult> RunAsync(
        PlanConfig plan, MutatingLaneConfig lane, AgentConfig agent,
        string stageId, IEventSink? events, Action<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var baseBranch = Git.Branch(plan.Repo);
        var baseHead = Git.Head(plan.Repo);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var scratchBranch = $"conductor-lane-{lane.Id}-{suffix}";
        var lanePath = Path.Combine(Path.GetTempPath(), $"conductor-mutating-{lane.Id}-{suffix}");

        try
        {
            // 1. Create worktree on a new scratch branch
            var createResult = Git.WorktreeAdd(plan.Repo, lanePath, scratchBranch);
            if (createResult.ExitCode != 0)
            {
                var msg = $"worktree creation failed for lane '{lane.Id}': {createResult.Output.Trim()}";
                log?.Invoke(msg);
                return new MutatingLaneResult { LaneId = lane.Id, Kind = lane.Kind, Error = msg,
                    CompletedUtc = DateTime.UtcNow, ElapsedMs = sw.ElapsedMilliseconds };
            }

            events?.Emit(new MutatingLaneStarted
            {
                LaneId = lane.Id, Kind = lane.Kind, StageId = stageId,
                ScratchBranch = scratchBranch,
            });
            log?.Invoke($"mutating lane '{lane.Id}' ({lane.Kind}) started in worktree '{scratchBranch}'");

            // 2. Write the prompt into the worktree for the agent to read
            var prompt = BuildPrompt(lane, plan.Name, stageId);
            var promptPath = Path.Combine(lanePath, ".conductor-lane-prompt.md");
            await File.WriteAllTextAsync(promptPath, prompt, ct).ConfigureAwait(false);

            // 3. Run the agent inside the worktree
            var args = agent.Args.Select(a =>
                a.Replace("{prompt}", prompt)
                 .Replace("{sessionId}", lane.Id));
            var agentResult = await ProcessRunner.RunAsync(agent.Command, args, lanePath,
                TimeSpan.FromMinutes(lane.TimeoutMinutes), ct).ConfigureAwait(false);

            if (agentResult.TimedOut)
            {
                events?.Emit(new MutatingLaneFinished
                {
                    LaneId = lane.Id, Kind = lane.Kind, Outcome = "error",
                    Error = $"agent timed out after {lane.TimeoutMinutes}min",
                    DurationMs = sw.ElapsedMilliseconds, AgentCommitted = false,
                });
                return new MutatingLaneResult { LaneId = lane.Id, Kind = lane.Kind,
                    Error = $"agent timed out after {lane.TimeoutMinutes}min",
                    CompletedUtc = DateTime.UtcNow, ElapsedMs = sw.ElapsedMilliseconds };
            }

            if (ct.IsCancellationRequested)
            {
                events?.Emit(new MutatingLaneFinished
                {
                    LaneId = lane.Id, Kind = lane.Kind, Outcome = "cancelled",
                    DurationMs = sw.ElapsedMilliseconds, AgentCommitted = false,
                });
                return new MutatingLaneResult { LaneId = lane.Id, Kind = lane.Kind,
                    Error = "cancelled",
                    CompletedUtc = DateTime.UtcNow, ElapsedMs = sw.ElapsedMilliseconds };
            }

            // 4. Check if the agent committed anything
            var commits = Git.CommitsSince(lanePath, baseHead);
            var agentCommitted = commits.Count > 0;
            if (!agentCommitted)
            {
                events?.Emit(new MutatingLaneFinished
                {
                    LaneId = lane.Id, Kind = lane.Kind, Outcome = "success",
                    DurationMs = sw.ElapsedMilliseconds, AgentCommitted = false,
                });
                log?.Invoke($"mutating lane '{lane.Id}' completed — no commits (nothing to merge)");
                return new MutatingLaneResult { LaneId = lane.Id, Kind = lane.Kind,
                    Merged = false, MergeGatePassed = null, AgentOutput = agentResult.Output,
                    CompletedUtc = DateTime.UtcNow, ElapsedMs = sw.ElapsedMilliseconds,
                    AgentCommitted = false };
            }

            // 5. Merge gate: create a staging worktree from the base branch, merge scratch into it,
            //    run the battery there to verify the integrated tree.
            var stagingSuffix = Guid.NewGuid().ToString("N")[..8];
            var stagingBranch = $"conductor-staging-{lane.Id}-{stagingSuffix}";
            var stagingPath = Path.Combine(Path.GetTempPath(), $"conductor-mergegate-{lane.Id}-{stagingSuffix}");

            var mergeGateResult = await RunMergeGateAsync(
                plan, lane, plan.Repo, stagingPath, stagingBranch,
                scratchBranch, baseBranch, log, ct).ConfigureAwait(false);

            // 6. Clean up staging worktree regardless of outcome
            try { Git.WorktreeRemove(plan.Repo, stagingPath); }
            catch { /* best-effort cleanup */ }
            try { Git.DeleteBranch(plan.Repo, stagingBranch); }
            catch { /* best-effort cleanup */ }

            // 7. Emit lane finished event
            events?.Emit(new MutatingLaneFinished
            {
                LaneId = lane.Id, Kind = lane.Kind,
                Outcome = mergeGateResult.Passed ? "success" : "failure",
                Error = mergeGateResult.Passed ? null : "merge gate rejected",
                DurationMs = sw.ElapsedMilliseconds,
                AgentCommitted = true,
            });

            // 8. Emit merge gate verdict
            events?.Emit(new MergeGateVerdict
            {
                LaneId = lane.Id, Kind = lane.Kind,
                Passed = mergeGateResult.Passed,
                TotalGates = mergeGateResult.TotalGates,
                PassedCount = mergeGateResult.PassedCount,
                FailedCount = mergeGateResult.FailedCount,
                FailureSummary = mergeGateResult.FailureSummary,
                DurationMs = mergeGateResult.DurationMs,
            });

            if (mergeGateResult.Passed)
            {
                // Merge the scratch branch into the primary repo (fast-forward if possible)
                var ffResult = Git.Exec(plan.Repo, "merge", "--ff-only", scratchBranch);
                var merged = ffResult.ExitCode == 0;
                log?.Invoke($"mutating lane '{lane.Id}' merge {(merged ? "accepted" : "ff-only failed: " + ffResult.Output)}");
                return new MutatingLaneResult { LaneId = lane.Id, Kind = lane.Kind,
                    Merged = merged, MergeGatePassed = true, MergeGate = mergeGateResult,
                    AgentOutput = agentResult.Output, CompletedUtc = DateTime.UtcNow,
                    ElapsedMs = sw.ElapsedMilliseconds, AgentCommitted = true,
                    Error = merged ? null : "fast-forward merge failed after gate passed" };
            }
            else
            {
                log?.Invoke($"mutating lane '{lane.Id}' merge gate FAILED — lane rejected, branch '{scratchBranch}' not merged");
                return new MutatingLaneResult { LaneId = lane.Id, Kind = lane.Kind,
                    Merged = false, MergeGatePassed = false, MergeGate = mergeGateResult,
                    AgentOutput = agentResult.Output, CompletedUtc = DateTime.UtcNow,
                    ElapsedMs = sw.ElapsedMilliseconds, AgentCommitted = true,
                    Error = "merge gate rejected" };
            }
        }
        catch (OperationCanceledException)
        {
            events?.Emit(new MutatingLaneFinished
            {
                LaneId = lane.Id, Kind = lane.Kind, Outcome = "cancelled",
                DurationMs = sw.ElapsedMilliseconds, AgentCommitted = false,
            });
            return new MutatingLaneResult { LaneId = lane.Id, Kind = lane.Kind,
                Error = "cancelled", CompletedUtc = DateTime.UtcNow, ElapsedMs = sw.ElapsedMilliseconds };
        }
        catch (Exception ex)
        {
            events?.Emit(new MutatingLaneFinished
            {
                LaneId = lane.Id, Kind = lane.Kind, Outcome = "error",
                Error = ex.Message, DurationMs = sw.ElapsedMilliseconds, AgentCommitted = false,
            });
            return new MutatingLaneResult { LaneId = lane.Id, Kind = lane.Kind,
                Error = ex.Message, CompletedUtc = DateTime.UtcNow, ElapsedMs = sw.ElapsedMilliseconds };
        }
        finally
        {
            // Clean up the lane worktree and scratch branch
            try { Git.WorktreeRemove(plan.Repo, lanePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try { Git.DeleteBranch(plan.Repo, scratchBranch); }
            catch { /* branch might already be merged/deleted */ }
        }
    }

    private static async Task<MergeGateOutcome> RunMergeGateAsync(
        PlanConfig plan, MutatingLaneConfig lane,
        string primaryRepo, string stagingPath, string stagingBranch,
        string scratchBranch, string baseBranch,
        Action<string>? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Create staging worktree from the base branch
        var createStaging = Git.Exec(primaryRepo, "worktree", "add", "-b", stagingBranch, stagingPath, baseBranch);
        if (createStaging.ExitCode != 0)
        {
            return new MergeGateOutcome
            {
                Passed = false, TotalGates = 0, PassedCount = 0, FailedCount = 0,
                FailureSummary = $"staging worktree creation failed: {createStaging.Output.Trim()}",
                DurationMs = sw.ElapsedMilliseconds,
            };
        }

        // Merge scratch into staging
        var mergeResult = Git.MergeBranch(stagingPath, scratchBranch);
        if (mergeResult.ExitCode != 0)
        {
            // Merge conflict — lane rejected
            return new MergeGateOutcome
            {
                Passed = false, TotalGates = 0, PassedCount = 0, FailedCount = 0,
                FailureSummary = $"merge conflict: {mergeResult.Output.Trim()}",
                DurationMs = sw.ElapsedMilliseconds,
            };
        }

        // Resolve the gates to run: lane.MergeGates or fall back to plan.Gates
        var gatesConfig = lane.MergeGates != null && lane.MergeGates.Count > 0
            ? lane.MergeGates
            : plan.Gates;

        if (gatesConfig.Count == 0)
        {
            // No gates configured — accept the merge trivially
            return new MergeGateOutcome
            {
                Passed = true, TotalGates = 0, PassedCount = 0, FailedCount = 0,
                DurationMs = sw.ElapsedMilliseconds,
            };
        }

        // Run the gates in the staging worktree. We create a synthetic PlanConfig
        // with Repo pointing to the staging path so GateRunner runs there.
        var stagingPlan = new PlanConfig
        {
            Repo = stagingPath,
            Gates = gatesConfig,
            Name = plan.Name,
        };

        List<GateResult> gateResults;
        try
        {
            gateResults = await GateRunner.RunAllAsync(stagingPlan, log, ct, currentStage: null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new MergeGateOutcome
            {
                Passed = false, TotalGates = gatesConfig.Count, PassedCount = 0,
                FailedCount = gatesConfig.Count,
                FailureSummary = $"gate execution failed: {ex.Message}",
                DurationMs = sw.ElapsedMilliseconds,
            };
        }

        var passedCount = gateResults.Count(g => g.Passed || g.Skipped);
        var failedCount = gateResults.Count(g => !g.Passed && !g.Skipped);
        var allPassed = GateRunner.AllRequiredPassed(gateResults);

        // Build summary for log/events
        var summary = allPassed ? null
            : GateRunner.Summary(gateResults) + "\n" + GateRunner.FailureDetails(gateResults, 2000);

        log?.Invoke($"merge gate for lane '{lane.Id}': {(allPassed ? "PASS" : "FAIL")} " +
                    $"({passedCount}/{gateResults.Count} passed, {failedCount} failed) in {sw.ElapsedMilliseconds}ms");

        return new MergeGateOutcome
        {
            Passed = allPassed,
            TotalGates = gateResults.Count,
            PassedCount = passedCount,
            FailedCount = failedCount,
            FailureSummary = summary,
            DurationMs = sw.ElapsedMilliseconds,
        };
    }
}

/// <summary>Result of running a Tier B mutating lane (B12.3).</summary>
public sealed class MutatingLaneResult
{
    public string LaneId { get; init; } = "";
    public string Kind { get; init; } = "delivery";
    /// <summary>True when the lane's scratch branch was successfully merged into the primary tree
    /// (merge gate passed). False when the merge gate failed, there was nothing to merge, or the
    /// lane itself errored.</summary>
    public bool Merged { get; init; }
    /// <summary>null = no merge gate was run (no commits, or lane error). true = gate passed.
    /// false = gate failed and the lane was rejected.</summary>
    public bool? MergeGatePassed { get; init; }
    /// <summary>Detailed merge gate stats when the gate ran.</summary>
    public MergeGateOutcome? MergeGate { get; init; }
    public string? AgentOutput { get; init; }
    public string? Error { get; init; }
    public DateTime CompletedUtc { get; init; }
    public long ElapsedMs { get; init; }
    public bool AgentCommitted { get; init; }
    public bool IsSuccess => Error == null && MergeGatePassed != false;
}

/// <summary>Outcome of a merge gate battery run for a Tier B lane (B12.3).</summary>
public sealed class MergeGateOutcome
{
    public bool Passed { get; init; }
    public int TotalGates { get; init; }
    public int PassedCount { get; init; }
    public int FailedCount { get; init; }
    public string? FailureSummary { get; init; }
    public long DurationMs { get; init; }
}
