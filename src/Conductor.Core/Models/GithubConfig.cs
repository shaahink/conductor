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

    /// <summary>Mirror the run's diary as one issue with one comment per finished session. True by
    /// default: the board says what the work IS, and the diary says what happened to it.</summary>
    public bool RunHistoryIssue { get; set; } = true;

    /// <summary>Post the run report as a pull-request comment when the run's branch has one open.
    /// Off by default — a comment on someone's PR is the loudest thing this integration can do.</summary>
    public bool ReportAsPrComment { get; set; }

    /// <summary>Prefix for every label conductor owns, so a mirror never fights the repo's own
    /// labels. <c>conductor</c> yields <c>conductor:status:done</c>.</summary>
    public string LabelPrefix { get; set; } = "conductor";
}
