using System.Globalization;
using Conductor.Models;

namespace Conductor.Core.Orchestration;

/// <summary>
/// Owns the declarative workflow lifecycle: reads workflow definitions from the plan,
/// resolves which step to run next based on the prior session's outcome, and evaluates
/// RunIf / SkipIf conditionals. Replaces the hardcoded Deliver→Verify→Fix state machine
/// (M3.1–M3.2).
/// </summary>
public sealed class WorkflowEngine
{
    private readonly Dictionary<string, WorkflowDefinition> _builtIns;

    public WorkflowEngine()
    {
        _builtIns = BuildBuiltIns();
    }

    // ── built-in workflow definitions ──

    private static Dictionary<string, WorkflowDefinition> BuildBuiltIns()
    {
        var dict = new Dictionary<string, WorkflowDefinition>(StringComparer.OrdinalIgnoreCase);

        // deliver-verify: classic cycle (default). Deliver → Verify → (if fail) Fix → loop.
        dict["deliver-verify"] = new WorkflowDefinition
        {
            Name = "deliver-verify",
            Description = "Deliver, verify independently, fix if needed. The default for most stages.",
            Repeat = true,
            Steps =
            [
                new WorkflowStep { Id = "deliver", Kind = SessionKind.Deliver, Deliver = true },
                new WorkflowStep { Id = "verify", Kind = SessionKind.Verify, Deliver = false },
                new WorkflowStep
                {
                    Id = "fix-if-needed",
                    Kind = SessionKind.Fix,
                    Deliver = true,
                    RunIf = "!verifier.passed",
                },
            ],
        };

        // big-dev-then-big-audit: several delivery sessions, then one audit → fix sweep.
        dict["big-dev-then-big-audit"] = new WorkflowDefinition
        {
            Name = "big-dev-then-big-audit",
            Description = "Deliver repeatedly, then one consolidated audit + fix sweep. Prefer when you want the agent to build momentum before QA.",
            Repeat = false,
            Steps =
            [
                new WorkflowStep { Id = "deliver", Kind = SessionKind.Deliver, Deliver = true },
                // Runs until stage is complete (Repeat=false means steps loop within the step list
                // until the step's RunIf/SkipIf conditions stop it)
                new WorkflowStep { Id = "audit", Kind = SessionKind.Audit, Deliver = false },
                new WorkflowStep
                {
                    Id = "fix-sweep",
                    Kind = SessionKind.Fix,
                    Deliver = true,
                    RunIf = "newlyDoneCount == 0",
                },
                new WorkflowStep
                {
                    Id = "deliver-final",
                    Kind = SessionKind.Deliver,
                    Deliver = true,
                },
            ],
        };

        // docs-only: zero dotnet gates, no verification, no test expectations.
        dict["docs-only"] = new WorkflowDefinition
        {
            Name = "docs-only",
            Description = "Documentation-only stage. Zero dotnet gates, skip verification, no commit required.",
            Repeat = false,
            Steps =
            [
                new WorkflowStep { Id = "deliver-docs", Kind = SessionKind.Deliver, Deliver = true },
            ],
        };

        // spike: exploratory work — no QA, no commit expectation.
        dict["spike"] = new WorkflowDefinition
        {
            Name = "spike",
            Description = "Exploratory spike. No QA, no gates, no commit required.",
            Repeat = false,
            Steps =
            [
                new WorkflowStep { Id = "spike", Kind = SessionKind.Deliver, Deliver = true },
            ],
        };

        return dict;
    }

    // ── public API ──

    /// <summary>Resolve the effective workflow for a stage: stage-level override wins,
    /// then plan default, then the built-in "deliver-verify".</summary>
    public WorkflowDefinition Resolve(PlanConfig plan, StageConfig stage)
    {
        var name = stage.Workflow ?? plan.DefaultWorkflow ?? "deliver-verify";
        if (_builtIns.TryGetValue(name, out var builtIn))
            return builtIn;

        if (plan.Workflows is { } custom && custom.TryGetValue(name, out var customDef))
            return customDef;

        return _builtIns["deliver-verify"];
    }

    /// <summary>Determine the next step to execute given the current step index
    /// and the previous session's runtime variables. Returns null when the workflow
    /// is exhausted (stage complete).</summary>
    public WorkflowStep? GetNextStep(
        WorkflowDefinition workflow,
        int currentStepIndex,
        WorkflowRuntimeVars vars)
    {
        if (workflow.Steps.Count == 0)
            return null;

        // Advance from currentStepIndex, evaluating conditionals
        var next = FindNextRunnableStep(workflow, currentStepIndex, vars);
        if (next.step != null)
            return next.step;

        // No more runnable steps in this iteration
        if (!workflow.Repeat)
            return null; // one-shot — stage finished

        // Repeat: wrap around to step 0 and try again
        next = FindNextRunnableStep(workflow, -1, vars);
        return next.step;
    }

    /// <summary>Resolve the next step for a stage AND durably record its index in
    /// <paramref name="stepIndices"/> in the same call. Both SessionRunner (resolving the kind of
    /// an upcoming session) and VerdictEngine (deciding what comes after one that just finished)
    /// need "the next step, with the index kept in sync" — having two independent call sites each
    /// read <paramref name="stepIndices"/>, compute a step, and separately write the index back is
    /// exactly how they drifted out of sync (a real bug: SessionRunner's own resolution never
    /// wrote the index back, so the entry permanently lagged one step behind after a stage's first
    /// session, and the next PendingVerify/PendingAudit/PendingFix was never populated even though
    /// the session's kind still resolved correctly by coincidence of the same lag — an NRE in
    /// PromptBuilder.Verify/Audit/Fix). Single source of truth instead of mirrored bookkeeping.
    /// Removes the dictionary entry and returns null when the workflow is exhausted.</summary>
    public WorkflowStep? ResolveAndRecordStep(
        WorkflowDefinition workflow,
        Dictionary<string, int> stepIndices,
        string stageId,
        WorkflowRuntimeVars vars)
    {
        var stepIndex = stepIndices.GetValueOrDefault(stageId, -1);
        var step = GetNextStep(workflow, stepIndex, vars);
        if (step == null)
        {
            stepIndices.Remove(stageId);
            return null;
        }

        var resolvedIndex = workflow.Steps.FindIndex(s => s.Id == step.Id);
        stepIndices[stageId] = resolvedIndex >= 0 ? resolvedIndex : stepIndex + 1;
        return step;
    }

    /// <summary>Evaluate a RunIf / SkipIf expression against runtime variables.
    /// Supported expressions are simple boolean/logic: "!verifier.passed",
    /// "verifier.score >= 80", "circuit.broken", "newlyDoneCount > 0".</summary>
    public bool EvaluateCondition(string expr, WorkflowRuntimeVars vars)
    {
        expr = expr.Trim();

        // Handle !prefix (negation)
        if (expr.StartsWith('!'))
            return !EvaluateCondition(expr[1..], vars);

        // Numeric comparisons
        if (TryEvalNumericCompare(expr, vars, out var result))
            return result;

        // Simple boolean variables
        return expr switch
        {
            "verifier.passed" => vars.VerifierPassed,
            "circuit.broken" => vars.CircuitBroken,
            "gatesGreen" => vars.GatesGreen,
            "hasCommits" => vars.HasCommits,
            "stalled" => vars.Stalled,
            "stageComplete" => vars.StageComplete,
            _ => true, // unknown expression → treat as true (permissive)
        };
    }

    private bool TryEvalNumericCompare(string expr, WorkflowRuntimeVars vars, out bool result)
    {
        result = false;

        foreach (var op in new[] { ">=", "<=", ">", "<", "==", "!=" })
        {
            var idx = expr.IndexOf(op, StringComparison.Ordinal);
            if (idx < 0) continue;

            var left = expr[..idx].Trim();
            var right = expr[(idx + op.Length)..].Trim();

            var leftVal = ResolveNumeric(left, vars);
            if (leftVal is not { } lv) return false;

            if (!double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var rv))
                return false;

            result = op switch
            {
                ">=" => lv >= rv,
                "<=" => lv <= rv,
                ">" => lv > rv,
                "<" => lv < rv,
                "==" => Math.Abs(lv - rv) < 0.001,
                "!=" => Math.Abs(lv - rv) > 0.001,
                _ => false,
            };
            return true;
        }

        return false;
    }

    private double? ResolveNumeric(string name, WorkflowRuntimeVars vars)
    {
        return name switch
        {
            "verifier.score" => vars.VerifierScore,
            "stage.attempts" => vars.StageAttempts,
            "newlyDoneCount" => vars.NewlyDoneCount,
            _ => null,
        };
    }

    private (WorkflowStep? step, int index) FindNextRunnableStep(
        WorkflowDefinition workflow,
        int startIndex,
        WorkflowRuntimeVars vars)
    {
        for (var i = startIndex + 1; i < workflow.Steps.Count; i++)
        {
            var step = workflow.Steps[i];

            if (step.SkipIf is { } skipIf && EvaluateCondition(skipIf, vars))
                continue;

            if (step.RunIf is { } runIf && !EvaluateCondition(runIf, vars))
                continue;

            return (step, i);
        }

        return (null, -1);
    }

    /// <summary>Get the initial step (first that passes conditionals).</summary>
    public WorkflowStep? GetInitialStep(WorkflowDefinition workflow, WorkflowRuntimeVars vars)
    {
        return GetNextStep(workflow, -1, vars);
    }

    /// <summary>Build runtime vars from a SessionRecord and VerdictEngine state after
    /// EvaluateSessionAsync runs. Called by the engine before resolving the next step.</summary>
    public WorkflowRuntimeVars BuildRuntimeVars(
        SessionRecord rec,
        int stageAttempts,
        bool gatesGreen,
        int? verifierScore,
        bool verifierPassed,
        bool circuitBroken,
        bool stageComplete)
    {
        return new WorkflowRuntimeVars
        {
            VerifierScore = verifierScore,
            VerifierPassed = verifierPassed,
            CircuitBroken = circuitBroken,
            StageAttempts = stageAttempts,
            GatesGreen = gatesGreen,
            HasCommits = rec.NewCommits is { Count: > 0 },
            Stalled = rec.Outcome is SessionOutcome.Stalled or SessionOutcome.TimedOut,
            NewlyDoneCount = rec.NewlyDone?.Count ?? 0,
            StageComplete = stageComplete,
        };
    }
}
