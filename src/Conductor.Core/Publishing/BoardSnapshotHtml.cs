using System.Globalization;
using System.Text;
using Conductor.Core.Http;
using Conductor.Core.Integrations.Github;

namespace Conductor.Core.Publishing;

/// <summary>
/// DV6.3 — the board as ONE self-contained HTML file: columns, cards, age-in-column, cost, the
/// owner's queue, the ledger line and the evidence, rendered from the contracts in
/// <see cref="BoardSnapshot"/> and complete in itself.
///
/// <para><b>Publish, don't serve.</b> ADR-0005 rules out inbound — no port, no tunnel, no reverse
/// proxy — and the loopback control plane carries <c>/control</c>, so an inbound route to the read
/// view is an inbound route to the steering wheel. A file has no route. It is rendered at a
/// boundary, pushed OUT as a Telegram document, and read wherever it lands; the machine that made
/// it need not be reachable, or even switched on.</para>
///
/// <para><b>Self-contained means self-contained.</b> One document: styles inline, no script, no
/// font, no image, no link that resolves anywhere. A page that fetches a stylesheet renders as
/// unstyled text on a phone in a tunnel, and a page that fetches ANYTHING tells a third party when
/// the owner read their own board. Pinned by
/// <c>DV6_3BoardPageTests.The_page_reaches_out_to_nothing</c>.</para>
///
/// <para><b>It states its own staleness.</b> Every other surface here is live and can say "now"; a
/// file cannot, and a file that looks live is worse than no file. The first thing under the title
/// is when it was rendered, which boundary rendered it, and the sentence that it does not
/// update.</para>
///
/// <para><b>The columns are DV6.2's columns.</b> The names come from
/// <see cref="GithubProjectColumns.Preferences"/> — first choice per status — so the page and the
/// Projects v2 mirror cannot grow two vocabularies for the same five statuses.</para>
/// </summary>
public static class BoardSnapshotHtml
{
    /// <summary>What the file is called wherever it is written or attached. One name, so the run,
    /// the chat and the owner are all looking at the same thing.</summary>
    public const string FileName = "board.html";

    /// <summary>The statuses that get a column, in board order. <c>archived</c> is deliberately
    /// absent: W1.2 took those items off the board, and a snapshot of the board is not the place to
    /// put them back.</summary>
    public static IReadOnlyList<string> ColumnStatuses { get; } =
        ["todo", "in_progress", "blocked", "done", "skipped"];

    /// <summary>The board's own spelling of a status — DV6.2's first choice for it.</summary>
    public static string ColumnName(string status) =>
        GithubProjectColumns.Preferences(status) is { Count: > 0 } p ? p[0] : status;

    public static string Render(BoardSnapshot snap)
    {
        ArgumentNullException.ThrowIfNull(snap);
        var sb = new StringBuilder(16 * 1024);
        sb.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"<title>{Esc(snap.State.PlanName)} — board as of {Stamp(snap.RenderedUtc)}</title>\n");
        sb.Append("<style>\n").Append(Css).Append("\n</style>\n</head>\n<body>\n");
        Header(sb, snap);
        Owner(sb, snap);
        Board(sb, snap);
        Evidence(sb, snap);
        Footer(sb, snap);
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    // ───────────────────────────────── the sections ─────────────────────────────────

    /// <summary>The title, the staleness claim, and the run's headline numbers.</summary>
    private static void Header(StringBuilder sb, BoardSnapshot snap)
    {
        var s = snap.State;
        sb.Append("<header>\n");
        sb.Append(CultureInfo.InvariantCulture, $"<h1>{Esc(s.PlanName)}</h1>\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"<p class=\"stale\"><b>as of {Stamp(snap.RenderedUtc)}</b> · rendered at {Esc(snap.Boundary)}.");
        sb.Append(" This page does not update: it is a snapshot, stale by at most one boundary, and the run"
                + " may have moved on since.</p>\n");

        sb.Append("<ul class=\"facts\">\n");
        Fact(sb, "status", s.Status + (string.IsNullOrWhiteSpace(s.AttentionReason) ? "" : " — " + s.AttentionReason));
        Fact(sb, "stage", string.IsNullOrWhiteSpace(s.StageTitle) ? s.StageId : s.StageId + " · " + s.StageTitle);
        Fact(sb, "checkpoints", FormattableString.Invariant($"{s.DoneCount} of {s.TotalCount} done"));
        Fact(sb, "cost", Cost(s));
        Fact(sb, "sessions", s.SessionNumber.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(s.GateSummary)) Fact(sb, "gates", s.GateSummary);
        if (snap.LedgerLine.Length > 0) Fact(sb, "ledger", snap.LedgerLine);
        sb.Append("</ul>\n</header>\n");
    }

    /// <summary>DV1.2's obligations, with the exact command that clears each one. The queue answers
    /// "what do I have to do", which is the only half of this page with a deadline — so it is above
    /// the board, not under it.</summary>
    private static void Owner(StringBuilder sb, BoardSnapshot snap)
    {
        var q = snap.Owner;
        sb.Append("<section class=\"owner\">\n");
        sb.Append(CultureInfo.InvariantCulture, $"<h2>Owner queue — {Count(q.Count, "item")}</h2>\n");
        if (q.Count == 0)
        {
            // Zero is a real answer (OwnerQueueDto's own rule) and it is said out loud rather than
            // rendered as an absent section a reader would have to interpret.
            sb.Append("<p class=\"none\">Nothing is waiting on the owner.</p>\n</section>\n");
            return;
        }

        sb.Append("<ol class=\"queue\">\n");
        foreach (var i in q.Items) OwnerItem(sb, i);
        sb.Append("</ol>\n</section>\n");
    }

    private static void OwnerItem(StringBuilder sb, OwnerQueueItemDto i)
    {
        sb.Append("<li>\n");
        sb.Append(CultureInfo.InvariantCulture, $"<div class=\"qt\">{Esc(i.Title)}</div>\n");
        sb.Append(CultureInfo.InvariantCulture, $"<div class=\"qm\">{Esc(i.Kind)} · {Esc(Age(i.AgeSeconds))}");
        sb.Append(string.IsNullOrWhiteSpace(i.Unblocks) ? "" : " · unblocks " + Esc(i.Unblocks)).Append("</div>\n");
        if (!string.IsNullOrWhiteSpace(i.Detail))
            sb.Append(CultureInfo.InvariantCulture, $"<div class=\"qd\">{Esc(i.Detail)}</div>\n");
        sb.Append(string.IsNullOrWhiteSpace(i.Command)
            ? "<div class=\"qc none\">nothing typed clears this — it clears itself</div>\n"
            : string.Create(CultureInfo.InvariantCulture, $"<div class=\"qc\">clears with <code>{Esc(i.Command)}</code></div>\n"));
        sb.Append("</li>\n");
    }

    /// <summary>The columns. Every status gets its column even when empty — a board that hides its
    /// empty columns changes shape between renders, and two snapshots of it cannot be compared.</summary>
    private static void Board(StringBuilder sb, BoardSnapshot snap)
    {
        var byStatus = snap.Tasks.Tasks
            .Where(t => !string.Equals(t.Status, "archived", StringComparison.OrdinalIgnoreCase))
            .GroupBy(t => t.Status.ToLowerInvariant(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Order).ToList(), StringComparer.Ordinal);

        sb.Append("<section class=\"board\">\n<h2>Board</h2>\n<div class=\"cols\">\n");
        foreach (var status in ColumnStatuses)
        {
            var cards = byStatus.TryGetValue(status, out var list) ? list : [];
            sb.Append(CultureInfo.InvariantCulture,
                $"<div class=\"col\">\n<h3>{Esc(ColumnName(status))} <span class=\"n\">{cards.Count.ToString(CultureInfo.InvariantCulture)}</span></h3>\n");
            if (cards.Count == 0) sb.Append("<p class=\"none\">empty</p>\n");
            foreach (var t in cards) Card(sb, t, snap.RenderedUtc);
            sb.Append("</div>\n");
        }
        sb.Append("</div>\n");

        // A status the fold produced that has no column is REPORTED, never dropped: a card simply
        // absent from a board reads as work that does not exist.
        var homeless = byStatus.Keys.Where(k => !ColumnStatuses.Contains(k, StringComparer.Ordinal)).ToList();
        if (homeless.Count > 0)
            sb.Append(CultureInfo.InvariantCulture,
                $"<p class=\"warn\">{Count(homeless.Sum(k => byStatus[k].Count), "card")} in no column — status {Esc(string.Join(", ", homeless))}</p>\n");
        sb.Append("</section>\n");
    }

    private static void Card(StringBuilder sb, TaskDto t, DateTime nowUtc)
    {
        var id = string.IsNullOrWhiteSpace(t.CheckpointId) ? t.TaskId : t.CheckpointId;
        sb.Append("<article class=\"card\">\n");
        sb.Append(CultureInfo.InvariantCulture, $"<div class=\"id\">{Esc(id)}");
        if (t.Confirmed) sb.Append(" <span class=\"ok\">confirmed</span>");
        sb.Append("</div>\n");
        sb.Append(CultureInfo.InvariantCulture, $"<div class=\"t\">{Esc(t.Title)}</div>\n");

        var meta = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(t.StageId)) meta.Add(t.StageId);
        meta.Add(InColumn(t.StatusSinceUtc, nowUtc));
        if (t.SessionNumber > 0) meta.Add("session " + t.SessionNumber.ToString(CultureInfo.InvariantCulture));
        if (t.Attempts > 1) meta.Add(Count(t.Attempts, "attempt"));
        sb.Append(CultureInfo.InvariantCulture, $"<div class=\"m\">{Esc(string.Join(" · ", meta))}</div>\n");
        sb.Append("</article>\n");
    }

    /// <summary>What PROVES the done column. Paths, not links: this file is read on a phone, and a
    /// <c>file://</c> link to the engine's disk would be a link that never opens — so the path is
    /// printed as the thing it is, and the footer says which machine it is on.</summary>
    private static void Evidence(StringBuilder sb, BoardSnapshot snap)
    {
        sb.Append("<section class=\"evidence\">\n");
        sb.Append(CultureInfo.InvariantCulture, $"<h2>Evidence — {Count(snap.Evidence.Count, "artifact")}</h2>\n");
        if (snap.Evidence.Count == 0)
        {
            sb.Append("<p class=\"none\">No artifact has been registered by this run.</p>\n</section>\n");
            return;
        }

        sb.Append("<ul class=\"ev\">\n");
        foreach (var a in snap.Evidence)
        {
            var owner = string.IsNullOrWhiteSpace(a.CheckpointId) ? a.StageId ?? "" : a.CheckpointId!;
            sb.Append(CultureInfo.InvariantCulture,
                $"<li><code>{Esc(a.Path)}</code><span class=\"m\">{Esc(owner)} · {Esc(a.Kind)} · {Bytes(a.Bytes)} · {Esc(When(a.CreatedAt))}</span></li>\n");
        }
        sb.Append("</ul>\n</section>\n");
    }

    private static void Footer(StringBuilder sb, BoardSnapshot snap)
    {
        var s = snap.State;
        sb.Append("<footer>\n");
        sb.Append(CultureInfo.InvariantCulture,
            $"<p>{Esc(s.Repo)} · run {Esc(s.RunId)}{(string.IsNullOrWhiteSpace(s.EngineVersion) ? "" : " · conductor " + Esc(s.EngineVersion))}</p>\n");
        sb.Append("<p>Published, not served: this file was pushed OUT of the machine that rendered it. "
                + "There is no port, no tunnel and nothing to connect to, and the paths above are on that "
                + "machine (ADR-0005).</p>\n");
        sb.Append("</footer>\n");
    }

    // ───────────────────────────────── the small answers ─────────────────────────────────

    private static void Fact(StringBuilder sb, string label, string value) =>
        sb.Append(CultureInfo.InvariantCulture, $"<li><span>{Esc(label)}</span>{Esc(value)}</li>\n");

    private static string Cost(StateDto s)
    {
        var spent = s.CostSpent.ToString("0.00", CultureInfo.InvariantCulture);
        // "no cap" and "loads left" are different facts and must not render the same (StateDto's own
        // rule for the null cap); the page says which of the two it is.
        return s.CostCap is { } cap
            ? FormattableString.Invariant($"${spent} of ${cap.ToString("0.00", CultureInfo.InvariantCulture)}")
            : FormattableString.Invariant($"${spent} · no cap set");
    }

    /// <summary>SF3.2's age-in-column. An unstamped card says so: a card that entered its column
    /// before the fold started carrying the stamp is not a card that entered it just now.</summary>
    private static string InColumn(string? sinceUtc, DateTime nowUtc) =>
        // AssumeUniversal, not RoundtripKind: the stamp is written with "O" and carries its own Z, but
        // an older event folded without one would otherwise be read as local time — and every age on
        // the page would then be wrong by the operator's offset, invisibly.
        DateTime.TryParse(sinceUtc, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var since)
            ? Span(nowUtc - since) + " in column"
            : "age unknown";

    /// <summary>A round-trip stamp, read by a person. Unparseable text is passed through as itself
    /// rather than blanked: a timestamp nobody can read is still evidence of what was recorded.</summary>
    private static string When(string? iso) =>
        DateTime.TryParse(iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? Stamp(t) : iso ?? "";

    private static string Age(long? seconds) =>
        seconds is { } s ? Span(TimeSpan.FromSeconds(s)) + " old" : "age unknown";

    private static string Span(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalMinutes < 1) return "under a minute";
        if (d.TotalHours < 1) return Count((int)d.TotalMinutes, "minute");
        if (d.TotalDays < 1) return Count((int)d.TotalHours, "hour");
        return Count((int)d.TotalDays, "day");
    }

    private static string Bytes(long bytes) => bytes < 1024
        ? Count((int)bytes, "byte")
        : (bytes / 1024.0).ToString("0.# KB", CultureInfo.InvariantCulture);

    private static string Count(int n, string noun) =>
        FormattableString.Invariant($"{n} {noun}{(n == 1 ? "" : "s")}");

    private static string Stamp(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>Full HTML escaping, not Telegram's. <c>MessageComposer.EscapeHtml</c> escapes the
    /// three characters Telegram's restricted parse mode needs; a document also has attributes, so a
    /// quote in a title would end one.</summary>
    private static string Esc(string? s) => string.IsNullOrEmpty(s)
        ? ""
        : s.Replace("&", "&amp;", StringComparison.Ordinal)
           .Replace("<", "&lt;", StringComparison.Ordinal)
           .Replace(">", "&gt;", StringComparison.Ordinal)
           .Replace("\"", "&quot;", StringComparison.Ordinal);

    /// <summary>A raw string literal inherits the line endings of ITS SOURCE FILE, so the CSS block
    /// below arrives as CRLF on a CRLF checkout while every other line of this renderer appends an
    /// explicit LF -- and the page becomes a mixed document whose bytes depend on how the repository
    /// was cloned. Normalise at the literal's own site, not over the finished document: a blanket
    /// pass at the seam would make <c>CH1_1BoardPageLineEndingsTests</c> unable to fail, and that
    /// test is the guard on the NEXT raw string to arrive in this file.</summary>
    private static string Lf(string s) => s.Contains('\r', StringComparison.Ordinal)
        ? s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal)
        : s;

    /// <summary>Inline, and every value literal. No font is fetched (the system stack renders on the
    /// device the file reached), and the dark half is a media query rather than a script — a page
    /// that needs JavaScript to be legible is not one document.</summary>
    private static readonly string Css = Lf("""
:root{--bg:#fbfbfa;--fg:#1b1b1a;--dim:#66655f;--line:#dedcd5;--card:#fff;--ok:#1a7f37;--warn:#9a3412}
@media(prefers-color-scheme:dark){:root{--bg:#16171a;--fg:#e8e6e1;--dim:#9a978f;--line:#2c2e33;--card:#1e2024;--ok:#3fb950;--warn:#f0883e}}
*{box-sizing:border-box}
body{margin:0;padding:16px;background:var(--bg);color:var(--fg);font:15px/1.45 -apple-system,BlinkMacSystemFont,Segoe UI,Roboto,Helvetica,Arial,sans-serif}
h1{font-size:20px;margin:0 0 4px}
h2{font-size:15px;text-transform:uppercase;letter-spacing:.06em;color:var(--dim);margin:24px 0 8px}
h3{font-size:13px;margin:0 0 8px;display:flex;justify-content:space-between;align-items:center}
code{font:12px/1.4 ui-monospace,SFMono-Regular,Consolas,monospace;word-break:break-all}
.stale{margin:0 0 12px;padding:8px 10px;border-left:3px solid var(--warn);background:var(--card);color:var(--dim);font-size:13px}
.facts{list-style:none;margin:0;padding:0;display:flex;flex-wrap:wrap;gap:6px}
.facts li{background:var(--card);border:1px solid var(--line);border-radius:6px;padding:4px 8px;font-size:13px}
.facts span{color:var(--dim);margin-right:6px}
.none{color:var(--dim);font-size:13px;margin:4px 0}
.warn{color:var(--warn);font-size:13px}
.queue{margin:0;padding-left:20px}
.queue li{margin-bottom:10px}
.qt{font-weight:600}
.qm,.m{color:var(--dim);font-size:12px}
.qd{font-size:13px}
.qc{font-size:12px;margin-top:2px}
.cols{display:flex;flex-wrap:wrap;gap:10px;align-items:flex-start}
.col{flex:1 1 200px;min-width:180px;border:1px solid var(--line);border-radius:8px;padding:10px}
.col .n{color:var(--dim);font-weight:400}
.card{background:var(--card);border:1px solid var(--line);border-radius:6px;padding:8px;margin-bottom:8px}
.card .id{font:12px ui-monospace,SFMono-Regular,Consolas,monospace;color:var(--dim)}
.card .t{font-size:13px;margin:2px 0}
.ok{color:var(--ok)}
.ev{list-style:none;margin:0;padding:0}
.ev li{border-bottom:1px solid var(--line);padding:6px 0}
.ev .m{display:block}
footer{margin-top:28px;border-top:1px solid var(--line);padding-top:10px;color:var(--dim);font-size:12px}
""");
}
