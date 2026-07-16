using System.Text.Json;
using System.Text.Json.Serialization;
using Conductor.Planning;
using PlanLint;

// plan-lint (P4): the standalone consumer proving Conductor.Planning stands alone. It reads a plan
// file with nothing but System.Text.Json + the library's own rule types, and prints the decisions
// the library resolves — effective workflow, QA projection, role assignments, session-start kind —
// with zero dependency on the engine, its store, or its HTTP surface.

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("usage: plan-lint <plan.json> [stageId]");
    Console.WriteLine("Prints the planning library's resolved decisions for each stage (or one stage).");
    return 2;
}

var planPath = Path.GetFullPath(args[0]);
if (!File.Exists(planPath))
{
    Console.WriteLine($"plan-lint: plan file not found: {planPath}");
    return 2;
}

// The same reading conventions the plan format documents (camelCase, comments, trailing commas,
// enums as strings) — declared locally; the format is JSON, not an engine type.
var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
};

PlanLite plan;
try
{
    plan = JsonSerializer.Deserialize<PlanLite>(await File.ReadAllTextAsync(planPath).ConfigureAwait(false), jsonOptions) ?? new PlanLite();
}
catch (JsonException ex)
{
    Console.WriteLine($"plan-lint: not a valid plan JSON: {ex.Message}");
    return 2;
}

var stageFilter = args.Length > 1 ? args[1] : null;
var stages = plan.Stages.Where(s =>
    stageFilter is null || string.Equals(s.Id, stageFilter, StringComparison.OrdinalIgnoreCase)).ToList();
if (stages.Count == 0)
{
    Console.WriteLine(stageFilter is null
        ? "plan-lint: the plan declares no stages"
        : $"plan-lint: no stage '{stageFilter}' in {plan.Name ?? planPath}");
    return 2;
}

var resolver = new WorkflowEngine();
var qaPolicy = new DefaultQaPolicy();
var assigner = new DefaultAssignmentPolicy();

Console.WriteLine($"plan: {plan.Name ?? Path.GetFileName(planPath)}  (pipeline rules: {(plan.Pipeline is null ? "none — classic behavior" : "present")})");
foreach (var stage in stages)
{
    var qa = qaPolicy.Project(plan.Pipeline?.Qa, stage.Qa);
    var workflow = resolver.Resolve(qa.WorkflowName ?? stage.Workflow, plan.DefaultWorkflow, plan.Workflows);
    // The engine's effective-skip rule, reproduced from pure library pieces: the dial owns the
    // answer when set; otherwise the per-stage override decides.
    var skipVerification = qa.SkipVerification ?? stage.Overrides?.SkipVerification == true;
    var startKind = resolver.ResolveStartKind(workflow, new Dictionary<string, int>(StringComparer.Ordinal), stage.Id, skipVerification);

    Console.WriteLine();
    Console.WriteLine($"stage {stage.Id} — {stage.Title}");
    Console.WriteLine($"  workflow:   {workflow.Name}{(workflow.Repeat ? " (repeats)" : "")} — {DescribeSteps(workflow)}");
    Console.WriteLine($"  qa:         {DescribeQa(plan.Pipeline?.Qa, stage.Qa, qa)}");
    Console.WriteLine($"  start kind: {startKind}{(skipVerification ? "  (verification skipped)" : "")}");
    Console.WriteLine($"  assignment: {DescribeMultiItem(plan.Pipeline?.MultiItem)}");
    foreach (var kind in new[] { SessionKind.Deliver, SessionKind.Verify, SessionKind.Audit, SessionKind.Fix })
    {
        var assignment = assigner.Assign(plan.Pipeline, kind,
            [new ReadyItem { Id = $"{stage.Id}.1", Title = "(synthetic ready item)" }], claimedPaths: null);
        Console.WriteLine($"    {kind,-7} → {DescribeAgent(assignment)}");
    }
}
return 0;

static string DescribeSteps(WorkflowDefinition workflow) =>
    string.Join(" → ", workflow.Steps.Select(s =>
        s.Id + (s.RunIf != null ? $" [if {s.RunIf}]" : "") + (s.SkipIf != null ? $" [skip if {s.SkipIf}]" : "")));

static string DescribeQa(QaRule? planRule, QaRule? stageRule, QaProjection qa)
{
    if (planRule is null && stageRule is null) return "classic — the stage workflow decides";
    var source = stageRule != null ? "stage dial" : "plan dial";
    return $"{source} → workflow={qa.WorkflowName ?? "(classic)"}, skipVerification={qa.SkipVerification?.ToString() ?? "(overrides decide)"}, " +
           $"threshold={qa.VerifierThreshold?.ToString() ?? "(limits default)"}, auditCoversPriorSessions={qa.AuditCoversPriorSessions}";
}

static string DescribeAgent(SessionAssignment assignment)
{
    if (assignment.Model is null && assignment.Persona is null && assignment.Command is null)
        return "stage/plan default agent";
    var parts = new List<string>();
    if (assignment.Command != null) parts.Add($"command={assignment.Command}");
    if (assignment.Model != null) parts.Add($"model={assignment.Model}");
    if (assignment.Persona != null) parts.Add($"persona={assignment.Persona}");
    return string.Join(", ", parts);
}

static string DescribeMultiItem(MultiItemRule? multi) =>
    multi is { Enabled: true }
        ? $"multi-item sessions enabled (max {multi.MaxItems})"
        : "one item per session (classic)";
