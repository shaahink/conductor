using Conductor.Core;
using Conductor.Core.Orchestration;
using Conductor.Core.Planning;
using Conductor.Models;
using Conductor.Planning;

namespace Conductor.Commands;

/// <summary>
/// KS3.4 — the five legs that are not simply "doctor said so": what journey resolves, what the next
/// session's prompt composes to, whether a newer engine has been published, whether the engine
/// answering here is the one this source tree would build, and whether the tracker handoff is
/// already asking for a human.
/// <para>Every one of them is read-only, and that is a promise, not an aspiration: preflight creates
/// and moves nothing under <c>plan.StateDir</c>, spawns no agent, and runs no gate. An existing
/// <c>run.db</c> is opened READ-ONLY at rest for the work graph and the orphan peek
/// (<see cref="WorkSnapshot.ReadAtRest"/>, <see cref="CrashRecovery.ApplyOrphan"/> — no migration,
/// no WAL pragma, no write lock, and no <c>-shm</c>/<c>-wal</c> sidecar either: a cleanly-closed
/// database is opened <c>immutable</c>, see <c>SqliteRunStore.OpenReadOnly</c>); a plan with no
/// store yet never has one created. The one thing preflight touches off the machine is the release
/// feed, through the same six-hour user-level cache <c>doctor</c> uses.</para>
/// </summary>
public sealed partial class PreflightCommand
{
    // ───────────────────────────────────────────────────────────── journey

    /// <summary>What <c>conductor journey</c> is read for before a launch: the workflow and the MODEL
    /// that each stage will actually resolve. Two failures live here and nothing downstream can see
    /// either of them — a pinned <c>agent.model</c> that never reaches the CLI because the argv
    /// template carries no <c>{model}</c> (doctor's <c>model</c> check, reported here rather than
    /// twice), and a <c>workflow</c> name that resolves to nothing: <see cref="WorkflowEngine"/>
    /// falls back to <c>deliver-verify</c> for a name it does not know, silently, so a typo'd
    /// workflow runs a different lifecycle than the plan says while journey prints the fallback's
    /// chain as if it were the author's.</summary>
    internal static Leg JourneyLeg(PlanConfig plan, IReadOnlyList<DoctorCommand.Check> checks)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(checks);
        var mine = checks.Where(c => CheckOwner.TryGetValue(c.Name, out var o) && o == JourneyLegName).ToList();

        var resolver = new WorkflowEngine();
        var qa = new DefaultQaPolicy();
        var unresolved = new List<string>();
        foreach (var stage in plan.Stages)
        {
            if (UnresolvedWorkflowName(plan, stage, resolver, qa) is { Length: > 0 } wanted)
                unresolved.Add($"stage '{stage.Id}' declares workflow '{wanted}', which is neither built in nor " +
                               "declared in plan.workflows — it silently runs deliver-verify instead");
        }

        var models = plan.Stages.Select(s => plan.ResolveAgent(s).Model)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var modelText = models.Count == 0 ? "the agent CLI's own default model" : string.Join(", ", models);
        var headline = plan.Stages.Count == 0
            ? "the plan declares no stages"
            : $"{plan.Stages.Count} stage(s) resolve a workflow and a model ({modelText})";

        return FromChecks(JourneyLegName, mine, headline, unresolved,
            unresolved.Count > 0 ? "fail" : "ok");
    }

    /// <summary>The workflow name a stage asks for and nothing answers, or null when it resolves.
    /// Asked by NAME rather than by comparing objects: the QA dial can project a name of its own
    /// (P2), the plan can declare its own definitions, and a built-in answers with a definition whose
    /// <c>Name</c> is the name that was asked for.</summary>
    private static string? UnresolvedWorkflowName(PlanConfig plan, StageConfig stage, IWorkflowResolver resolver, IQaPolicy qa)
    {
        var wanted = qa.Project(plan, stage, null).WorkflowName ?? stage.Workflow ?? plan.DefaultWorkflow;
        if (string.IsNullOrWhiteSpace(wanted)) return null;
        if (plan.Workflows is { } custom && custom.ContainsKey(wanted)) return null;
        var resolved = resolver.Resolve(wanted, null, plan.Workflows);
        return string.Equals(resolved.Name, wanted, StringComparison.OrdinalIgnoreCase) ? null : wanted;
    }

    // ───────────────────────────────────────────────────────────── version

    /// <summary>Running version against the release feed. Doctor reports a newer release as a
    /// <c>warn</c>, which is right for a health check and wrong for a launch drill: starting a
    /// multi-day run on an engine that has already been superseded is a decision, and preflight makes
    /// you take it deliberately (<c>conductor update</c>, or <c>--no-update-check</c>).
    /// <para>Only an ACTUALLY newer release fails. An unreachable feed, a disabled check and a
    /// non-semver local build all stay green — an offline machine is not a broken one, and this leg
    /// must never be the reason a run cannot start on a plane.</para></summary>
    internal static async Task<Leg> VersionLegAsync(bool updateCheck, DateTimeOffset now)
    {
        if (!updateCheck)
            return new Leg(VersionLegName, "ok",
                $"{BuildInfo.Current.Full} — release feed not consulted (--no-update-check)", []);

        var (check, status) = await DoctorCommand.UpdateStatusAsync(now).ConfigureAwait(false);
        return VersionLeg(check, status);
    }

    /// <summary>The rule, with the probe already done. Separated so the verdict can be asserted
    /// against a stated release rather than against whatever GitHub happens to be serving — a test
    /// whose result depends on a live feed measures the feed.</summary>
    internal static Leg VersionLeg(DoctorCommand.Check check, Conductor.Core.Update.UpdateStatus? status)
    {
        ArgumentNullException.ThrowIfNull(check);
        return status is { Available: true }
            ? new Leg(VersionLegName, "fail", check.Message, [])
            : new Leg(VersionLegName, check.State == "fail" ? "warn" : check.State, check.Message, []);
    }

    // ───────────────────────────────────────────────────────────── escalation

    /// <summary>The escalation-block check. The token is matched as a plain SUBSTRING of the tracker's
    /// handoff block, so a handoff that still carries one parks the very first session at NeedsHuman
    /// before an agent is spawned — the run looks started, spends nothing, and waits. It is the
    /// cheapest launch failure to cause and the most expensive to notice, because the surface that
    /// reports it is the one you walked away from.
    /// <para>Extracted with <see cref="ProgressConventions.BuildHandoffRegex"/> — the same regex
    /// <see cref="MarkdownTableProvider"/> uses — read off the tracker FILE, so the answer is the same
    /// under any <c>progress.kind</c>. Carries KS1.4's <c>escalation</c> sweep of stage notes,
    /// promptExtra and templates: one implementation, two places it can hurt.</para>
    /// <para>Neither this code, its message nor its tests ever spell the token: it is read from
    /// <c>plan.conventions.humanToken</c>, because a drill that printed it into a handoff would be
    /// the failure it exists to catch.</para></summary>
    internal static async Task<Leg> EscalationLegAsync(PlanConfig plan, IReadOnlyList<DoctorCommand.Check> checks)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(checks);
        var mine = checks.Where(c => CheckOwner.TryGetValue(c.Name, out var o) && o == EscalationLegName).ToList();

        var token = plan.Conventions.HumanToken;
        if (string.IsNullOrEmpty(token))
            return FromChecks(EscalationLegName, mine, "conventions.humanToken is empty — nothing can park this run by prose");

        string tracker;
        try { tracker = await File.ReadAllTextAsync(plan.TrackerPath).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FromChecks(EscalationLegName, mine,
                $"{plan.Tracker} could not be read, so the handoff block was not inspected", [ex.Message], "warn");
        }

        var match = plan.Conventions.BuildHandoffRegex().Match(tracker);
        var handoff = match.Success ? match.Groups["body"].Value.Trim() : "";
        if (handoff.Length == 0)
            return FromChecks(EscalationLegName, mine,
                $"{plan.Tracker} has no handoff block under '{plan.Conventions.HandoffMarker}' — nothing there asks for a human");

        return plan.Conventions.MentionsHuman(handoff)
            ? FromChecks(EscalationLegName, mine,
                $"the handoff block of {plan.Tracker} already asks for a human (conventions.humanToken)",
                ["the first session parks at NeedsHuman before an agent is spawned — clear the request from the handoff, " +
                 "or resolve it and `conductor resume` into the existing run"],
                "fail")
            : FromChecks(EscalationLegName, mine,
                $"the handoff block of {plan.Tracker} carries no escalation request — session one will spawn");
    }
}
