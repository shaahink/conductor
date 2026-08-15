namespace Conductor.Models;

/// <summary>
/// KS9.1 — the plan's <c>github</c> block: where a run's board is MIRRORED to, and nothing else.
///
/// <para><b>Off by default, and off means absent.</b> <see cref="PlanConfig.Github"/> is null on
/// every plan written before this checkpoint and on every plan that does not ask for it, and the
/// engine's behaviour with a null block is byte-identical to the behaviour before the block existed.
/// A present block with <see cref="Enabled"/> false is the same thing said out loud.</para>
///
/// <para><b>One direction only.</b> L6.3 / D-7 / ADR 0005 decided this: conductor PUSHES. Nothing
/// here configures a read, a webhook or a poll, because there is no code path that reads GitHub
/// state back into run state — the tracker stays the verified contract and
/// <c>Events/TaskWrites.cs</c> stays the only writer of task state. Dragging a card on GitHub
/// changes nothing in the run, on purpose.</para>
///
/// <para><b>Added in the same checkpoint as its reader.</b> NEXT-FEATURES.md:115 names the
/// <c>mutatingLanes</c> anti-pattern by name — plan config that nothing consumes, which reads as a
/// feature and is a comment. Every field below is read by <c>GithubBoardSync</c>,
/// <c>GithubIdentity</c> or <c>GithubCommand</c> in the checkpoint that introduced it.</para>
/// </summary>
public sealed class GithubConfig
{
    /// <summary>The master switch. False (the default) means the mirror never runs, even with a
    /// token present and a repo named.</summary>
    public bool Enabled { get; set; }

    /// <summary><c>owner/name</c> of the repository to mirror INTO. Empty = derive it from the
    /// plan repo's <c>origin</c> remote (<c>GithubIdentity.Resolve</c>). A scratch mirror is exactly
    /// what this field is for: the board does not have to live in the repo being worked on.</summary>
    public string Repo { get; set; } = "";

    /// <summary><c>issues</c> (the default) or <c>issues+project</c>. The second requires
    /// <see cref="ProjectNumber"/> and a token carrying the <c>project</c> scope — KS9.3.</summary>
    public string Board { get; set; } = "issues";

    /// <summary>The Projects v2 board number, as it appears in the project URL. 0 = unset, which is
    /// a refusal rather than a silent no-op when <see cref="Board"/> asks for a project.</summary>
    public int ProjectNumber { get; set; }

    /// <summary>KS9.2 — reconcile the board AS THE RUN GOES, at the boundaries the engine already
    /// treats as boundaries, instead of only on a manual <c>github sync --backfill</c>. True by
    /// default, but only ever consulted under <see cref="Enabled"/>: a plan that has not opted in has
    /// no mirror to switch off. Set false to keep the backfill and nothing else.</summary>
    public bool LiveMirror { get; set; } = true;

    /// <summary>Mirror the run's diary as one issue with one comment per finished session. True by
    /// default: the board says what the work IS, and the diary says what happened to it.</summary>
    public bool RunHistoryIssue { get; set; } = true;

    /// <summary>Post the run report as a pull-request comment when the run's branch has one open.
    /// Off by default — a comment on someone's PR is the loudest thing this integration can do.</summary>
    public bool ReportAsPrComment { get; set; }

    /// <summary>Prefix for every label conductor owns, so a mirror never fights the repo's own
    /// labels. <c>conductor</c> yields <c>conductor:status:done</c>.</summary>
    public string LabelPrefix { get; set; } = "conductor";

    /// <summary>The only two board spellings. Anything else is refused by name rather than
    /// silently read as the default — a typo that quietly downgrades a board to issues-only is
    /// indistinguishable, from the outside, from a project mirror that ran and did nothing.</summary>
    public const string BoardIssues = "issues";

    /// <inheritdoc cref="BoardIssues"/>
    public const string BoardIssuesAndProject = "issues+project";

    /// <summary>True when this block asks for a Projects v2 board on top of the issue board. An
    /// unknown <see cref="Board"/> value is NOT this — it is <see cref="BoardRefusal"/>'s business,
    /// so a misspelling can never arrive here as a quiet false.</summary>
    public bool WantsProjectBoard =>
        string.Equals(Board?.Trim(), BoardIssuesAndProject, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// KS9.3 — the config gate, as a sentence rather than as a branch in a command, so the bar
    /// "refuses BY NAME, never a silent no-op" is asserted against the text itself. null means the
    /// board block is coherent and the caller may proceed.
    ///
    /// <para>Absent is not wrong: a null or blank <see cref="Board"/> is the default issue board,
    /// because a plan written before this field existed must keep behaving exactly as it did. A
    /// value that was TYPED and is not one of the two spellings is always wrong.</para>
    /// </summary>
    public string? BoardRefusal()
    {
        var board = Board?.Trim();
        if (string.IsNullOrEmpty(board)) return null;

        var issues = string.Equals(board, BoardIssues, StringComparison.OrdinalIgnoreCase);
        if (!issues && !WantsProjectBoard)
            return $"github.board '{board}' is not a board. it is '{BoardIssues}' or '{BoardIssuesAndProject}'.";

        // Reached only for issues+project: the project half needs a number, and 0 is what the field
        // holds when nobody set one. docs/plan-config.md has promised this refusal since KS9.1.
        if (WantsProjectBoard && ProjectNumber <= 0)
            return $"github.board is '{BoardIssuesAndProject}' but github.projectNumber is {ProjectNumber}. " +
                "set it to the Projects v2 board number from the project url " +
                "(github.com/users/<owner>/projects/<number>).";

        return null;
    }
}
