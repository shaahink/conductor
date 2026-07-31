using System.Text;
using System.Text.RegularExpressions;

namespace Conductor.Models;

/// <summary>Per-plan progress conventions (B1.4, R1.3). Every default reproduces Loom's original
/// hard-coded behaviour byte-for-byte; a plan targeting a differently-shaped tracker overrides only
/// what differs. The type also assembles the tracker regexes so the markdown-table provider has a
/// single, configurable source of truth for the row/handoff shapes (F-1).</summary>
public sealed class ProgressConventions
{
    /// <summary>Shared, unmodified defaults (Loom's conventions), used by the static parse facade and
    /// by any <c>CheckpointRow</c> built without explicit conventions.</summary>
    public static ProgressConventions Default { get; } = new();

    /// <summary>ReDoS guard applied to every tracker regex (MA0009, ADR-0001 FU-B0-3). The tracker is
    /// untrusted input, so a pathological pattern can never hang the run.</summary>
    public static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Regex matching a checkpoint id; an optional <c>stage</c> named group yields the owning
    /// stage id. Default matches Loom/Baton ids (<c>L0.1</c>, <c>B1.4</c>) with stage = the part before
    /// the first dot. Shamshir sets <c>(?&lt;stage&gt;[A-Za-z]+-?\d+)(?:\.\d+)?[a-z]?</c> to admit the
    /// irregular <c>P-0</c>/<c>P3.4b</c>/<c>F5</c> ids.</summary>
    public string StageIdPattern { get; set; } = @"(?<stage>[A-Za-z]+\d+)(?:\.\d+)?[a-z]?";

    /// <summary>Heading opening the handoff block (default <c>## Handoff</c>).</summary>
    public string HandoffMarker { get; set; } = "## Handoff";

    /// <summary>Token an agent writes in the handoff to request a human decision (default <c>HUMAN:</c>).</summary>
    public string HumanToken { get; set; } = "HUMAN:";

    /// <summary>Status keywords grouped by meaning; a cell is classified by leading-keyword prefix
    /// (trailing decoration like <c>DONE ✅</c> is ignored).</summary>
    public StatusVocabulary Status { get; set; } = new();

    private Regex? _stageRx;

    /// <summary>Owning stage for a checkpoint id, honouring <see cref="StageIdPattern"/>'s <c>stage</c>
    /// group when present, else Loom's split-on-first-dot fallback.</summary>
    public string DeriveStageId(string id)
    {
        if (string.IsNullOrEmpty(id)) return id;
        _stageRx ??= new Regex("^(?:" + StageIdPattern + ")", RegexOptions.IgnoreCase, RegexTimeout);
        if (_stageRx.Match(id) is { Success: true } m && m.Groups["stage"] is { Success: true, Length: > 0 } g)
            return g.Value;
        return id.Split('.')[0];
    }

    public bool IsDone(string status) => StartsWithAny(status, Status.Done);
    public bool IsBlocked(string status) => StartsWithAny(status, Status.Blocked);
    public bool IsInProgress(string status) => StartsWithAny(status, Status.InProgress);

    /// <summary>SC5.3: deliberately not delivered. Distinct from BLOCKED (still owed) — a skipped
    /// checkpoint is settled, so the engine stops scheduling it and it does not hold a stage open.</summary>
    public bool IsSkipped(string status) => StartsWithAny(status, Status.Skipped);

    /// <summary>Does the handoff block ask for a human decision (<see cref="HumanToken"/>)?</summary>
    public bool MentionsHuman(string handoff)
        => !string.IsNullOrEmpty(HumanToken) && handoff.Contains(HumanToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>The per-line checkpoint-row regex for the markdown-table provider, assembled from the
    /// id pattern + status vocabulary. With the defaults this is equivalent to Conductor's original
    /// hard-coded row regex.</summary>
    internal Regex BuildRowRegex()
    {
        var statusAlt = string.Join("|", Status.All()
            .OrderByDescending(w => w.Length)
            .Select(ToWordRegex));
        var pattern =
            @"^\|\s*(?<id>" + StageIdPattern + @")\s*\|(?<title>[^|]*)\|\s*(?<status>" +
            statusAlt + @")(?<rest>[^|]*)\|(?<commit>[^|]*)\|(?<evidence>[^|]*)\|";
        return new Regex(pattern, RegexOptions.IgnoreCase, RegexTimeout);
    }

    /// <summary>Regex extracting the handoff block body (from <see cref="HandoffMarker"/> to the next
    /// level-2 heading or end of file).</summary>
    internal Regex BuildHandoffRegex()
    {
        var pattern = "^" + ToMarkerRegex(HandoffMarker) + @"[^\r\n]*\r?\n(?<body>.*?)(?=^##\s|\z)";
        return new Regex(pattern, RegexOptions.Multiline | RegexOptions.Singleline, RegexTimeout);
    }

    private static bool StartsWithAny(string status, List<string> words)
    {
        // The row regex captures the status keyword with its original inner whitespace (it matches
        // `IN\s+PROGRESS`), so a cell like "IN  PROGRESS" (double space / tab) reaches here verbatim.
        // Collapse whitespace runs on both sides before the prefix test so multi-word keywords still
        // classify — matching the old hard-coded `StartsWith("IN")` intent without its looseness.
        var normalized = CollapseWhitespace(status);
        return words.Exists(w => normalized.StartsWith(CollapseWhitespace(w), StringComparison.OrdinalIgnoreCase));
    }

    // Collapse every run of whitespace to a single space; returns the input unchanged when it holds no
    // consecutive/irregular whitespace, so the common single-space path allocates nothing new.
    private static string CollapseWhitespace(string s)
    {
        var needsWork = s.Contains("  ", StringComparison.Ordinal);
        for (var i = 0; !needsWork && i < s.Length; i++)
            needsWork = char.IsWhiteSpace(s[i]) && s[i] != ' ';   // tab/newline/etc → normalise to space
        if (!needsWork) return s;

        var sb = new StringBuilder(s.Length);
        var prevWs = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevWs) sb.Append(' ');
                prevWs = true;
            }
            else { sb.Append(ch); prevWs = false; }
        }
        return sb.ToString();
    }

    // Words may contain spaces ("IN PROGRESS"); match any run of whitespace between tokens so a
    // double-space or tab in the cell still classifies (matches the original `IN\s+PROGRESS`).
    private static string ToWordRegex(string word)
        => string.Join(@"\s+", word.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));

    private static string ToMarkerRegex(string marker)
        => string.Join(@"\s*", marker.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape));
}

/// <summary>Status keywords grouped by meaning (B1.4). Loom's defaults; a plan overrides any group to
/// speak its own vocabulary.</summary>
public sealed class StatusVocabulary
{
    public List<string> Done { get; set; } = ["DONE"];
    public List<string> Blocked { get; set; } = ["BLOCKED"];
    public List<string> InProgress { get; set; } = ["IN PROGRESS"];
    public List<string> Todo { get; set; } = ["TODO"];

    /// <summary>SC5.3: the work graph has folded <c>skipped</c> since W1.1 and <c>task --skipped</c>
    /// now writes it, so the row regex must know the word — a status the alternation does not list
    /// makes the whole row fail to match, and the checkpoint silently leaves the parsed snapshot.</summary>
    public List<string> Skipped { get; set; } = ["SKIPPED"];

    internal IEnumerable<string> All() => Done.Concat(Blocked).Concat(InProgress).Concat(Todo).Concat(Skipped);
}
