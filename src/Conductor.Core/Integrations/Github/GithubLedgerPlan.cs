using System.Globalization;
using System.Text;
using Conductor.Core.Store;

namespace Conductor.Core.Integrations.Github;

/// <summary>
/// DV6.1 — ONE ledger entry as an issue: a tracked bug, or a followups.md row.
///
/// <para><b>Why not a <see cref="GithubCard"/>.</b> A card is a checkpoint, and a checkpoint's issue
/// is closed when the checkpoint is done and RETIRED when the plan stops declaring it — both of which
/// happen inside the life of one run. A ledger entry has the opposite lifetime: it is opened when it
/// is filed and closed only when the ledger itself says it is closed, which is routinely a different
/// run and sometimes a different era. Giving it its own type is what keeps the retire sweep — which
/// reads the task marker — structurally unable to reach it.</para>
/// </summary>
/// <param name="Key">The local map's key: <c>bug:12</c> or <c>followup:FU-B11-3</c>. Distinct
/// namespaces, so a bug id can never collide with a followup id.</param>
/// <param name="Marker">The HTML comment planted in the body — the identity a human can read and the
/// only thing a later pass matches on.</param>
/// <param name="CreateIfMissing">False for an entry the ledger says is already closed. A bug fixed
/// before any mirror ever saw it must not mint an issue purely to close it: the board would fill with
/// history nobody asked for, on a repository where the point is what is still OPEN.</param>
public sealed record GithubLedgerCard(
    string Key,
    string Marker,
    string Title,
    string Body,
    List<string> Labels,
    bool Closed,
    bool CreateIfMissing);

/// <summary>
/// DV6.1 — what the bug ledger and followups.md should look like on the mirror, decided from the
/// LOCAL ledger alone and with no HTTP in sight (the same split <see cref="GithubBoardPlan"/> makes,
/// for the same reason).
///
/// <para><b>This is the class that survives the run.</b> <c>OpenBugsReport</c> and
/// <c>SF04BugsOutliveTheirRunTests</c> already made the DATA outlive its run — every open bug in this
/// <c>run.db</c>, whichever run filed it, is carried into the next run's prompts. What did not
/// survive was any way to SEE it without the machine: the ledger is real, durable and invisible.
/// These issues are the visible half, and they inherit the data's lifetime rather than the run's.</para>
/// </summary>
public static class GithubLedgerPlan
{
    /// <summary>The label every bug issue carries — the thing that makes the class filterable.</summary>
    public static string BugLabel(string prefix) => Prefix(prefix) + ":bug";

    /// <summary>The label every followup issue carries.</summary>
    public static string FollowupLabel(string prefix) => Prefix(prefix) + ":followup";

    private static string Prefix(string prefix) =>
        string.IsNullOrWhiteSpace(prefix) ? "conductor" : prefix.Trim();

    /// <summary>The desired ledger: every bug in this database and every row in followups.md.
    ///
    /// <para>ALL bugs are passed in, not just the open ones, and the difference is
    /// <see cref="GithubLedgerCard.CreateIfMissing"/>: an open entry is created if it has no issue,
    /// and a closed one is only ever used to CLOSE an issue that already exists.</para></summary>
    public static List<GithubLedgerCard> Cards(
        IEnumerable<CarriedBugRow> bugs, IEnumerable<FollowupEntry> followups, string labelPrefix)
    {
        ArgumentNullException.ThrowIfNull(bugs);
        ArgumentNullException.ThrowIfNull(followups);
        var prefix = Prefix(labelPrefix);
        var cards = new List<GithubLedgerCard>();
        foreach (var b in bugs) cards.Add(CardFor(b.Bug, b.PlanName, prefix));
        foreach (var f in followups) cards.Add(CardFor(f, prefix));
        return cards;
    }

    /// <summary>One tracked bug as an issue.</summary>
    public static GithubLedgerCard CardFor(BugRow bug, string? filedByPlan, string labelPrefix)
    {
        ArgumentNullException.ThrowIfNull(bug);
        var prefix = Prefix(labelPrefix);
        var open = string.Equals(bug.Status, "open", StringComparison.OrdinalIgnoreCase);
        var id = bug.Id.ToString(CultureInfo.InvariantCulture);
        var marker = GithubIdentity.BugMarker(bug.Id);

        var labels = new List<string>
        {
            BugLabel(prefix),
            $"{prefix}:severity:{bug.Severity}",
            $"{prefix}:status:{bug.Status}",
        };
        if (!string.IsNullOrWhiteSpace(bug.StageId)) labels.Add($"{prefix}:stage:{bug.StageId.Trim()}");

        var body = new StringBuilder();
        body.Append(marker).Append('\n').Append('\n');
        if (!string.IsNullOrWhiteSpace(bug.Detail)) body.Append(bug.Detail.Trim()).Append('\n').Append('\n');
        body.Append("**Severity** ").Append(Or(bug.Severity)).Append("  ")
            .Append("**Status** ").Append(Or(bug.Status)).Append("  ")
            .Append("**Stage** ").Append(Or(bug.StageId)).Append('\n');
        body.Append("**Filed by** ").Append(Or(filedByPlan)).Append(" - run ").Append(Short(bug.RunId));
        if (bug.FoundSession is { } found) body.Append(" - session ").Append(found.ToString(CultureInfo.InvariantCulture));
        body.Append("  ").Append("**Filed** ").Append(Or(bug.CreatedAt)).Append('\n');
        body.Append('\n').Append(Footer);

        return new GithubLedgerCard(
            Key: "bug:" + id,
            Marker: marker,
            Title: $"bug #{id} — {Line(bug.Title)}",
            Body: body.ToString(),
            Labels: labels,
            Closed: !open,
            CreateIfMissing: open);
    }

    /// <summary>One followups.md row as an issue.</summary>
    public static GithubLedgerCard CardFor(FollowupEntry entry, string labelPrefix)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var prefix = Prefix(labelPrefix);
        var open = FollowupParser.IsOpen(entry);
        var marker = GithubIdentity.FollowupMarker(entry.Id);

        var labels = new List<string> { FollowupLabel(prefix), $"{prefix}:status:{(open ? "open" : "closed")}" };
        if (IsPlainStage(entry.OwningStage)) labels.Add($"{prefix}:stage:{entry.OwningStage.Trim()}");

        var body = new StringBuilder();
        body.Append(marker).Append('\n').Append('\n');
        if (!string.IsNullOrWhiteSpace(entry.Detail)) body.Append(entry.Detail.Trim()).Append('\n').Append('\n');
        body.Append("**Owning stage** ").Append(Or(entry.OwningStage)).Append('\n');
        body.Append("**Status** ").Append(Or(entry.Status)).Append('\n');
        body.Append('\n').Append("<sub>Row <code>").Append(entry.Id)
            .Append("</code> of <code>.conductor/followups.md</code>.</sub>").Append('\n');
        body.Append('\n').Append(Footer);

        return new GithubLedgerCard(
            Key: "followup:" + entry.Id,
            Marker: marker,
            Title: $"followup {entry.Id} — {Line(entry.Item)}",
            Body: body.ToString(),
            Labels: labels,
            Closed: !open,
            CreateIfMissing: open);
    }

    /// <summary>The sentence that states the lifetime, on every ledger issue. It is here rather than
    /// in a doc because the issue is read where the doc is not — on a phone, months later, by someone
    /// wondering why this one did not close when the run ended.</summary>
    private const string Footer =
        "<sub>Filed by conductor and kept by the LEDGER, not by the run: this issue stays open until " +
        "the ledger says the entry is closed, which is often a later run. Nothing here is ever read " +
        "back into a run.</sub>";

    /// <summary>An owning-stage cell fit to be a label. followups.md carries prose in that column —
    /// one real row owns the stage <c>HUMAN: (Shamshir run)</c> — and a label made of prose is one
    /// nobody can filter on and that GitHub may refuse outright.</summary>
    private static bool IsPlainStage(string? stage) =>
        (stage ?? "").Trim() is { Length: > 0 and <= 12 } s && s.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');

    /// <summary>A title is one line. A bug detail pasted into a title would make an issue list
    /// unreadable, and GitHub silently accepts it.</summary>
    private static string Line(string? text)
    {
        var s = (text ?? "").ReplaceLineEndings("\n");
        var cut = s.IndexOf('\n', StringComparison.Ordinal);
        if (cut >= 0) s = s[..cut];
        s = s.Trim();
        return s.Length <= 120 ? s : s[..117].TrimEnd() + "...";
    }

    private static string Short(string runId) => runId.Length > 12 ? runId[..12] : runId;

    private static string Or(string? s) => string.IsNullOrWhiteSpace(s) ? "-" : s.Trim();
}
