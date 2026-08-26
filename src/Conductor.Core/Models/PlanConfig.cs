using Conductor.Core.Store;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Conductor.Models;

/// <summary>The per-mega-plan configuration file (e.g. plans/loom.plan.json).</summary>
public sealed partial class PlanConfig
{
    /// <summary>Schema version. Currently only "1.0" is supported; a plan without a version or
    /// with an unsupported version is rejected with a clear diagnostic (B1.6).</summary>
    public string Version { get; set; } = "1.0";
    /// <summary>P1: Monotonic plan-edit counter, bumped on every modification (set, reload, add-stage).
    /// Starts at 1; the orchestrator can compare this against its loaded value to detect
    /// external edits at session boundaries.</summary>
    public int PlanVersion { get; set; } = 1;
    public string Name { get; set; } = "plan";
    public string Repo { get; set; } = "";

    /// <summary>CH1.2 — what the file actually said for <c>repo</c> when it was written RELATIVE, so
    /// the plan writer can put the file's own text back instead of the absolute path this process
    /// resolved it to. Without it <see cref="Core.Planning.PlanDocumentEditor"/> diffs an absolutised
    /// model against the file's relative text, sees a change, and quietly re-absolutises the file on
    /// the first <c>plan set</c> — undoing the portability nobody would notice was gone.
    /// <c>null</c> when the plan named an absolute repo, which is left exactly as written.</summary>
    [JsonIgnore] public string? RepoAsWritten { get; private set; }
    /// <summary>SC4.3: sibling repositories this plan's work may land in, absolute or relative to
    /// <see cref="Repo"/>. The session verdict diffs each of them for commits alongside the primary
    /// repo, so a checkpoint delivered entirely in a sibling is progress instead of
    /// <c>NoProgress</c> (sk #3 scored it NoProgress twice). Empty = single-repo plan, unchanged.</summary>
    public List<string> SatelliteRepos { get; set; } = [];
    public string Tracker { get; set; } = "";
    public string PlanDoc { get; set; } = "";
    public string? BranchPattern { get; set; }
    public bool PauseOnBlocked { get; set; } = true;
    public AgentConfig Agent { get; set; } = new();
    public AdvisorConfig? Advisor { get; set; }
    /// <summary>KS4.5: the optional advisory second-model review. Off unless present and enabled, and
    /// deliberately NOT part of <see cref="Advisor"/>: the advisor's answer moves the run, the judge's
    /// answer is only ever recorded.</summary>
    public JudgeConfig? Judge { get; set; }
    /// <summary>Optional clean-slate command run before each agent session and before each gate battery.</summary>
    public HookConfig? Setup { get; set; }
    /// <summary>Optional command run after each gate battery to stop anything the session/gates left running.</summary>
    public HookConfig? Teardown { get; set; }
    /// <summary>Selects and configures the progress provider (B1.3). Default = markdown-table (Loom's
    /// strict TRACKER.md), so existing plans are unchanged. `script` and `plan-checkpoints` are the
    /// escape hatches for projects whose progress isn't a strict markdown table (F-1, D-2).</summary>
    public ProgressConfig Progress { get; set; } = new();
    /// <summary>Per-plan progress conventions (B1.4, R1.3): checkpoint-id shape, handoff marker,
    /// human token, status vocabulary. Defaults reproduce Loom's original behaviour, so existing
    /// plans are unchanged; a differently-shaped tracker (e.g. Shamshir's P-0/P3.4b/F5 ids) overrides
    /// only what differs.</summary>
    public ProgressConventions Conventions { get; set; } = new();
    public List<StageConfig> Stages { get; set; } = new();
    public List<GateConfig> Gates { get; set; } = new();
    /// <summary>KS4.1: path to a JSON array of <see cref="GateConfig"/> loaded as HOLDOUT gates —
    /// engine-only checks the agent never sees. Relative to the plan file's dir, and it must be
    /// OUTSIDE the repo working tree: see <see cref="HoldoutGateSource"/> for why that is the point.</summary>
    public string? HoldoutGates { get; set; }
    /// <summary>"perSession" (full battery every session) or "perPhase" (fast gates/session, full battery at stage-done). Default perSession.</summary>
    public string GatePolicy { get; set; } = "perSession";
    public AuditConfig? Audit { get; set; }
    /// <summary>false: skip the per-delivery Verify and rely on Audit / the full battery (M3 stopgap).
    /// The LOWEST-precedence input to <c>QaPolicyExtensions.EffectiveSkipVerification</c> — a QA dial
    /// or a stage's <c>overrides.skipVerification</c> both outrank it (SF0.1 / bug 11).</summary>
    public bool VerifyEachDelivery { get; set; } = true;
    /// <summary>On-demand read-only "what's the status?" agent (dashboard `G` key). Default null → disabled.</summary>
    public StatusAgentConfig? StatusAgent { get; set; }
    public LimitsConfig Limits { get; set; } = new();
    public ReportConfig Report { get; set; } = new();
    public NotifyConfig? Notify { get; set; }
    public TelegramConfig? Telegram { get; set; }
    /// <summary>DV3.3 — the courier block: how this machine turns a voice note into text, and (from
    /// DV4) the daemon that receives one. null → transcription is not configured, which is a
    /// supported state: a voice note still files, with its audio, and the reply says it was not
    /// transcribed.</summary>
    public CourierConfig? Courier { get; set; }
    /// <summary>DV5.2 / findings §2.3 CL-1 — the cloud lane. null (the default, and what every
    /// existing plan carries) → the engine never spawns cloud work at all. Even non-null it is off
    /// until <see cref="CloudLaneConfig.Enabled"/> says otherwise: out there there is no meter, no
    /// stall watchdog and no control plane, so the lane is asked for, never assumed.</summary>
    public CloudLaneConfig? Cloud { get; set; }

    /// <summary>KS9.1: push-only GitHub mirror of the board and the diary. null (the default, and what
    /// every existing plan carries) → the mirror does not exist. Nothing inbound, ever — see
    /// <see cref="GithubConfig"/>.</summary>
    public GithubConfig? Github { get; set; }
    /// <summary>SF5.2: the babysitter <c>conductor watch</c> invokes on wake, with the brief on stdin.
    /// null → nothing runs on wake unless <c>--hook</c> says so.</summary>
    public SupervisorConfig? Supervisor { get; set; }
    public string PromptExtra { get; set; } = "";

    /// <summary>Directory (relative to the plan file) holding the session templates — <c>session.md</c>,
    /// <c>fix.md</c>, <c>verify.md</c>, … — and a <c>packs/</c> subdirectory. Falls back to the plan
    /// directory itself, then to the built-in defaults. Prompts are editable content, not code: this is
    /// what makes the .md files on disk the thing that actually ships to the agent.</summary>
    public string? TemplatesDir { get; set; }

    /// <summary>"Batteries included" domain packs merged into every prompt as <c>{packs}</c>, by name;
    /// each resolves to <c>&lt;templatesDir&gt;/packs/&lt;name&gt;.md</c>. Use them to carry house style and
    /// the mistakes agents habitually make in this codebase's domain (e.g. <c>dotnet-engineer</c>,
    /// <c>modern-csharp</c>, <c>agent-pitfalls</c>) rather than restating them in every stage's notes.</summary>
    public List<string> Packs { get; set; } = [];

    /// <summary>Opt-in prompt batteries for bounded context injection (B8.5). null = none.</summary>
    public BatteriesConfig? Batteries { get; set; }
    /// <summary>When true, the agent is instructed to skip its own pre-session build+test ritual
    /// and defer to Conductor's battery, which remains the single source of truth (B10.4). This
    /// saves ~30-50% of agent-output tokens that were spent echoing build/test output that Conductor
    /// re-runs anyway. Default false (back-compat).</summary>
    public bool BatteryCollapse { get; set; }
    /// <summary>Mandated docs to read in order at session start (paths relative to repo root).
    /// Rendered as an ordered list in the session prompt. Empty/null = no list rendered (B1.5).</summary>
    public List<string>? ReadOrder { get; set; }
    /// <summary>Read-only analysis lanes that run concurrently with sessions (B12.1 Tier A).
    /// Each lane spawns an agent in a scratch temp directory — it can never write the working tree.</summary>
    public List<AnalysisLaneConfig> AnalysisLanes { get; set; } = new();
    // KS3.3: `mutatingLanes` used to be declared here. It was parsed, round-tripped by every editor,
    // documented with a seven-field table — and read by no code path in src/, for the whole life of
    // the field. The Tier B machinery it appeared to configure (MutatingLaneRunner, worktree
    // isolation, the merge gate) is real but reachable from exactly one direction:
    // LaneCoordinator.FollowupEntryToMutatingLane, built from .conductor/followups.md after a stage
    // confirms. A plan that declared the block got silence. Removing the property is what makes the
    // silence audible: the key now resolves to nothing, so `plan set` refuses it and `doctor` names
    // it as inert instead of the plan claiming a feature it never had.
    /// <summary>Plan-level workflow definitions keyed by name. When a stage or the plan references
    /// a workflow name not in this dictionary, the built-in definitions (deliver-verify, etc.) are used
    /// as fallbacks (M3.1).</summary>
    public Dictionary<string, WorkflowDefinition>? Workflows { get; set; }
    /// <summary>The default workflow name used for stages that don't specify one. Falls back to
    /// "deliver-verify" when unset (M3.1).</summary>
    public string? DefaultWorkflow { get; set; }

    /// <summary>P0: the declarative pipeline rules block (<see cref="PipelineRules"/>, owned by
    /// Conductor.Planning). Absent = null = every default reproduces the classic behavior exactly —
    /// an existing plan with no `pipeline` block is byte-for-byte unchanged. P1 populates
    /// roles/multi-item, P2 the QA dial.</summary>
    public PipelineRules? Pipeline { get; set; }

    [JsonIgnore] public string PlanFilePath { get; internal set; } = "";
    [JsonIgnore] public string PlanDir => Path.GetDirectoryName(PlanFilePath) ?? ".";
    /// <summary>The per-run scratch and discovery directory INSIDE the working tree: logs,
    /// transcripts, evidence, <c>control-plane.json</c>, the engine lock, and the tracked
    /// deliverables (<c>REPORT.md</c>, <c>followups.md</c>, <c>handovers/</c>). K3.1 took only
    /// <c>run.db</c> out of it — see <see cref="RunDbPath"/>.</summary>
    [JsonIgnore] public string StateDir => Path.Combine(Repo, StateHome.ScratchDirName);

    private StateResolution? _state;

    /// <summary>K3.1: where this plan's history lives — a machine-level home keyed by repo path plus
    /// plan name, not <c>&lt;repo&gt;/.conductor/run.db</c>. Resolved once per loaded plan; the
    /// first resolution imports a pre-K3.1 database if one is sitting in the working tree.</summary>
    [JsonIgnore] public string RunDbPath => ResolveState().RunDbPath;

    /// <summary>The full resolution — path, which precedence rule produced it, and what (if
    /// anything) was imported. Commands that report on state (<c>doctor</c>) want all three;
    /// everything else wants <see cref="RunDbPath"/>.</summary>
    public StateResolution ResolveState() => _state ??= StateHome.Resolve(Repo, Name);
    [JsonIgnore] public string TrackerPath => Path.Combine(Repo, Tracker);
    [JsonIgnore] public bool PerPhaseGates => GatePolicy.Equals("perPhase", StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolve the effective agent config for a stage: stage.Agent overrides plan.Agent
    /// field-by-field via <see cref="AgentConfig.Merge"/>. When both are null/empty a fresh default
    /// is returned so callers never dereference null (B7.1).</summary>
    public AgentConfig ResolveAgent(StageConfig stage)
        => Agent?.Merge(stage.Agent) ?? stage.Agent ?? new AgentConfig();

    /// <summary>Resolve the persona name for a stage — stage.Persona falls back to plan-wide
    /// scrape from stage.Notes (the "Persona: X" hint convention that existed before B7 landed).</summary>
    public string? ResolvePersona(StageConfig stage)
    {
        if (!string.IsNullOrWhiteSpace(stage.Persona)) return stage.Persona;
        // Fall back: parse legacy "Persona: architect" hints from notes (pre-B7 convention)
        if (!string.IsNullOrWhiteSpace(stage.Notes))
        {
            var match = System.Text.RegularExpressions.Regex.Match(stage.Notes,
                @"Persona:\s*(?<persona>[\w-]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.ExplicitCapture,
                ProgressConventions.RegexTimeout);
            if (match.Success) return match.Groups["persona"].Value;
        }
        return null;
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static PlanConfig Load(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
            throw new FileNotFoundException($"Plan file not found: {full}");
        var cfg = JsonSerializer.Deserialize<PlanConfig>(File.ReadAllText(full), JsonOpts)
                  ?? throw new InvalidOperationException($"Plan file is empty: {full}");
        cfg.PlanFilePath = full;
        cfg.ResolveRepoAgainstPlanFile();
        // KS4.1: holdout gates join plan.Gates here, and the location rule keeping their commands out
        // of the agent's working tree is enforced BEFORE Validate, so a plan breaking it never runs.
        HoldoutGateSource.Apply(cfg);
        cfg.Validate();
        return cfg;
    }

    /// <summary>KS3.2 — persist through the parse-preserving writer: only what actually changed is
    /// spliced into the file, so `//` comments, key order, formatting and defaults-the-file-never-
    /// carried all survive every <c>add-stage</c>, import apply and Face edit. This used to be a
    /// whole-file re-serialisation, which dropped every comment and materialised every default.</summary>
    public void Save()
    {
        BumpVersion();
        Core.Planning.PlanDocumentEditor.Save(this);
    }

    public void BumpVersion() => PlanVersion++;

    public void AddStage(StageConfig stage)
    {
        Stages.Add(stage);
        BumpVersion();
    }


    /// <summary>CH1.2 — a plan's <c>repo</c> may be written RELATIVE TO THE PLAN FILE'S OWN DIRECTORY,
    /// so a plan that ships inside the repository it drives (<c>"repo": "../.."</c> from
    /// <c>plans/&lt;era&gt;/</c>) loads on a fresh clone at any path, on any machine. This repo's own
    /// plans carried <c>C:/code/conductor</c> and were therefore loadable on exactly one machine —
    /// three doctor lints that load them failed on every other, CI included.
    /// <para>An absolute value is left exactly as written: a plan that is DRIVING a run names the
    /// checkout it drives, and resolving that against the plan file would be a guess. A Windows
    /// drive-letter path counts as absolute even when read on Linux, where
    /// <see cref="Path.IsPathRooted(string)"/> says otherwise — otherwise the error a Linux reader
    /// gets names a nonsense path it never configured instead of the one the file holds.</para>
    /// <para>Call after <see cref="PlanFilePath"/> is set and before <see cref="Validate"/>. Idempotent.</para></summary>
    internal void ResolveRepoAgainstPlanFile()
    {
        if (RepoAsWritten is not null) return;
        if (string.IsNullOrWhiteSpace(Repo) || LooksAbsolute(Repo)) return;
        if (string.IsNullOrWhiteSpace(PlanFilePath)) return;
        if (Path.GetDirectoryName(Path.GetFullPath(PlanFilePath)) is not { Length: > 0 } dir) return;

        RepoAsWritten = Repo;
        Repo = Path.GetFullPath(Path.Combine(dir, Repo));
    }

    /// <summary>Rooted here, or rooted on the platform that wrote it. <c>C:/code/conductor</c> is not
    /// rooted by Linux's rules, and treating it as relative there turns "this path does not exist"
    /// into a path the operator never typed.</summary>
    private static bool LooksAbsolute(string path) =>
        Path.IsPathRooted(path) || (path.Length >= 3 && path[1] == ':' && (path[2] == '/' || path[2] == '\\'));
}
