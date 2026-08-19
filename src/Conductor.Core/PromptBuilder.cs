using Conductor.Core.Orchestration;
using Conductor.Core.Store;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// Renders session prompts from md templates in <plan-dir>/<templatesDir>/,
/// falling back to built-in defaults when a template file is absent.
/// Placeholders: {name} replaced verbatim.
/// </summary>
public sealed partial class PromptBuilder
{
    private readonly PlanConfig _plan;
    private readonly PersonaRegistry _personas;
    private readonly LessonsManager _lessons;
    private readonly IQaPolicy _qa;

    public PromptBuilder(PlanConfig plan, PersonaRegistry? personaRegistry = null, LessonsManager? lessons = null, IQaPolicy? qa = null)
    {
        _plan = plan;
        _personas = personaRegistry ?? new PersonaRegistry(plan);
        _lessons = lessons ?? new LessonsManager(plan.StateDir);
        _qa = qa ?? new DefaultQaPolicy();
    }
    public string Deliver(StageConfig stage, int sessionNumber, int attempt, int maxAttempts, string? personaOverride = null)
        => Render("session.md", Vars(stage, sessionNumber, attempt, maxAttempts, personaOverride));

    public string Fix(StageConfig stage, int sessionNumber, int attempt, int maxAttempts, PendingFix fix, string? personaOverride = null)
    {
        var vars = Vars(stage, sessionNumber, attempt, maxAttempts, personaOverride);
        var gateFailures = string.IsNullOrWhiteSpace(fix.GateFailures) ? "(no gate output captured)" : fix.GateFailures;
        // SC4.4: a fix prompt is the one place where a human correction and the engine's own evidence
        // are both present, and they used to arrive as peers — the gate output at the top, the
        // injection at the bottom. When anything is queued for this session the evidence is stamped
        // as outranked, so the agent cannot read the two and pick the nearer one.
        var queuedCount = InstructionQueue.List(_plan).Count;
        if (queuedCount > 0)
            gateFailures = InstructionQueue.SupersedeStamp(queuedCount) + "\n\n" + gateFailures;
        vars["gateFailures"] = gateFailures;
        vars["progressSummary"] = fix.ProgressSummary;
        vars["prevSession"] = fix.FromSession.ToString();
        return Render("fix.md", vars);
    }

    public string Resume(StageConfig stage, int sessionNumber, int attempt, int maxAttempts, PendingResume resume)
    {
        var vars = Vars(stage, sessionNumber, attempt, maxAttempts);
        vars["reason"] = resume.Reason;
        return Render("resume.md", vars);
    }

    public string Audit(StageConfig stage, int sessionNumber, Models.PendingAudit audit, string stageStartHead, string? personaOverride = null)
    {
        var vars = Vars(stage, sessionNumber, 1, 1, personaOverride);
        vars["diffBase"] = stageStartHead;
        vars["handoverPath"] = $".conductor/handovers/{stage.Id}.md";
        return Render("audit.md", vars);
    }

    public string Advisor(StageConfig stage, string outcome, string gates, string commits, string handoff, string tail, int attempt, int maxAttempts)
    {
        var vars = Vars(stage, 0, attempt, maxAttempts);
        vars["outcome"] = outcome;
        vars["gates"] = gates;
        vars["commits"] = commits;
        vars["handoff"] = handoff;
        vars["tail"] = tail;
        return Render("advisor.md", vars);
    }

    public string Verify(StageConfig stage, int sessionNumber, PendingVerify verify, string? personaOverride = null)
    {
        var vars = Vars(stage, sessionNumber, 1, 1, personaOverride);
        vars["prevSession"] = verify.FromSession.ToString();
        vars["diffBase"] = verify.StageStartHead;
        return Render("verify.md", vars);
    }

    public string Review(StageConfig stage, int sessionNumber, int attempt, int maxAttempts, string reviewPath)
    {
        var vars = Vars(stage, sessionNumber, attempt, maxAttempts);
        vars["reviewPath"] = reviewPath;
        return Render("review.md", vars);
    }

    /// <summary>SF6.3 — <c>chat.md</c> had a built-in and no caller: <c>ChatCommand</c> hand-rolled a
    /// different prompt inline (and rendered a whole DELIVER prompt first, only to throw it away), so a
    /// <c>templates/chat.md</c> an operator edited was inert — readable and ignored, the same defect
    /// class SF0.1 killed in the plan keys. Now chat goes through the one resolution path, which is
    /// also what makes scaffolding the file honest. <c>{extra}</c> carries the query, as the template
    /// has always said it did.</summary>
    public string Chat(string query)
    {
        var stage = new StageConfig { Id = "chat", Title = "Conductor Chat", Kind = "deliver", Sessions = 1 };
        var vars = Vars(stage, 0, 1, 1);
        vars["extra"] = query;
        return Render("chat.md", vars);
    }

    private Dictionary<string, string> Vars(StageConfig stage, int sessionNumber, int attempt, int maxAttempts, string? personaOverride = null)
    {
        var readOrder = "";
        if (_plan.ReadOrder is { Count: > 0 } docs)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Required reading (in order):");
            for (var i = 0; i < docs.Count; i++)
                sb.AppendLine($"{i + 1}. {docs[i]}");
            readOrder = sb.ToString();
        }

        // P1: a role→agent rule may override the stage persona for this one session.
        var personaName = personaOverride ?? _plan.ResolvePersona(stage);
        var personaSystemPrompt = _personas.ResolveSystemPrompt(personaName) ?? "";

        var lessonsContent = _lessons.ReadRecent(5);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["planName"] = _plan.Name,
            ["repo"] = _plan.Repo,
            ["tracker"] = _plan.Tracker,
            // Fall back to the tracker when a plan sets no separate design doc, so the ritual reads
            // "exactly as `TRACKER.md` prescribes" instead of the broken "exactly as `` prescribes".
            ["planDoc"] = string.IsNullOrWhiteSpace(_plan.PlanDoc) ? _plan.Tracker : _plan.PlanDoc,
            ["stage"] = stage.Id,
            ["stageTitle"] = stage.Title,
            ["stageNotes"] = string.IsNullOrWhiteSpace(stage.Notes) ? "" : $"\nStage-specific notes from the orchestrator config:\n{stage.Notes}\n",
            ["sessionNumber"] = sessionNumber.ToString(),
            ["attempt"] = attempt.ToString(),
            ["maxAttempts"] = maxAttempts.ToString(),
            ["extra"] = _plan.PromptExtra,
            ["readOrder"] = readOrder,
            ["persona"] = personaName ?? "",
            ["personaSystemPrompt"] = personaSystemPrompt,
            ["lessons"] = lessonsContent,
            ["verifierThreshold"] = _qa.EffectiveVerifierThreshold(_plan, stage).ToString(),
            ["tools"] = ToolContract.Render(_plan),
            ["packs"] = LoadPacks(),
            ["batteryCollapseNote"] = _plan.BatteryCollapse
                ? "\n**IMPORTANT — battery collapse (B10.4):** Do NOT run build or test commands yourself. Conductor's independent battery is the single source of truth. Instead, describe what you changed in a `## Changes` section of your handoff so Conductor knows what to verify. This saves tokens and avoids duplicating work."
                : "",
        };
    }

    /// <summary>Resolves a template by name. Search order: the plan's <c>templatesDir</c>, then the plan
    /// directory itself, then the built-in default. Until this honoured <c>templatesDir</c>, every plan that
    /// set it silently ran on the built-ins and its .md files were dead — so a template edit did nothing.</summary>
    internal string ResolveTemplatePath(string templateFile)
    {
        if (!string.IsNullOrWhiteSpace(_plan.TemplatesDir))
        {
            var inTemplatesDir = Path.Combine(_plan.PlanDir, _plan.TemplatesDir, templateFile);
            if (File.Exists(inTemplatesDir)) return inTemplatesDir;
        }
        return Path.Combine(_plan.PlanDir, templateFile);
    }

    /// <summary>Concatenates the plan's domain packs — the "batteries included" context: house C#
    /// style, the mistakes agents make in this domain, etc. Resolves era-first then shared, the same
    /// way personas already do (SF6.2): <c>templatesDir/packs/&lt;name&gt;.md</c> wins when it exists,
    /// otherwise <c>&lt;PlanDir&gt;/packs/&lt;name&gt;.md</c>. Without the shared fallback a pack is
    /// stranded in whichever era set first wrote it and no later plan can choose it.</summary>
    private string LoadPacks()
    {
        if (_plan.Packs is not { Count: > 0 }) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var name in _plan.Packs)
        {
            var path = ResolvePackPath(name);
            if (path is null) continue;
#pragma warning disable MA0045 // sync read — Render is sync on the control loop's session-start path
            sb.AppendLine(File.ReadAllText(path).Trim()).AppendLine();
#pragma warning restore MA0045
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Era set first, shared bank second, null when neither has it. Pack names are plain
    /// identifiers — a name carrying a separator or <c>..</c> is refused outright rather than
    /// combined into a path, since plan JSON can arrive over the control plane's import endpoint.</summary>
    private string? ResolvePackPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim();
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains("..", StringComparison.Ordinal) ||
            name.Contains('/', StringComparison.Ordinal) ||
            name.Contains('\\', StringComparison.Ordinal))
            return null;

        var file = name + ".md";
        if (!string.IsNullOrWhiteSpace(_plan.TemplatesDir))
        {
            var inEra = Path.Combine(_plan.PlanDir, _plan.TemplatesDir, "packs", file);
            if (File.Exists(inEra)) return inEra;
        }
        var shared = Path.Combine(_plan.PlanDir, "packs", file);
        return File.Exists(shared) ? shared : null;
    }

    private string Render(string templateFile, Dictionary<string, string> vars)
    {
#pragma warning disable MA0045 // sync Render — called from BuildPrompt which is sync in the control loop
        var path = ResolveTemplatePath(templateFile);
        var text = File.Exists(path) ? File.ReadAllText(path) : BuiltIn(templateFile);
#pragma warning restore MA0045

        // SC3.3. Two protections, and the order is the whole point:
        //   1. the template's own {{word}} escapes are held BEFORE substitution, so `{{extra}}`
        //      renders the literal `{extra}` instead of the extra's value;
        //   2. every substituted VALUE is held as it goes in, so a brace inside stage notes, a
        //      tracker handoff, gate output or an agent's transcript tail is prose — it can neither
        //      be re-substituted by a later variable (which used to depend on dictionary order) nor
        //      read as an unresolved placeholder and kill the run.
        // Only the template can carry a placeholder, so only the template can be refused.
        text = PromptPlaceholders.ProtectEscapes(text);
        foreach (var (k, v) in vars) text = text.Replace("{" + k + "}", PromptPlaceholders.ProtectValue(v), StringComparison.Ordinal);
        PromptValidator.ThrowIfUnresolved(text, templateFile);
        text = PromptPlaceholders.Restore(text).Trim();

        // SC4.4. Human-injected instructions (from the TUI `I` key / `.conductor/queue/`) go into
        // every session prompt whether or not the template names them — but WHERE decides whether
        // they are read. They used to be appended last, which put a correction below the very
        // evidence it corrected plus the batteries, the task cards and the audit findings that
        // SessionRunner appends after this (devcontext #15: 113 lines below, and the agent worked
        // the evidence). They now splice in immediately after the role line, before the persona is
        // prepended — so the only thing that can sit above an injection is a role definition, never
        // a fact the injection might be there to correct.
        var queued = InstructionQueue.PromptSection(_plan);
        if (queued.Length > 0) text = InsertAfterRoleLine(text, queued);

        // B13.4: the session's economics, stated once, high in the prompt. Below the human injection
        // because a correction outranks a budget, above everything else because how the agent paces
        // itself changes every decision that follows.
        var budget = BudgetSection();
        if (budget.Length > 0) text = InsertAfterRoleLine(text, budget);

        // Merge in persona system prompt — appended after base prompt rendering so the
        // persona doesn't need to be referenced in every template (B7.3). Conductor contract
        // rules (the built-in template's rules block) remain last and win over persona content.
        var personaSystemPrompt = vars.GetValueOrDefault("personaSystemPrompt", "");
        if (personaSystemPrompt.Length > 0)
            text = personaSystemPrompt + "\n\n" + text;

        return text;
    }

    /// <summary>SC4.4: splices <paramref name="block"/> in directly beneath the prompt's role line —
    /// the first line, the "You are a FIX session ..." sentence every template opens with — so the
    /// block is the first thing the agent reads that is not its own job description. A template that
    /// is a single line gets the block appended, which is the same position.</summary>
    internal static string InsertAfterRoleLine(string text, string block)
    {
        var nl = text.IndexOf('\n', StringComparison.Ordinal);
        if (nl < 0) return text + "\n\n" + block;
        // Keep the template's own line ending: a CRLF template must not sprout lone LFs mid-prompt.
        var eol = nl > 0 && text[nl - 1] == '\r' ? "\r\n" : "\n";
        var roleLine = text[..nl].TrimEnd('\r');
        var rest = text[(nl + 1)..].TrimStart('\r', '\n');
        return roleLine + eol + eol + block + eol + eol + rest;
    }

    /// <summary>Builds and renders the battery group section from the plan's
    /// <see cref="BatteriesConfig"/>, using current run state for the recent-failure digest (B8.5).
    /// When a <paramref name="store"/> is supplied (always, in a real run) the M7 knowledge batteries —
    /// the ledger and the run's open bugs — are injected too, so knowledge compounds across sessions.</summary>
    public string BatterySection(RunState? state, IRunStore? store = null,
        IReadOnlyList<TaskItem>? checkpoints = null, string? stageId = null)
    {
        var cfg = _plan.Batteries;

        var list = new List<IPromptBattery>();

        // M7.1/M7.2: knowledge that compounds — injected by default (no batteries block required), and
        // added FIRST so the byte cap never truncates them away: the ledger and open bugs from prior
        // sessions must reliably reach the next prompt (that is the whole point of M7).
        if (store != null && state is { RunId.Length: > 0 })
        {
            if (cfg?.Ledger ?? true)
            {
                var ledgerBattery = new LedgerBattery(store, state.RunId, cfg?.LedgerMaxEntries ?? 8);
                if (!ledgerBattery.IsEmpty) list.Add(ledgerBattery);
            }
            if (cfg?.Bugs ?? true)
            {
                var bugsBattery = new BugsBattery(store, state.RunId);
                if (!bugsBattery.IsEmpty) list.Add(bugsBattery);
            }
        }

        if (cfg != null)
        {
            if (cfg.Lessons) list.Add(new LessonsBattery(_lessons, cfg.LessonsMaxEntries));
            if (cfg.RecentFailure && state != null) list.Add(new RecentFailureBattery(state));
        }

        // KS7.5: the two context-economics batteries. Both are gated on the caller having supplied
        // what they need - the repo map on a repo path that exists, the recap on the folded board -
        // so a caller that composes a prompt without a graph (the control-plane preview) renders the
        // same prompt minus a section it has no data for, rather than an empty heading.
        if (cfg?.RepoMap == true && _plan.Repo is { Length: > 0 } repoRoot)
        {
            var mapBattery = new RepoMapBattery(repoRoot, cfg.RepoMapMaxEntries);
            if (!mapBattery.IsEmpty) list.Add(mapBattery);
        }
        if (cfg?.DefinitionOfDone == true && checkpoints is { Count: > 0 } && stageId is { Length: > 0 })
        {
            var dodBattery = new DefinitionOfDoneBattery(checkpoints, stageId);
            if (!dodBattery.IsEmpty) list.Add(dodBattery);
        }

        // B12.1: inject recent analysis-lane artifacts into the next session's prompt
        if (_plan.AnalysisLanes.Count > 0 && state != null)
        {
            var laneBattery = new LaneArtifactBattery(_plan.StateDir, state.CurrentStage ?? "");
            if (!laneBattery.IsEmpty) list.Add(laneBattery);
        }

        var maxBytes = cfg?.MaxBytes ?? 2048;
        return list.Count > 0 ? new BatteryGroup(list, maxBytes).Render() : "";
    }

}
