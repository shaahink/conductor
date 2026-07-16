using System.Globalization;

namespace Conductor.Planning;

/// <summary>
/// Owns the declarative workflow lifecycle: resolves the effective workflow definition, walks steps
/// with RunIf / SkipIf conditionals, and records the step index. Replaces the hardcoded
/// Deliver→Verify→Fix state machine (M3.1–M3.2). Moved from the engine assembly in P0 — it is a pure
/// decision component (data in, decisions out, no IO), the default <see cref="IWorkflowResolver"/>.
/// </summary>
public sealed class WorkflowEngine : IWorkflowResolver
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

    // ── public API (IWorkflowResolver) ──

    /// <summary>Resolve the effective workflow: the stage-level name wins, then the plan default,
    /// then the built-in "deliver-verify". Agnostic since P0 — takes the names + custom definitions,
    /// not the engine's PlanConfig (the engine has a thin Resolve(plan, stage) extension).</summary>
    public WorkflowDefinition Resolve(string? stageWorkflow, string? defaultWorkflow,
        IReadOnlyDictionary<string, WorkflowDefinition>? customWorkflows)
    {
        var name = stageWorkflow ?? defaultWorkflow ?? "deliver-verify";
        if (_builtIns.TryGetValue(name, out var builtIn))
            return builtIn;

        if (customWorkflows != null && customWorkflows.TryGetValue(name, out var customDef))
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
    /// <paramref name="stepIndices"/> in the same call. Both resolution call sites (the engine's
    /// SessionRunner resolving an upcoming session's kind, and its VerdictEngine deciding what comes
    /// after one that just finished) need "the next step, with the index kept in sync" — having two
    /// independent call sites each read <paramref name="stepIndices"/>, compute a step, and
    /// separately write the index back is exactly how they drifted out of sync (a real bug:
    /// SessionRunner's own resolution never wrote the index back, so the entry permanently lagged
    /// one step behind after a stage's first session, and the next PendingVerify/PendingAudit/
    /// PendingFix was never populated even though the session's kind still resolved correctly by
    /// coincidence of the same lag — an NRE in PromptBuilder.Verify/Audit/Fix). Single source of
    /// truth instead of mirrored bookkeeping. Removes the dictionary entry and returns null when
    /// the workflow is exhausted.</summary>
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

    /// <summary>P4: the post-session advance decision — see <see cref="IWorkflowResolver.Advance"/>.
    /// Behavior-identical to the engine's former recursion: each skipped verification re-evaluates
    /// with verifier.passed = true and every other fact unchanged.</summary>
    public WorkflowAdvance Advance(WorkflowDefinition workflow, Dictionary<string, int> stepIndices,
        string stageId, WorkflowRuntimeVars vars, bool skipVerification)
    {
        var hops = new List<WorkflowHop>();
        var current = vars;
        while (true)
        {
            var from = stepIndices.GetValueOrDefault(stageId, -1);
            var step = ResolveAndRecordStep(workflow, stepIndices, stageId, current);
            if (step == null)
                return new WorkflowAdvance { Next = null, Hops = hops, ExhaustedFromIndex = from };

            var to = stepIndices[stageId];
            if (step.Kind == SessionKind.Verify && skipVerification)
            {
                hops.Add(new WorkflowHop(from, to, step, SkippedAsPassed: true));
                current = WithVerifierPassed(current);
                continue;
            }
            hops.Add(new WorkflowHop(from, to, step, SkippedAsPassed: false));
            return new WorkflowAdvance { Next = step, Hops = hops };
        }
    }

    /// <summary>P4: the session-start kind decision — see
    /// <see cref="IWorkflowResolver.ResolveStartKind"/>.</summary>
    public SessionKind ResolveStartKind(WorkflowDefinition workflow, Dictionary<string, int> stepIndices,
        string stageId, bool skipVerification)
    {
        // A recorded index IS this session's step — consume it without advancing. Resolving again
        // here double-stepped the workflow onto a verify no advance had populated context for.
        WorkflowStep? step;
        if (stepIndices.TryGetValue(stageId, out var recorded) && recorded >= 0 && recorded < workflow.Steps.Count)
            step = workflow.Steps[recorded];
        else
            step = ResolveAndRecordStep(workflow, stepIndices, stageId, new WorkflowRuntimeVars());

        if (step == null) return SessionKind.Deliver; // exhausted or not configured
        if (step.Kind == SessionKind.Verify && skipVerification) return SessionKind.Deliver;
        return step.Kind;
    }

    private static WorkflowRuntimeVars WithVerifierPassed(WorkflowRuntimeVars vars) => new()
    {
        VerifierScore = vars.VerifierScore,
        VerifierPassed = true,
        CircuitBroken = vars.CircuitBroken,
        StageAttempts = vars.StageAttempts,
        GatesGreen = vars.GatesGreen,
        HasCommits = vars.HasCommits,
        Stalled = vars.Stalled,
        NewlyDoneCount = vars.NewlyDoneCount,
        StageComplete = vars.StageComplete,
    };

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

    private static double? ResolveNumeric(string name, WorkflowRuntimeVars vars)
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
}
