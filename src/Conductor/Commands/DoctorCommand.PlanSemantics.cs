using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.History;
using Conductor.Core.Store;
using Conductor.Models;

namespace Conductor.Commands;

/// <summary>
/// KS1.4 — the four lints that read the plan DOCUMENT: where its gates and hooks point, whether the
/// checkpoint ids it declares are the ones the tracker actually carries, and whether the file on
/// disk has moved on from the version a run is still executing. The prompt-side half (argv length,
/// the brace sweep, the escalation-token sweep) lives in <c>DoctorCommand.PromptSemantics.cs</c>;
/// splitting on that seam keeps both files under the architecture ceiling and names each for what it
/// declares rather than numbering them.
/// <para>Every check here RESOLVES and never EXECUTES. A doctor that ran a gate to see whether it
/// works would rebuild the engine it is diagnosing (bug #16), so the probe stops at "the program
/// exists and the declared paths are there" — which is the half that fails at spawn, silently, with
/// the run already thirteen sessions in.</para>
/// </summary>
public sealed partial class DoctorCommand
{
    /// <summary>The shells <see cref="ProcessRunner.RunShellAsync"/> knows. Anything else returns
    /// exit -1 at gate time with "unknown shell", which reads as a failing gate rather than as the
    /// authoring mistake it is.</summary>
    private static readonly string[] KnownShells = ["powershell", "bash", "sh"];

    /// <summary>Words that open a shell command without naming a program on disk. Kept deliberately
    /// short: a token this list does not know is reported, because a typo'd executable is the whole
    /// point of the probe.</summary>
    private static readonly HashSet<string> ShellKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "cd", "echo", "exit", "set", "if", "for", "foreach", "while", "pushd", "popd",
        "true", "false", ".", "&", "source", "export", "rem", "call", "start", "type",
    };

    /// <summary>KS1.4 — the gate-command path probe. A gate is a shell line the engine spawns at a
    /// stage boundary; when the program it names is not there the battery does not "fail", it fails
    /// to start, and the run reads that as a red gate and burns its attempts fixing code that was
    /// never wrong. Resolves the shell (<see cref="GateConfig.Shell"/>), the leading executable of
    /// <see cref="GateConfig.Command"/>, the working directory (<see cref="GateConfig.Cwd"/>) and the
    /// declared paths (<see cref="GateConfig.SkipIfMissing"/>, <see cref="GateConfig.WatchPaths"/>).
    /// <para>It never runs the gate. Coverage — which stage gets which battery — is
    /// <see cref="CheckGates"/>'s question and is not asked again here.</para></summary>
    internal static Check CheckGatePaths(PlanConfig plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Gates.Count == 0)
            return new Check("gate-paths", "ok", "no gates configured — nothing to resolve");

        var fails = new List<string>();
        var warns = new List<string>();

        foreach (var g in plan.Gates)
        {
            var label = string.IsNullOrWhiteSpace(g.Name) ? "(unnamed)" : g.Name;

            var shell = string.IsNullOrWhiteSpace(g.Shell) ? ProcessRunner.DefaultShell : g.Shell;
            if (!KnownShells.Contains(shell, StringComparer.OrdinalIgnoreCase))
                fails.Add($"gate '{label}': shell '{shell}' is not one of {string.Join(", ", KnownShells)} — every battery would exit -1 without running");
            else if (ShellExecutable(shell) is { } exe && ResolveProgram(exe, plan.Repo) is null)
                fails.Add($"gate '{label}': shell '{shell}' resolves to '{exe}', which is not on PATH");

            var cwd = string.IsNullOrWhiteSpace(g.Cwd) ? plan.Repo : Path.Combine(plan.Repo, g.Cwd);
            if (!Directory.Exists(cwd))
                fails.Add($"gate '{label}': cwd '{g.Cwd}' does not exist ({cwd})");

            var leading = LeadingProgram(g.Command);
            if (leading is null)
                fails.Add($"gate '{label}': command is empty");
            else if (!ShellKeywords.Contains(leading) && ResolveProgram(leading, Directory.Exists(cwd) ? cwd : plan.Repo) is null)
                fails.Add($"gate '{label}': command starts with '{leading}', which is neither a file nor on PATH — the gate fails at spawn, not at assertion");

            if (g.SkipIfMissing is { Length: > 0 } skip && !PathExists(Path.Combine(plan.Repo, skip)))
                warns.Add($"gate '{label}': skipIfMissing '{skip}' does not exist, so this gate is skipped on every battery until it does");

            foreach (var w in g.WatchPaths ?? [])
            {
                if (string.IsNullOrWhiteSpace(w)) continue;
                if (!PathExists(Path.IsPathRooted(w) ? w : Path.Combine(plan.Repo, w)))
                    warns.Add($"gate '{label}': watchPath '{w}' does not exist, so it adds nothing to the result-cache key");
            }
        }

        if (fails.Count > 0)
            return new Check("gate-paths", "fail", string.Join("; ", fails));
        if (warns.Count > 0)
            return new Check("gate-paths", "warn", string.Join("; ", warns));
        return new Check("gate-paths", "ok",
            $"{plan.Gates.Count} gate(s): shell, leading program and every declared path resolve (nothing was executed)");
    }

    /// <summary>KS1.4 — the hook dry-run. <c>setup</c>, <c>teardown</c> and the per-stage pre/post
    /// hooks are best-effort by design: <see cref="GateRunner.RunHookAsync"/> logs a nonzero exit and
    /// carries on, so a hook naming a program that is not installed is invisible for the whole run
    /// while the clean-slate it promised never happens. Hooks always go through PowerShell, so this
    /// resolves that shell, the leading program of each command, and each hook's working directory.
    /// Zero/negative timeouts are already refused at plan load (<c>PlanConfig.CollectErrors</c>) and
    /// are not re-checked here. Nothing is executed — a "dry run" that ran the hook would run the
    /// build the hook exists to reset.</summary>
    internal static Check CheckHooks(PlanConfig plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var hooks = new List<(string Label, HookConfig Hook)>();
        if (plan.Setup is { } setup) hooks.Add(("plan.setup", setup));
        if (plan.Teardown is { } teardown) hooks.Add(("plan.teardown", teardown));
        foreach (var s in plan.Stages)
        {
            if (s.PreHook is { } pre) hooks.Add(($"stage '{s.Id}' pre-hook", pre));
            if (s.PostHook is { } post) hooks.Add(($"stage '{s.Id}' post-hook", post));
        }
        hooks.RemoveAll(h => string.IsNullOrWhiteSpace(h.Hook.Command));
        if (hooks.Count == 0)
            return new Check("hooks", "ok", "no setup/teardown or stage hooks configured");

        var fails = new List<string>();
        var shell = ShellExecutable("powershell")!;
        if (ResolveProgram(shell, plan.Repo) is null)
            fails.Add($"every hook runs through '{shell}', which is not on PATH");

        foreach (var (label, hook) in hooks)
        {
            var cwd = string.IsNullOrWhiteSpace(hook.Cwd) ? plan.Repo : Path.Combine(plan.Repo, hook.Cwd);
            if (!Directory.Exists(cwd))
                fails.Add($"{label}: cwd '{hook.Cwd}' does not exist ({cwd})");

            var leading = LeadingProgram(hook.Command);
            if (leading is not null && !ShellKeywords.Contains(leading)
                && ResolveProgram(leading, Directory.Exists(cwd) ? cwd : plan.Repo) is null)
                fails.Add($"{label}: command starts with '{leading}', which is neither a file nor on PATH — the hook exits nonzero and the run carries on without it");
        }

        return fails.Count > 0
            ? new Check("hooks", "fail", string.Join("; ", fails))
            : new Check("hooks", "ok", $"{hooks.Count} hook(s) resolve: shell, leading program and cwd (nothing was executed)");
    }

    /// <summary>KS1.4 — the checkpoint-id cross-check. The engine only ever sees the checkpoint rows
    /// its provider can PARSE: <c>MarkdownTableProvider</c> keeps the lines that match the row regex
    /// assembled from <c>conventions.stageIdPattern</c> and drops the rest without a word, so a row
    /// whose id is shaped a little differently is work the run will never schedule and never report.
    /// A duplicated id is the same defect from the other end — two rows, one identity, and whichever
    /// the fold visits last wins.
    /// <para>Distinct from <see cref="CheckWorkCoverage"/>, which asks whether the PARSED items line
    /// up with the plan's stages. This asks whether the tracker's rows became items at all.</para></summary>
    internal static Check CheckCheckpointIds(PlanConfig plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Core.TrackerSnapshot declared;
        try { declared = Core.Planning.ProgressProviderFactory.Create(plan).Read(plan); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new Check("checkpoint-ids", "warn", $"declared work unreadable ({ex.Message}) — no id could be cross-checked");
        }

        var fails = new List<string>();

        var duplicates = declared.Checkpoints
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"'{g.Key}' x{g.Count()}")
            .ToList();
        if (duplicates.Count > 0)
            fails.Add($"duplicate checkpoint id(s) {string.Join(", ", duplicates)} — one identity, two rows, and the later write wins");

        var parsed = declared.Checkpoints.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unparsed = UnparsedRows(declared.RawText, parsed);
        if (unparsed.Count > 0)
            fails.Add($"tracker row(s) {string.Join(", ", unparsed.Select(u => $"'{u.Id}' (line {u.Line})"))} " +
                      $"do not parse under conventions.stageIdPattern '{plan.Conventions.StageIdPattern}' — the engine never schedules them");

        if (fails.Count > 0) return new Check("checkpoint-ids", "fail", string.Join("; ", fails));
        return new Check("checkpoint-ids", "ok",
            $"{parsed.Count} checkpoint id(s) parse under conventions.stageIdPattern '{plan.Conventions.StageIdPattern}', none declared twice");
    }

    /// <summary>KS1.4 — plan drift. The plan file is editable while a run is up, and the engine only
    /// picks an edit up at a session boundary (<c>RunLoop.Reload</c>), so between the two the file on
    /// disk and the plan being executed are different documents — and every surface reads the file.
    /// Stated mechanically, with no hash nobody else computes: the version the last run RECORDED
    /// loading (<see cref="PlanReloaded"/>, folded out of that run's event log) against
    /// <see cref="PlanConfig.PlanVersion"/> on disk now.
    /// <para>Fails only while a run is still being scheduled FROM, because that is the only case
    /// where the stale document is doing anything. Which runs those are is not read off
    /// <c>runs.status</c>: KS1.3 installed <see cref="RunLiveness"/> exactly because a killed engine
    /// never gets to correct its own row, so an unfinished row with no engine behind it means the
    /// opposite of what it says. Asking the column raw would have made this lint red on every repo
    /// whose last run was killed and whose plan has been edited since — a red that no reload can
    /// clear, on the machine where four such rows are the whole of FU-F1-06.</para></summary>
    internal static Check CheckPlanDrift(PlanConfig plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        RunArchive? archive;
        string dbPath;
        try
        {
            dbPath = plan.ResolveState().RunDbPath;
            archive = RunArchive.TryOpen(dbPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return new Check("plan-drift", "warn", $"the run store could not be opened ({ex.Message}) — drift is unknown");
        }
        if (archive is null)
            return new Check("plan-drift", "ok", $"no history yet — nothing has loaded this plan (file is v{plan.PlanVersion})");

        var run = archive.Runs().FirstOrDefault(r => string.Equals(r.PlanName, plan.Name, StringComparison.OrdinalIgnoreCase));
        if (run is null)
            return new Check("plan-drift", "ok", $"no run of '{plan.Name}' in the store (file is v{plan.PlanVersion})");

        var loaded = LastLoadedPlanVersion(archive, run.RunId);
        if (loaded is null)
            return new Check("plan-drift", "ok",
                $"run {run.ShortRunId} recorded no plan reload, so nothing says it is behind the v{plan.PlanVersion} file");
        if (loaded == plan.PlanVersion)
            return new Check("plan-drift", "ok", $"run {run.ShortRunId} loaded plan v{loaded}, the file on disk is v{plan.PlanVersion}");

        var head = $"run {run.ShortRunId} last loaded plan v{loaded}, but {plan.PlanFilePath} is v{plan.PlanVersion}";
        var live = RunLiveness.StoreLooksLive(dbPath, plan.Repo);
        if (!RunLiveness.IsStillGoing(run.Status, live))
            return new Check("plan-drift", "ok",
                $"{head} — that run reads '{RunLiveness.Reconcile(run.Status, live)}' and nothing is scheduling from it, " +
                "so the difference is only the edits made since");
        return new Check("plan-drift", "fail",
            $"{head} — that run has not finished and an engine is holding the store, so it keeps scheduling from the stale document until `conductor plan reload`");
    }

    /// <summary>The last plan version this run recorded loading. Folded from the event log — the same
    /// source <c>RunArchive.Checkpoints</c> and <c>RunArchive.Stages</c> read — because the runs table
    /// has no column for it and a snapshot column would be one more thing to keep true.</summary>
    private static int? LastLoadedPlanVersion(RunArchive archive, string runId)
    {
        int? last = null;
        foreach (var evt in archive.EventsOf(runId))
            if (evt is PlanReloaded reloaded) last = reloaded.PlanVersion;
        return last;
    }

    /// <summary>Tracker lines that LOOK like checkpoint rows — a five-cell pipe table whose first cell
    /// opens with a letter or digit — but whose id is not among the ones the provider parsed. Header
    /// and separator rows are skipped by shape, so a plan with a differently-named header column is
    /// not a finding.</summary>
    private static List<(int Line, string Id)> UnparsedRows(string rawText, HashSet<string> parsedIds)
    {
        var found = new List<(int, string)>();
        if (string.IsNullOrEmpty(rawText)) return found;

        var lines = rawText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length < 2 || line[0] != '|') continue;
            var cells = line.Trim('|').Split('|');
            if (cells.Length < 5) continue;

            var id = cells[0].Trim();
            if (id.Length == 0 || !char.IsLetterOrDigit(id[0])) continue;
            if (id.Contains(' ', StringComparison.Ordinal)) continue; // a prose header cell, not an id
            if (parsedIds.Contains(id)) continue;
            if (HeaderWords.Contains(id)) continue;
            found.Add((i + 1, id));
        }
        return found;
    }

    private static readonly HashSet<string> HeaderWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "ids", "checkpoint", "checkpoints", "item", "metric", "stage", "phase", "task",
        "name", "file", "why", "run", "no", "num", "status", "step", "bug",
    };

    /// <summary>The binary a shell name spawns, matching <see cref="ProcessRunner.RunShellAsync"/>
    /// exactly — including that "powershell" means <c>powershell.exe</c> on Windows and <c>pwsh</c>
    /// everywhere else. Null for a shell that type does not know.</summary>
    private static string? ShellExecutable(string shell) => shell.ToLowerInvariant() switch
    {
        "powershell" => OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
        "bash" => "bash",
        "sh" => "sh",
        _ => null,
    };

    /// <summary>The first program token of a shell command line: a quoted path in full, otherwise
    /// everything up to the first whitespace, with a trailing separator trimmed. Null when the
    /// command is blank.</summary>
    internal static string? LeadingProgram(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var s = command.TrimStart();
        string token;
        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            token = end < 0 ? s[1..] : s[1..end];
        }
        else
        {
            var end = s.IndexOfAny([' ', '\t', '\r', '\n']);
            token = end < 0 ? s : s[..end];
        }
        token = token.TrimEnd(';', '&', '|');
        return token.Length == 0 ? null : token;
    }

    /// <summary>Resolves a program the way a spawn would: an absolute or relative path is probed on
    /// disk (with PATHEXT on Windows), a bare name goes to PATH. Null = nothing would start.</summary>
    private static string? ResolveProgram(string token, string cwd)
    {
        if (!IsPathLike(token)) return ResolveOnPath(token);
        var full = Path.IsPathRooted(token) ? token : Path.Combine(cwd, token);
        if (File.Exists(full)) return full;
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var ext in (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                     .Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(full + ext)) return full + ext;
        return null;
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);
}
