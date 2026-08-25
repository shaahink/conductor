using System.Text;
using Conductor.Core;
using Conductor.Models;
using Conductor.Planning;

namespace Conductor.Commands;

/// <summary>
/// KS1.4 — the three lints that read the COMPOSED PROMPT rather than the plan document: how long the
/// argv the engine will spawn actually is, whether every brace token under <c>templatesDir</c> is a
/// placeholder something resolves, and whether the escalation token has been left where a session
/// will read it back out. The plan-document half is in <c>DoctorCommand.PlanSemantics.cs</c>.
/// <para>All three reuse <see cref="PromptMatrix"/> — the same session-kind matrix
/// <see cref="CheckPrompt"/> renders — and <see cref="PromptBuilder"/> itself. A second renderer that
/// could disagree with the real one is the defect the prompt check exists to prevent.</para>
/// </summary>
public sealed partial class DoctorCommand
{
    /// <summary>CreateProcess' command-line ceiling. <c>AgentSession.Start</c> spawns with
    /// <c>UseShellExecute=false</c>, so this is the wall the engine actually hits.</summary>
    internal const int CreateProcessCommandLineCeiling = ArgvLimits.CreateProcessCommandLine;

    /// <summary>cmd.exe's much lower ceiling. It applies whenever the agent command resolves to a
    /// <c>.cmd</c>/<c>.bat</c> shim — an npm-installed CLI is exactly that — because Windows runs the
    /// shim through the command interpreter. Bug #15's "silently stops a cmd.exe-based agent".</summary>
    internal const int CmdExeCommandLineCeiling = ArgvLimits.CmdExeCommandLine;

    /// <summary>The session kinds, per stage, that <see cref="CheckPrompt"/> renders and
    /// <see cref="CheckArgvLength"/> measures. One matrix, so the two can never disagree about what a
    /// session is.</summary>
    internal static (string Template, Func<string> Render)[] PromptMatrix(PromptBuilder prompts, PlanConfig plan, StageConfig stage)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(plan);
        var fix = new PendingFix { FromSession = 1, GateFailures = "(doctor)", ProgressSummary = "(doctor)" };
        var resume = new PendingResume { FromSession = 1, Reason = "(doctor)" };
        var verify = new PendingVerify { FromSession = 1, StageStartHead = "HEAD" };
        var audit = new PendingAudit { StageId = plan.Stages.FirstOrDefault()?.Id ?? "", StageStartHead = "HEAD" };
        return
        [
            ("session.md", () => prompts.Deliver(stage, 1, 1, 1)),
            ("fix.md", () => prompts.Fix(stage, 1, 1, 1, fix)),
            ("resume.md", () => prompts.Resume(stage, 1, 1, 1, resume)),
            ("verify.md", () => prompts.Verify(stage, 1, verify)),
            ("audit.md", () => prompts.Audit(stage, 1, "HEAD")),
            ("review.md", () => prompts.Review(stage, 1, 1, 1, "(doctor)")),
            // KS4.5: the judge is not a session kind, but its prompt is composed the same way and
            // travels the same road — as an ARGUMENT to a CLI — so it is linted and measured with the
            // rest. Left out, its two new placeholders were unresolvable by construction and a
            // scaffolded templates dir was doctor-red on a template the operator had never touched.
            ("judge.md", () => prompts.Judge(stage, "(doctor)", "(doctor)", "(doctor)", "(doctor)", "(doctor)", "(doctor)")),
        ];
    }

    /// <summary>KS1.4 (bugs #15 / #21) — the composed-prompt argv length. The prompt travels to the
    /// agent as an ARGUMENT, so the plan's packs, its promptExtra and a stage's notes all land in one
    /// command line; past the OS ceiling it is truncated or refused, and the agent does its best with
    /// whatever arrived while the run reports a healthy session. Nothing downstream can see it.
    /// <para>Measures the real thing: every session kind for every stage, rendered by
    /// <see cref="PromptBuilder"/>, substituted by <c>AgentSession.ResolveArgs</c>, and quoted the way
    /// <c>ProcessStartInfo.ArgumentList</c> quotes. The ceiling is chosen by how the agent is spawned
    /// — 8191 through a <c>.cmd</c>/<c>.bat</c> shim, 32767 through CreateProcess — and an argv that
    /// clears the resolved ceiling but not the shim's is a WARN rather than silence, because which of
    /// the two applies is a property of the machine the agent was installed on, not of the plan.</para></summary>
    internal static Check CheckArgvLength(PlanConfig plan) => CheckArgvLength(plan, ArgvCeiling(plan));

    /// <summary>The same lint with the ceiling STATED rather than resolved. Doctor itself always
    /// resolves — a diagnostic that ignored how this machine will spawn the agent would be describing
    /// somebody else's machine. A caller that must be hermetic (a pinned test whose verdict may not
    /// depend on whether the agent CLI here is an npm shim) states it instead.</summary>
    internal static Check CheckArgvLength(PlanConfig plan, (int Ceiling, string Why) resolved)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Stages.Count == 0)
            return new Check("argv", "ok", "no stages — nothing composes");

        var (ceiling, why) = resolved;
        var prompts = new PromptBuilder(plan);
        var worstLength = 0;
        var worstWhere = "";

        foreach (var stage in plan.Stages)
        {
            var eff = plan.ResolveAgent(stage);
            foreach (var (template, render) in PromptMatrix(prompts, plan, stage))
            {
                string prompt;
                try { prompt = render(); }
                catch (PromptCompositionException) { continue; }   // CheckPrompt owns that failure
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                foreach (var (name, argsTemplate) in AgentTemplates(eff))
                {
                    if (argsTemplate.Count == 0) continue;
                    var argv = AgentSession.ResolveArgs(argsTemplate, prompt, "00000000-0000-0000-0000-000000000000",
                        string.Equals(name, "resumeArgs", StringComparison.Ordinal) ? "00000000-0000-0000-0000-000000000000" : null,
                        eff.Model);
                    var length = CommandLineLength(eff.Command, argv);
                    if (length <= worstLength) continue;
                    worstLength = length;
                    worstWhere = $"stage '{stage.Id}' {template} through agent.{name}";
                }
            }
        }

        if (worstLength == 0)
            return new Check("argv", "ok", "no session composes an argv — nothing to measure");

        var measured = $"longest composed argv is {worstLength} chars ({worstWhere}) against the {ceiling}-char ceiling ({why})";
        if (worstLength > ceiling)
            return new Check("argv", "fail",
                $"{measured} — the agent is truncated or refused at spawn and the run scores the session as if it had read everything; " +
                "shorten promptExtra/packs/stage notes, or pass the prompt on stdin");
        // Bug #21: clearing CreateProcess' ceiling is not clearing the ceiling. The same plan is fatal
        // the moment agent.command lands on a .cmd/.bat shim — which is what an npm install of an agent
        // CLI is on Windows, and it is one `agent.command` edit or one machine away. Saying nothing
        // here would mean the only warning arrives on the box where it is already too late.
        if (ceiling > CmdExeCommandLineCeiling && worstLength > CmdExeCommandLineCeiling)
            return new Check("argv", "warn",
                $"{measured}, but {worstLength} is over the {CmdExeCommandLineCeiling}-char cmd.exe ceiling — " +
                "this plan is fatal on any machine whose agent.command resolves to a .cmd/.bat shim (an npm-installed CLI is exactly that)");

        // DV2.2, bug #55 — the remainder this lint used to leave out of its own number. PromptBuilder
        // renders here with no store, so the knowledge batteries contribute NOTHING to worstLength
        // while at spawn they contribute up to `batteries.maxBytes`; measured against the real spawn,
        // doctor read 350-500 chars light. The battery cap is a true upper bound, so adding it turns
        // an under-measurement into a stated bound rather than a second guess. The sections that need
        // a live run to exist — the claimed-items list, the task-context cards, the parallel-audit
        // findings — are named rather than estimated: doctor cannot know them before a run does, and
        // `conductor preflight` measures them through SessionComposer for a run that exists.
        var remainder = BatteryRemainder(plan);
        var bound = worstLength + remainder;
        if (bound > ceiling && worstLength <= ceiling)
            return new Check("argv", "warn",
                $"{measured} — but the knowledge batteries render from the live store at spawn and are not in that " +
                $"number: with their cap it reaches {bound}, over the ceiling. Lower batteries.maxBytes or the prompt " +
                "(`conductor preflight` measures a real session's tail sections too)");

        return bound * 10 > ceiling * 9
            ? new Check("argv", "warn",
                $"{measured} — under 10% headroom left once the batteries' {remainder}-char cap is counted")
            : new Check("argv", "ok", measured);
    }

    /// <summary>What a launch adds to a doctor-composed argv and doctor cannot render: the whole
    /// battery section's cap plus its two joining characters, and the width the launch's pid can add
    /// over this process's (ToolContract embeds it; a Windows pid is at most ten digits). Zero when
    /// both knowledge batteries are switched off — there is then nothing unrendered to allow for.</summary>
    private static int BatteryRemainder(PlanConfig plan)
    {
        var cfg = plan.Batteries;
        var on = (cfg?.Ledger ?? true) || (cfg?.Bugs ?? true) || (cfg?.Lessons ?? false);
        return on ? (cfg?.MaxBytes ?? 2048) + 2 + PreflightCommand.PidSlack : 0;
    }

    /// <summary>Which argv template a spawn uses: <c>args</c> on a first start, <c>resumeArgs</c>
    /// whenever the session resumes (<c>AgentSession.Start</c> swaps them), so both are measured.</summary>
    private static List<(string Name, IReadOnlyList<string> Template)> AgentTemplates(AgentConfig agent)
    {
        var list = new List<(string, IReadOnlyList<string>)> { ("args", agent.Args) };
        if (agent.ResumeArgs is { Count: > 0 } resume) list.Add(("resumeArgs", resume));
        return list;
    }

    /// <summary>The ceiling this plan's agent will actually hit on THIS machine, and why. Internal
    /// because it is the half of the lint that reads PATH: a caller that needs a verdict independent
    /// of how the agent CLI happens to be installed here passes its own pair to the stated-ceiling
    /// overload of <c>CheckArgvLength</c> instead of inheriting this one.</summary>
    /// <para>DV2.2, bug #15: the resolution moved to <see cref="ArgvLimits"/> in Core so the SPAWN
    /// can consult it too. It used to live only here, which is why the engine could walk into a wall
    /// this very method knew about.</para>
    internal static (int Ceiling, string Why) ArgvCeiling(PlanConfig plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ArgvLimits.CeilingFor(plan.Agent.Command, plan.Repo);
    }

    /// <summary>The length of the command line <c>ProcessStartInfo.ArgumentList</c> would build,
    /// quoting each argument by the same rules the runtime uses. Length, not the string: the point is
    /// the measurement, and a prompt does not belong in a doctor's memory twice.</summary>
    internal static int CommandLineLength(string fileName, IReadOnlyList<string> args)
        => ArgvLimits.CommandLineLength(fileName, args);


    /// <summary>KS1.4 (promptExtra trap 8) — the brace sweep. A template file is not part of the plan
    /// document, so plan validation never sees it; a typo'd <c>{name}</c> in one is refused at RENDER
    /// time, which is the first minute of the stage that uses it, on stderr, hours after the operator
    /// walked away. This sweeps every <c>.md</c> under <c>templatesDir</c> — including the files
    /// <see cref="CheckPrompt"/> never renders — and names the file and the token.
    /// <para>"Known" is not a list kept here: each candidate is put through <see cref="PromptBuilder"/>
    /// itself, and a token that survives some session kind is a placeholder. A doubled brace is a
    /// literal and is never a finding, exactly as <see cref="PromptPlaceholders"/> defines it.</para></summary>
    internal static async Task<Check> CheckTemplateBracesAsync(PlanConfig plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var files = TemplateFiles(plan);
        if (files.Count == 0)
            return new Check("templates", "ok", "no templatesDir files — every session renders from the built-in templates");

        var byFile = new List<(string File, IReadOnlyList<string> Tokens)>();
        var candidates = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            string text;
            try { text = await File.ReadAllTextAsync(file).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new Check("templates", "warn", $"{Rel(plan, file)} could not be read ({ex.Message})");
            }
            var tokens = PromptPlaceholders.UnresolvableIn(text);
            if (tokens.Count == 0) continue;
            byFile.Add((file, tokens));
            foreach (var t in tokens) candidates.Add(t);
        }
        if (candidates.Count == 0)
            return new Check("templates", "ok", $"{files.Count} template file(s) carry no brace token at all");

        HashSet<string> known;
        try { known = await ResolvablePlaceholdersAsync(candidates).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Check("templates", "warn", $"the placeholder probe could not write its scratch templates ({ex.Message})");
        }

        var findings = new List<string>();
        foreach (var (file, tokens) in byFile)
            foreach (var token in tokens)
                if (!known.Contains(token))
                    findings.Add($"{Rel(plan, file)} carries {token}, which no session kind resolves");

        return findings.Count > 0
            ? new Check("templates", "fail",
                string.Join("; ", findings) + " — fix the name, or double the braces to mean a literal one")
            : new Check("templates", "ok",
                $"{files.Count} template file(s): all {candidates.Count} brace token(s) are placeholders PromptBuilder resolves");
    }

    /// <summary>KS1.4 (promptExtra trap 9) — the escalation-token sweep. The token is matched as a
    /// plain substring of the tracker's handoff block, so a session that reads it in its own prompt
    /// and echoes it back — describing the convention, quoting a note — parks the run as hard as an
    /// agent raising one, and the park then re-notifies on every wake. This sweeps the two places a
    /// session reads prose from: the plan's authored notes and the template files.
    /// <para>The token is never written here: it is taken from
    /// <c>plan.conventions.humanToken</c>, so this source, its tests and its fixtures stay clean of
    /// the literal that would park the run reading them.</para></summary>
    internal static async Task<Check> CheckEscalationTokenAsync(PlanConfig plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var token = plan.Conventions.HumanToken;
        if (string.IsNullOrEmpty(token))
            return new Check("escalation", "ok", "conventions.humanToken is empty — no escalation token to leak");

        var hits = new List<string>();
        if (plan.PromptExtra.Contains(token, StringComparison.OrdinalIgnoreCase))
            hits.Add("plan.promptExtra");
        foreach (var stage in plan.Stages)
            if (stage.Notes is { Length: > 0 } notes && notes.Contains(token, StringComparison.OrdinalIgnoreCase))
                hits.Add($"stage '{stage.Id}' notes");
        foreach (var file in TemplateFiles(plan))
        {
            try
            {
                var text = await File.ReadAllTextAsync(file).ConfigureAwait(false);
                if (text.Contains(token, StringComparison.OrdinalIgnoreCase)) hits.Add(Rel(plan, file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new Check("escalation", "warn", $"{Rel(plan, file)} could not be read ({ex.Message})");
            }
        }

        return hits.Count > 0
            ? new Check("escalation", "fail",
                $"the escalation token (conventions.humanToken) appears in {string.Join(", ", hits)} — every session reads it and an echo of it in the handoff parks the run; " +
                "describe the escalation in prose instead of spelling the token")
            : new Check("escalation", "ok",
                $"the escalation token appears in no stage note, promptExtra or template — only a real escalation can park this run");
    }

    /// <summary>Which of <paramref name="candidates"/> some session kind resolves. Answered by the
    /// real <see cref="PromptBuilder"/> against a throwaway plan in a scratch directory: a token that
    /// renders is a placeholder, a token that raises <see cref="PromptCompositionException"/> is not.
    /// Offline, and the scratch directory is removed on the way out.</summary>
    private static async Task<HashSet<string>> ResolvablePlaceholdersAsync(IEnumerable<string> candidates)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        var dir = Path.Combine(Path.GetTempPath(), "conductor-doctor-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var probe = new PlanConfig
            {
                Name = "doctor-placeholder-probe",
                Repo = dir,
                Tracker = "TRACKER.md",
                PlanFilePath = Path.Combine(dir, "probe.plan.json"),
            };
            probe.Stages.Add(new StageConfig { Id = "probe", Title = "probe", Sessions = 1 });
            var prompts = new PromptBuilder(probe);
            var kinds = ProbeKinds(prompts, probe);

            foreach (var token in candidates)
            {
                foreach (var (template, render) in kinds)
                {
                    await File.WriteAllTextAsync(Path.Combine(dir, template), "You are a probe session.\n\n" + token + "\n").ConfigureAwait(false);
                    try { render(); known.Add(token); break; }
                    catch (PromptCompositionException) { /* not this kind's placeholder — try the next */ }
                }
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* scratch */ } catch (UnauthorizedAccessException) { /* scratch */ }
        }
        return known;
    }

    /// <summary>The probe's kinds: <see cref="PromptMatrix"/> plus the two templates the matrix does
    /// not cover — <c>advisor.md</c> and <c>chat.md</c> are rendered by the same
    /// <see cref="PromptBuilder"/> and carry vocabulary of their own.</summary>
    private static (string Template, Func<string> Render)[] ProbeKinds(PromptBuilder prompts, PlanConfig probe)
    {
        var stage = probe.Stages[0];
        return
        [
            .. PromptMatrix(prompts, probe, stage),
            ("advisor.md", () => prompts.Advisor(stage, "(probe)", "(probe)", "(probe)", "(probe)", "(probe)", 1, 1)),
            ("chat.md", () => prompts.Chat("(probe)")),
        ];
    }

    /// <summary>Every <c>.md</c> under the plan's <c>templatesDir</c>, packs and personas included —
    /// a brace or an escalation token is as live in one of those as in <c>session.md</c>.</summary>
    private static List<string> TemplateFiles(PlanConfig plan)
    {
        if (string.IsNullOrWhiteSpace(plan.TemplatesDir)) return [];
        var dir = Path.Combine(plan.PlanDir, plan.TemplatesDir);
        if (!Directory.Exists(dir)) return [];
        try { return Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string Rel(PlanConfig plan, string file)
    {
        try { return Path.GetRelativePath(plan.PlanDir, file).Replace('\\', '/'); }
        catch (ArgumentException) { return file; }
    }
}
