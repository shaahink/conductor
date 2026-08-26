using Conductor.Core.Planning;
using System.Globalization;
using System.Text;

using Conductor.Core.Events;
using Conductor.Core.Evidence;
using Conductor.Core.History;
using Conductor.Core.Store;
using Conductor.Models;

namespace Conductor.Core.Integrations.Messaging;

/// <summary>KS11.1 / CHAPAR CH-1 — what a push SAYS, with no idea what will carry it.
///
/// <para>All of this used to live inside <c>TelegramService</c>, which meant a second channel would
/// have re-implemented every line of it, and — the reason it actually mattered — that there was no
/// way to ask "what would this run say about that session?" without standing up an HTTP listener.
/// Nothing here knows about Telegram, chat ids, HTTP, or the send queue: it takes the run's facts
/// and returns text.</para>
///
/// <para>The bodies are moved verbatim from K5.2/K5.4's composition, and KS11.1's goldens are what
/// prove that. HTML is not a Telegram detail — it is the rich-text dialect the seam speaks, and an
/// adapter that cannot render it is the adapter's problem to solve at its own wire.</para></summary>
public sealed partial class MessageComposer
{
    private readonly PlanConfig _plan;
    private readonly RunState _state;
    private readonly IProgressProvider _progress;
    private readonly IRunStore? _store;
    private readonly Action<string> _warn;

    /// <param name="warn">Where a refused notify template goes. A template that names a fact the
    /// event does not have is LOGGED rather than thrown: the notification path is the run's only
    /// voice, and taking it down over a typo in an optional file would be the opposite of the point.</param>
    public MessageComposer(PlanConfig plan, RunState state, IProgressProvider progress,
        IRunStore? store, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        _plan = plan;
        _state = state;
        _progress = progress;
        _store = store;
        _warn = warn ?? (_ => { });
    }

    /// <summary>DV5.1 — the run's own checkout. Handed out because <c>/cloud</c> has to MEASURE a
    /// repo (a cloud session clones from its remote), and the surface must not carry a second copy of
    /// the plan to find out where it is.</summary>
    public string RepoDir => _plan.Repo;

    /// <summary>What this run is called in a sentence — the plan's name, or the word conductor.</summary>
    public string RunLabel => string.IsNullOrWhiteSpace(_plan.Name) ? "conductor" : _plan.Name.Trim();

    // ────────────────────────────── the composed bodies ──────────────────────────────

    /// <summary>K5.2 — the session-end body, rebuilt from the owner's own transcribed run (15
    /// sessions, $97.46, five defects).
    /// <para>The session number is printed ONCE and comes from the record, not from the live
    /// counter: the identity line stamped at the wire carries <see cref="SessionEndPush.Number"/>,
    /// and this body no longer opens with a second copy that a late push could disagree with.</para>
    /// <para>The stage carries its title. The result is RENDERED from the K5.1 contract rather than
    /// re-cut — the caller hands over the record whole and the bounding happens here, once. A
    /// rollover says what it landed and that its gates are deferred, not "(not recorded)". And every
    /// push carries a progress line, which fifteen messages of that run did not have between
    /// them.</para></summary>
    public Task<string> SessionEndAsync(SessionEndPush push)
    {
        ArgumentNullException.ThrowIfNull(push);

        // K5.4: the outcome leads. The stage and its title moved to the context line the stamp
        // applies to EVERY push, so this no longer renders them a second time. Money carries its
        // headroom, and the composition itself is a template the owner can replace.
        var cost = MoneyLine.ForSession(push.CostUsd, _state.TotalCostUsd, CostCeiling())
                 + (push.Score is { } score ? " · score " + EscapeHtml($"{score:0}/100") : "");
        var result = SessionResult.Parse(push.ResultSummary);

        return ComposeAsync("session-end", NotifyDefaults.SessionEnd, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["outcome"] = EscapeHtml(push.Outcome),
            ["duration"] = push.Duration is { } d ? " · " + EscapeHtml(Elapsed(d)) : "",
            ["landed"] = LandedLine(push),
            ["result"] = RemoteLinks.LinkifyPullRequests(ResultLines(push.ResultSummary), Remote()),
            // KS11.3 / CH-5: the proof line — the gate verdict and the artifact that shows it,
            // together, because "what landed" and "what proves it" are the two halves of a claim and
            // reading them three lines apart is how a reader stops checking the second one.
            ["proof"] = ProofLine(GatesLine(push), result.Evidence),
            ["telemetry"] = Telemetry(TelemetryFacts(push.Stage, push.CostUsd, push.Score)),
            ["report"] = ReportLink(),
            // Kept for owner templates written against K5.4's shape. Nothing in the built-in uses
            // them any more; an override that names one still renders instead of being refused.
            ["progress"] = EscapeHtml(ProgressLine(push.Stage)),
            ["gates"] = EscapeHtml(GatesLine(push)),
            ["cost"] = cost,
        });
    }

    /// <summary>K5.4 — the run is over, said in the order the owner reads it: what happened, what it
    /// cost against its cap, how much of the plan actually landed, how long it took, and where the
    /// report is. The repo, the branch and the stage ride the context line like every other push, so
    /// none of them is spelled out here a second time.</summary>
    public Task<string> RunCompleteAsync(RunCompletePush push)
    {
        ArgumentNullException.ThrowIfNull(push);

        var clean = push.SkippedStages.Count == 0;
        return ComposeAsync("run-complete", NotifyDefaults.RunComplete, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // "COMPLETE" over three skipped stages is a lie of omission, so the headline itself says it.
            ["outcome"] = clean ? "run complete" : "run complete, with stages skipped",
            ["duration"] = push.Duration is { } d ? " · " + EscapeHtml(Elapsed(d)) : "",
            ["checkpoints"] = EscapeHtml(
                $"{push.CheckpointsDone}/{push.CheckpointsTotal} checkpoints · {push.Sessions} session"
                + (push.Sessions == 1 ? "" : "s")),
            ["skipped"] = clean ? "" : EscapeHtml($"skipped: {string.Join(", ", push.SkippedStages)}"),
            ["telemetry"] = Telemetry(RunTelemetryFacts()),
            ["report"] = ReportLink(),
            ["cost"] = MoneyLine.ForRun(_state.TotalCostUsd, CostCeiling()),
        });
    }

    /// <summary>DV1.2 — ONE owner-queue obligation, composed in CH-5's grammar.
    ///
    /// <para>The queue has been regenerated at every session boundary since SF4.1 and reached nobody:
    /// <c>.conductor/OWNER-QUEUE.md</c> and <c>GET /owner/queue</c> both require someone to be
    /// LOOKING, and the case the surface was written for is the one where nobody is. This is the push
    /// half — and it is composed through <see cref="NotifyTemplate"/> like every other push, so the
    /// owner can reshape it without rebuilding the engine that is driving their run.</para>
    ///
    /// <para><b>The age is the telemetry.</b> Every other push spends that line on money; an
    /// obligation's number is how long it has been sitting there, which is the fact that decides
    /// whether the owner deals with it now. A source that cannot date itself says so — a queue entry
    /// that reads "just now" when it may be six hours old is worse than one that admits it does not
    /// know (<see cref="OwnerQueueItem.SinceUtc"/>).</para></summary>
    public Task<string> OwnerQueueItemAsync(OwnerQueueItem item, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(item);

        var age = item.AgeSeconds(nowUtc) is { } secs
            ? "waiting " + Elapsed(TimeSpan.FromSeconds(secs))
            : "waiting — age unknown";

        return ComposeAsync("owner-queue", NotifyDefaults.OwnerQueueItem,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["headline"] = EscapeHtml(item.Title),
                ["unblocks"] = EscapeHtml("unblocks: " + item.Unblocks),
                ["why"] = item.Detail is { Length: > 0 } d ? EscapeHtml("why you: " + d) : "",
                // The one line a reader acts on. Empty command is a FACT for a blocked-until wait,
                // not a gap, and inventing one would send the owner to a keyboard for nothing.
                ["clears"] = item.Command.Length > 0
                    ? "clears with: <code>" + EscapeHtml(item.Command) + "</code>"
                    : "clears with: nothing to type — it clears itself",
                ["telemetry"] = Telemetry(age),
            });
    }

    /// <summary>The caption that rides the file. Bounded by the caption limit — a quarter of the
    /// message limit — so it is composed short rather than clipped from a body.</summary>
    public Task<string> EvidenceCaptionAsync(EvidenceArtifact a, int batchSize)
    {
        ArgumentNullException.ThrowIfNull(a);
        return ComposeAsync("evidence", NotifyDefaults.Evidence, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["batch"] = batchSize > 1 ? $" ({batchSize.ToString(CultureInfo.InvariantCulture)} new)" : "",
            ["artifact"] = EvidenceLine(a),
            ["telemetry"] = Telemetry(TelemetryFacts(a.StageId, null, null)),
            ["progress"] = EscapeHtml(ProgressLine(a.StageId)),
        });
    }

    /// <summary>DV6.3 — the caption on the board page. Composed from the SAME snapshot the page is
    /// rendered from, so the two cannot disagree about what the board said.</summary>
    public Task<string> BoardCaptionAsync(Publishing.BoardSnapshot snap)
    {
        ArgumentNullException.ThrowIfNull(snap);
        var s = snap.State;
        return ComposeAsync("board", NotifyDefaults.Board, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["headline"] = EscapeHtml(string.Create(CultureInfo.InvariantCulture,
                $"board — {s.DoneCount} of {s.TotalCount} checkpoints done")),
            // A document in a chat cannot say how old it is; this line is the whole reason a reader
            // scrolling back a week does not mistake last Tuesday's board for today's.
            ["stale"] = EscapeHtml(string.Create(CultureInfo.InvariantCulture,
                $"as of {snap.RenderedUtc:yyyy-MM-dd HH:mm} UTC · {snap.Boundary} · it does not update")),
            ["owner"] = snap.Owner.Count > 0
                ? EscapeHtml(string.Create(CultureInfo.InvariantCulture,
                    $"{snap.Owner.Count} item{(snap.Owner.Count == 1 ? "" : "s")} need you"))
                : "",
            ["ledger"] = snap.LedgerLine.Length > 0 ? EscapeHtml(snap.LedgerLine) : "",
            ["telemetry"] = Telemetry(TelemetryFacts(s.StageId, null, null)),
        });
    }

    /// <summary>The artifacts beyond the upload budget, announced exactly as K5.3 announced them.</summary>
    public Task<string> EvidenceOverflowAsync(IReadOnlyList<EvidenceArtifact> rest)
    {
        ArgumentNullException.ThrowIfNull(rest);

        var lines = new StringBuilder();
        foreach (var a in rest.Take(EvidenceLinesPerPush)) lines.AppendLine("• " + EvidenceLine(a));
        if (rest.Count > EvidenceLinesPerPush) lines.Append($"+{rest.Count - EvidenceLinesPerPush} more");

        return ComposeAsync("evidence-overflow", NotifyDefaults.EvidenceOverflow,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["count"] = rest.Count.ToString(CultureInfo.InvariantCulture),
                ["noun"] = rest.Count == 1 ? "artifact" : "artifacts",
                ["lines"] = lines.ToString().TrimEnd(),
            });
    }

    /// <summary>K5.4 — one call for every composed push: the owner's template if there is a usable
    /// one, the built-in otherwise.</summary>
    private Task<string> ComposeAsync(string eventName, string builtIn, IReadOnlyDictionary<string, string> facts) =>
        NotifyTemplate.RenderAsync(eventName, builtIn, facts, _plan.PlanDir, _plan.TemplatesDir, _warn);

    // ────────────────────────────── the stamp ──────────────────────────────

    /// <summary>FU-OWNER-11 — the two facts a message cannot recover on its own: WHICH plan sent it
    /// and WHICH session it belongs to. One chat can receive two machines' runs, so an unattributed
    /// line is unreadable; and a message read hours later has no other way to be placed in the run's
    /// history.
    /// <para>Read off the LIVE plan and state rather than a constructor snapshot: a reload can rename
    /// the plan (SC1.3) and the session counter moves under every message.</para></summary>
    public string IdentityLine => IdentityFor(null);

    /// <summary>K5.2: ONE source for the session number. A session-end push passes the record's own
    /// number, which is the truthful one for a message about that session; everything else falls back
    /// to the live counter.</summary>
    public string IdentityFor(int? sessionNumber)
    {
        var name = string.IsNullOrWhiteSpace(_plan.Name) ? "conductor" : _plan.Name.Trim();
        return FormattableString.Invariant(
            $"<i>{EscapeHtml(name)} · s{sessionNumber ?? _state.SessionCounter}</i>");
    }

    /// <summary>K5.4 — the second half of the stamp. FU-OWNER-11 put the plan and the session on
    /// every message; what it still could not answer is WHICH CHECKOUT and WHICH WORK. One chat can
    /// carry two clones of the same plan on two branches, and a message that names neither is
    /// unreadable in exactly the way the identity line was invented to fix.
    /// <para>Empty — not a row of separators — when there is no repo, no branch, no stage and no
    /// tracker to read.</para></summary>
    public string ContextLine(string? stageId = null)
    {
        var parts = new List<string>(3);

        var repo = RepoLabel();
        var branch = Branch();
        if (repo.Length > 0) parts.Add(branch.Length > 0 ? $"{repo}@{branch}" : repo);

        // The message's OWN stage wins over the run's: a session-end push composed while the run has
        // already moved on is about the stage it names, not the stage the engine is now in.
        var stage = string.IsNullOrWhiteSpace(stageId) ? _state.CurrentStage : stageId;
        if (!string.IsNullOrWhiteSpace(stage)) parts.Add(StageLabel(stage));

        var checkpoint = CurrentCheckpoint(stage);
        if (checkpoint is { Length: > 0 }) parts.Add(checkpoint);

        return parts.Count == 0 ? "" : $"<i>{EscapeHtml(string.Join(" · ", parts))}</i>";
    }

    /// <summary>Identity, then context — the block every outbound message opens with. The first line
    /// is unchanged from FU-OWNER-11 on purpose: it is what every other surface and test recognises a
    /// conductor push by.</summary>
    public string Stamp(int? sessionNumber, string? stageId = null)
    {
        var context = ContextLine(stageId);
        return context.Length > 0 ? IdentityFor(sessionNumber) + "\n" + context : IdentityFor(sessionNumber);
    }

    // ────────────────────────────── the fragments ──────────────────────────────

    /// <summary>The stage as an id AND a title. It was rendered as a bare letter — "— G" — because
    /// the id was passed and the title was never looked up.</summary>
    public string StageLabel(string stageId)
    {
        if (string.IsNullOrWhiteSpace(stageId)) return "-";
        var title = _plan.Stages.FirstOrDefault(s =>
            string.Equals(s.Id, stageId, StringComparison.OrdinalIgnoreCase))?.Title;
        return string.IsNullOrWhiteSpace(title) ? stageId : $"{stageId} — {Clip(title.Trim(), 64)}";
    }

    /// <summary>Where the run is, in one line. Fifteen messages of the owner's run carried no
    /// checkpoint count, no stage progress and no ETA between them.</summary>
    public string ProgressLine(string? stageId)
    {
        TrackerSnapshot track;
        try { track = _progress.Read(_plan, CancellationToken.None); }
        catch (IOException) { return ""; }
        catch (InvalidOperationException) { return ""; }

        if (track.Checkpoints.Count == 0) return "";
        var line = $"progress: {track.Checkpoints.Count(c => c.IsDone)}/{track.Checkpoints.Count} checkpoints";

        var stage = string.IsNullOrWhiteSpace(stageId) ? _state.CurrentStage : stageId;
        if (!string.IsNullOrWhiteSpace(stage))
        {
            var rows = track.ForStage(stage).ToList();
            if (rows.Count > 0) line += $" · {stage} {rows.Count(c => c.IsDone)}/{rows.Count}";
        }
        return line;
    }

    /// <summary>KS5.4 — the ceiling this run is GOVERNED by: the plan's <c>limits.maxRunCostUsd</c>
    /// plus every dollar an owner has approved on top of it, through the one function the cap check,
    /// <c>/state</c>, doctor and the run report all read.</summary>
    private decimal? CostCeiling() =>
        Core.Budget.BudgetCeiling.EffectiveCostCap(_plan.Limits.MaxRunCostUsd, _state.BudgetGrantUsd);

    /// <summary>Only outcomes the owner can do something about are allowed to buzz.</summary>
    public static PushSeverity SessionSeverity(string outcome) =>
        outcome.Contains("Attention", StringComparison.OrdinalIgnoreCase)
        || outcome.Contains("Blocked", StringComparison.OrdinalIgnoreCase)
        || outcome.Contains("Failed", StringComparison.OrdinalIgnoreCase)
            ? PushSeverity.Alert : PushSeverity.Quiet;

    /// <summary>The report where a phone can read it, or nothing at all — the template drops the line
    /// rather than printing a dead link on a repo with no remote.</summary>
    private string ReportLink() =>
        RemoteLinks.Report(Remote(), Branch()) is { } url
            ? $"<a href=\"{EscapeHtml(url)}\">the run's report</a>"
            : "";

    private static string EvidenceLine(EvidenceArtifact a)
    {
        var where = a.CheckpointId is { Length: > 0 } cp ? $" — {cp}" : "";
        return $"{EscapeHtml(a.Path)} ({a.Kind}, {Size(a.Bytes)}){EscapeHtml(where)}";
    }

    /// <summary>An artifact path is repo-relative when the file is inside the repo and absolute when
    /// it is not (K5.3). The wire needs an absolute one, and a path that no longer resolves must
    /// degrade to the text line rather than throwing inside a fire-and-forget push.</summary>
    public string? ResolveArtifact(string path)
    {
        try
        {
            if (Path.IsPathRooted(path)) return File.Exists(path) ? path : null;
            var joined = Path.GetFullPath(Path.Combine(_plan.Repo, path));
            return File.Exists(joined) ? joined : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }
    }

    /// <summary>How many artifacts of one batch are sent as files. A watcher sweep that finds thirty
    /// screenshots must not send thirty photos; the rest are announced exactly as K5.3 announced
    /// them.</summary>
    public const int EvidenceFilesPerPush = 4;

    private const int EvidenceLinesPerPush = 8;

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };

    /// <summary>A rollover runs no gate battery and burns no attempt — that is what a rollover MEANS
    /// (K1.1) — so "(not recorded)" reads as a fault where there is none.</summary>
    private static string GatesLine(SessionEndPush push) =>
        !string.IsNullOrWhiteSpace(push.GateSummary) ? push.GateSummary
        : push.IsRollover ? "deferred — the session rolled over, no attempt burned"
        : "(not recorded)";

    /// <summary>What the session actually put on disk. K1.1 records commits and claims on the
    /// rollover path too; until K5.2 nothing rendered them, so a rollover that had shipped a pull
    /// request pushed a message that said nothing at all.</summary>
    /// <remarks>K5.4: the commits are LINKS when the repo has a remote — a sha in a chat is a string
    /// the owner has to carry back to a machine.</remarks>
    private string LandedLine(SessionEndPush push)
    {
        var parts = new List<string>(2);
        if (push.Commits > 0)
        {
            var count = $"{push.Commits} commit{(push.Commits == 1 ? "" : "s")}";
            var shas = push.CommitShas ?? [];
            parts.Add(shas.Count == 0
                ? count
                : count + " (" + string.Join(", ",
                    shas.Take(CommitLinksPerPush).Select(s => RemoteLinks.Commit(Remote(), s))) +
                    (shas.Count > CommitLinksPerPush ? ", …)" : ")"));
        }
        if (push.NewlyDone.Count > 0) parts.Add($"claimed {EscapeHtml(string.Join(", ", push.NewlyDone))}");
        return parts.Count > 0 ? "landed: " + string.Join(" · ", parts) : "";
    }

    private const int CommitLinksPerPush = 3;

    /// <summary>A duration a human reads at a glance — <c>1h 12m</c>, not <c>01:12:34.567</c>.</summary>
    public static string Elapsed(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m"
        : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m"
        : $"{(int)d.TotalSeconds}s";

    /// <summary>K5.1's structure, rendered. The caller passes the record WHOLE — cutting it here,
    /// once, is the difference between a bounded message and the same paragraph cut twice.</summary>
    private static string ResultLines(string? resultSummary)
    {
        var parsed = SessionResult.Parse(resultSummary);
        if (!parsed.IsStructured)
        {
            var raw = parsed.ToCompact(ResultMaxChars);
            return raw.Length > 0 ? "result: " + EscapeHtml(raw) : "";
        }

        var sb = new StringBuilder();
        sb.Append("result: <b>").Append(EscapeHtml(parsed.Headline)).Append("</b>");
        foreach (var o in parsed.Outcomes) sb.Append("\n  • ").Append(EscapeHtml(o));
        if (parsed.Gaps.Length > 0) sb.Append("\ngaps: ").Append(EscapeHtml(parsed.Gaps));
        // KS11.3 / CH-5: evidence is not part of the result block any more — it is half the PROOF
        // line, beside the gate verdict, where a reader looking for "what shows this" finds both.
        return Clip(sb.ToString(), ResultMaxChars);
    }

    private const int ResultMaxChars = 900;

    public static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public static string EscapeHtml(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    // ── the checkout facts, both of which cost a git process ──

    private string RepoLabel()
    {
        try { return RunHistory.RepoLabel(_plan.Repo) ?? ""; }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return ""; }
    }

    /// <summary>The checkpoint the run is on: the one marked in progress, else the next one not done.
    /// The tracker is the same view the Face and the report read, so a push cannot claim a checkpoint
    /// the board disagrees with.</summary>
    private string? CurrentCheckpoint(string? stageId)
    {
        if (string.IsNullOrWhiteSpace(stageId)) return null;
        try
        {
            var rows = _progress.Read(_plan, CancellationToken.None).ForStage(stageId).ToList();
            return (rows.FirstOrDefault(c => c.IsInProgress) ?? rows.FirstOrDefault(c => !c.IsDone))?.Id;
        }
        catch (IOException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private string _branch = "";
    private DateTime _branchReadUtc = DateTime.MinValue;
    private static readonly TimeSpan GitFactTtl = TimeSpan.FromSeconds(30);

    /// <summary>Shelling out to git on every message would put a process between the engine and each
    /// push; a stage that switches branch is still reflected within <see cref="GitFactTtl"/>, which
    /// is far finer than the interval a human reads a chat at.</summary>
    private string Branch()
    {
        if (DateTime.UtcNow - _branchReadUtc < GitFactTtl) return _branch;
        _branchReadUtc = DateTime.UtcNow;
        try { _branch = Git.Branch(_plan.Repo) ?? ""; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { _branch = ""; }
        // A detached HEAD answers "HEAD", which names nothing — better to say nothing.
        if (string.Equals(_branch, "HEAD", StringComparison.Ordinal)) _branch = "";
        return _branch;
    }

    private string? _remote;
    private bool _remoteRead;

    /// <summary>The remote this run's links point at. <see cref="Reporter"/> memoizes the git call
    /// itself; this only avoids asking on every single push.</summary>
    public string? Remote()
    {
        if (_remoteRead) return _remote;
        _remoteRead = true;
        try { _remote = Reporter.RemoteUrl(_plan.Repo); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { _remote = null; }
        return _remote;
    }
}
