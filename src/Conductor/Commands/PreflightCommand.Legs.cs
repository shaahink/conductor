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
/// and moves nothing under <c>plan.StateDir</c>, spawns no agent, and runs no gate. The one thing it
/// touches off the machine is the release feed, through the same six-hour user-level cache
/// <c>doctor</c> uses.</para>
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

    // ───────────────────────────────────────────────────────────── compose

    /// <summary>The <c>run --dry-run</c> leg: the prompt the NEXT session would be spawned with,
    /// composed and measured, with nothing spawned and nothing saved. Carries doctor's three
    /// prompt-side lints (<c>prompt</c>, <c>templates</c>, <c>argv</c>) because they answer the same
    /// question one stage earlier — will this compose at all, and will it fit in an argv.
    /// <para>Which session is "next" is not re-decided here, in any of its three parts: the resume peek
    /// is <see cref="JourneyCommand.PeekResumeAsync"/> (state.json, then the run.db row, read-only), the
    /// STAGE is <see cref="StageSelection"/> — the run loop's own selector, not a copy of it — and the
    /// kind is chosen by the same precedence <c>RunLoop</c>'s dry-run branch uses: resume, then audit,
    /// then fix, then the stage's own delivery kind.</para></summary>
    internal static async Task<Leg> ComposeLegAsync(PlanConfig plan, IReadOnlyList<DoctorCommand.Check> checks)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(checks);
        var mine = checks.Where(c => CheckOwner.TryGetValue(c.Name, out var o) && o == ComposeLegName).ToList();

        if (plan.Stages.Count == 0)
            return FromChecks(ComposeLegName, mine, "the plan declares no stages, so no session composes");

        var state = await JourneyCommand.PeekResumeAsync(plan).ConfigureAwait(false);
        var track = SafeReadWork(plan);
        var stage = NextStage(plan, state, track);
        if (stage is null)
            return StageSelection.AllEffectivelyDone(plan, state, track)
                ? FromChecks(ComposeLegName, mine,
                    "every stage reads done — the next `conductor run` confirms completion rather than spawning a session")
                // RunLoop's own answer to this is NeedsHuman before a session: the run starts, parks and
                // spends nothing. That is a launch failure, and it is one only this leg can see.
                : FromChecks(ComposeLegName, mine,
                    "no stage is runnable — every stage left is skipped, or blocked by a `dependsOn` that is neither done nor skipped",
                    ["`conductor run` would park at NeedsHuman before spawning anything — review the dependsOn chain " +
                     "and state.skippedStages"],
                    "fail");

        var kind = NextKind(state, stage);
        try
        {
            var prompt = Compose(plan, stage, state, kind);
            return FromChecks(ComposeLegName, mine,
                $"next session #{state.SessionCounter + 1} is {kind} on stage '{stage.Id}', " +
                $"composing to {prompt.Text.Length} chars (nothing spawned)",
                KnowledgeBatteryCaveat(plan, state, prompt));
        }
        catch (PromptCompositionException ex)
        {
            // Doctor's own prompt lint usually names the same template first; saying it twice under
            // one leg is noise, so the refusal is only spelled out when nothing else already did.
            var already = mine.Exists(c => c.State == "fail");
            return FromChecks(ComposeLegName, mine,
                $"the prompt for the next session ({kind} on stage '{stage.Id}') is REFUSED — nothing would spawn",
                already ? [] : [ex.Message], "fail");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FromChecks(ComposeLegName, mine,
                $"the prompt for the next session ({kind} on stage '{stage.Id}') could not be read",
                [ex.Message], "warn");
        }
    }

    /// <summary>The stage a launch would land on — <see cref="StageSelection"/>'s answer, which is
    /// <see cref="RunLoop"/>'s answer, because it is the same code. Null when nothing is runnable:
    /// either everything is done and owed nothing, or what remains is skipped or blocked.
    /// <para>It was briefly a second implementation here ("the stage state is in, else the first the
    /// tracker does not read done") and that copy ignored <c>state.skippedStages</c>, stage
    /// <c>dependsOn</c> and — under <c>perPhaseGates</c> — <c>state.confirmedStages</c>. It therefore
    /// named a different stage than <c>run --dry-run</c> named for the same plan, and measured a
    /// different stage's prompt: different notes, different promptExtra, different templates. On the
    /// one surface whose entire purpose is truth before launch.</para></summary>
    internal static StageConfig? NextStage(PlanConfig plan, RunState state, TrackerSnapshot track)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(track);
        // The loop's own shape: completion is only confirmed when nothing is still owed; a queued
        // resume/audit/fix/verify runs on the standing stage even when every row reads done.
        if (StageSelection.AllEffectivelyDone(plan, state, track))
            return StageSelection.OwesASession(state) ? StageSelection.Standing(plan, state) : null;
        return StageSelection.Select(plan, state, track);
    }

    /// <summary>The session kind the loop would pick, in the loop's own order (RunLoop's dry-run
    /// branch): a queued resume, then a queued audit, then a queued fix, then delivery — where a
    /// <c>review</c> stage delivers through the review template.</summary>
    internal static SessionKind NextKind(RunState state, StageConfig stage)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(stage);
        if (state.PendingResume is not null) return SessionKind.Resume;
        if (state.PendingAudit is not null) return SessionKind.Audit;
        if (state.PendingFix is not null) return SessionKind.Fix;
        return SessionKind.Deliver;
    }

    /// <summary>Renders through the real <see cref="PromptBuilder"/> — the one the run loop uses —
    /// with the run's own session/attempt numbers, and then appends the battery section exactly as
    /// <c>RunLoop</c>'s dry-run branch and <c>SessionRunner</c> do. The batteries are not decoration:
    /// a recorded gate failure comes back as a whole section, and this leg carries doctor's
    /// <c>argv</c> check precisely because prompt length is the 8191-char trap — a measurement taken
    /// before the batteries were added would understate the number that matters.
    /// <para>One honest gap, and it is stated in the leg's detail rather than hidden: the M7 knowledge
    /// batteries (the ledger, the run's open bugs) are read from <c>run.db</c>, and the store opens
    /// read-write — it migrates the schema and drops a WAL sidecar. Preflight promises to write
    /// nothing, so it passes no store and reports the BOUND instead, which is exact because the whole
    /// battery section is capped at <c>batteries.maxBytes</c> — see
    /// <see cref="KnowledgeBatteryCaveat"/>.</para></summary>
    private static ComposedPrompt Compose(PlanConfig plan, StageConfig stage, RunState state, SessionKind kind)
    {
        var prompts = new PromptBuilder(plan);
        var session = state.SessionCounter + 1;
        var attempt = state.NextAttemptNumber;
        var maxAttempts = Math.Max(1, stage.Sessions * plan.Limits.StageSlackFactor);
        var isReview = stage.Kind.Equals("review", StringComparison.OrdinalIgnoreCase);
        var prompt = kind switch
        {
            SessionKind.Resume => prompts.Resume(stage, session, attempt, maxAttempts, state.PendingResume!),
            SessionKind.Audit => prompts.Audit(stage, session, state.PendingAudit!, state.CurrentStageStartHead ?? "HEAD~1"),
            SessionKind.Fix => prompts.Fix(stage, session, attempt, maxAttempts, state.PendingFix!),
            _ => isReview
                ? prompts.Review(stage, session, attempt, maxAttempts,
                    Path.Combine(plan.StateDir, "reviews", $"{stage.Id}.md"))
                : prompts.Deliver(stage, session, attempt, maxAttempts),
        };
        // store: null — everything the state itself carries (the recent-failure digest, lessons, lane
        // artifacts) is measured; the two store-backed batteries are bounded in the detail line.
        var battery = prompts.BatterySection(state, store: null);
        return new ComposedPrompt(prompt, battery.Length > 0 ? prompt.TrimEnd() + "\n\n" + battery : prompt);
    }

    /// <summary>The prompt as the loop builds it, in two pieces: the template render on its own
    /// (<paramref name="Core"/>) and the same thing with the battery section appended
    /// (<paramref name="Text"/>). The pair exists because the reported length is the second and the
    /// unmeasured-battery ceiling is derived from the FIRST — a ceiling measured from a string that
    /// already contains batteries would double-count them.</summary>
    private sealed record ComposedPrompt(string Core, string Text);

    /// <summary>What the measured length does NOT include, said as a number rather than as a hedge.
    /// The WHOLE battery section — knowledge batteries included — is capped at <c>batteries.maxBytes</c>
    /// (2048 by default) by <c>BatteryGroup.Render</c>, plus at most two characters for whichever
    /// truncation tail it appends. So the argv the agent actually sees is at most the bare prompt plus
    /// that cap: a ceiling, which is the direction that matters when the risk is 8191.
    /// <para>Derived from the prompt WITHOUT batteries, because the cap covers the section as a whole —
    /// adding it to a string that already carries the measured batteries would count them twice.</para>
    /// <para>Silent on a fresh run, on a plan whose store does not exist yet, and when both knowledge
    /// batteries are switched off: there is nothing unmeasured to warn about.</para></summary>
    private static IReadOnlyList<string> KnowledgeBatteryCaveat(PlanConfig plan, RunState state, ComposedPrompt composed)
    {
        var cfg = plan.Batteries;
        var knowledgeOn = (cfg?.Ledger ?? true) || (cfg?.Bugs ?? true);
        if (!knowledgeOn || state.RunId.Length == 0 || !File.Exists(plan.RunDbPath)) return [];

        var maxBytes = cfg?.MaxBytes ?? 2048;
        var ceiling = composed.Core.TrimEnd().Length + 2 + maxBytes + 2;
        return
        [
            $"the ledger and open-bug batteries are read from run.db when the session spawns; this drill does not " +
            $"open that store (the store opens read-write — schema migration and a WAL sidecar — and preflight " +
            $"writes nothing), so the composed argv is at most {ceiling} chars (batteries.maxBytes {maxBytes})",
        ];
    }

    private static TrackerSnapshot SafeReadWork(PlanConfig plan)
    {
        try { return ProgressProviderFactory.Create(plan).Read(plan, CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new TrackerSnapshot();
        }
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
