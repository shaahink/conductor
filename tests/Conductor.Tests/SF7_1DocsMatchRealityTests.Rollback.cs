using System.Text.RegularExpressions;

namespace Conductor.Tests;

/// <summary>
/// SF7.1 part 4 — the rollback paragraph. <c>ARCHITECTURE.md</c> said "<c>rollback</c> squashes
/// bookkeeping commits; it does not revert code" while <c>ControlDispatcher</c> ran
/// <c>git reset --hard</c> onto the stage-start head. An operator who believed the doc would reach for
/// the one verb in the danger group that throws work away, expecting it to tidy the log. The verified
/// audit confirmed the claim verbatim (<c>docs/dev/NEXT-ERA-VERIFIED-PLAN-2026-08-07.md</c>, row 4);
/// KS1.5 rewrote the paragraph and this file is its pin.
/// <para>The pin DERIVES, in the SF7.1 house style: every claim the paragraph must carry is read out
/// of the rollback arm in <c>Core/Commands/ControlDispatcher.cs</c> first, so the day the verb stops
/// resetting — or starts stashing, or loses a refusal — the doc goes red rather than quietly becoming
/// wrong again. Nothing here compares the paragraph to a copy of itself.</para>
/// </summary>
public sealed partial class SF7_1DocsMatchRealityTests
{
    private static string Architecture() => Doc("ARCHITECTURE.md");

    private static string Dispatcher()
        => Doc("src", "Conductor.Core", "Commands", "ControlDispatcher.cs");

    /// <summary>The historical sentence, kept only as the negative case's seed.</summary>
    private const string StaleParagraph =
        "`rollback` squashes bookkeeping commits; it does **not** revert code.";

    /// <summary>A doc sentence that says rollback leaves the code alone. Applied only to windows that
    /// mention rollback, so ordinary prose elsewhere ("this does not change the plan file") is not
    /// swept up.</summary>
    [GeneratedRegex(@"(does|do|will)\s+\*{0,2}not\*{0,2}\s+(revert|undo|touch|rewrite|discard)|squash\w*\s+bookkeeping",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 5000)]
    private static partial Regex StaleRollbackClaim();

    /// <summary>Each refusal branch, counted from the arm's own log calls rather than from its toasts —
    /// a branch logs once and toasts once, and only the branch count is a fact about behaviour.</summary>
    [GeneratedRegex("""log\(\$?"rollback refused""", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex RefusalBranch();

    [GeneratedRegex(@"new RollbackExecuted\s*\{(?<init>[^}]*)\}", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex RollbackEmit();

    [GeneratedRegex(@"(?<name>\w+)\s*=", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Assigned();

    /// <summary>The <c>case ControlAction.Rollback</c> arm, up to the next arm — the only place the
    /// verb's behaviour lives.</summary>
    private static string RollbackArm(string dispatcher)
    {
        var start = dispatcher.IndexOf("case ControlAction.Rollback", StringComparison.Ordinal);
        Assert.True(start >= 0,
            "ControlDispatcher has no rollback arm any more — ARCHITECTURE.md describes a verb that is " +
            "gone, and this pin has to be rewritten with it.");
        var next = dispatcher.IndexOf("case ControlAction.", start + "case ControlAction.".Length,
            StringComparison.Ordinal);
        return next > start ? dispatcher[start..next] : dispatcher[start..];
    }

    /// <summary>The paragraph under test: the one blank-line-delimited block in ARCHITECTURE.md that
    /// opens with the verb. The Face's verb-table line (danger group: kill/abort/rollback) mentions
    /// rollback too and is deliberately not this.</summary>
    private static string RollbackParagraph(string doc)
    {
        var hits = doc.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n")
            .Where(p => p.TrimStart().StartsWith("`rollback`", StringComparison.Ordinal))
            .ToList();
        Assert.True(hits.Count == 1,
            $"ARCHITECTURE.md should carry exactly one paragraph opening with `rollback`; found {hits.Count}");
        return hits[0];
    }

    /// <summary>The whole derivation, as a pure function so the negative case can run the same bar over
    /// a paragraph that is known to be stale. Every entry reads a fact out of the dispatcher first and
    /// only then asks the prose for it.</summary>
    private static IReadOnlyList<string> RollbackDocComplaints(string paragraph, string dispatcher)
    {
        var arm = RollbackArm(dispatcher);
        var prose = paragraph.Replace("\n", " ", StringComparison.Ordinal);
        var complaints = new List<string>();

        void MustSay(string token, string because)
        {
            if (!prose.Contains(token, StringComparison.OrdinalIgnoreCase))
                complaints.Add($"the paragraph never says \"{token}\" — {because}");
        }

        // 1. What it does. Git.Exec(repo, "reset", "--hard", sha) at ControlDispatcher.cs:189.
        var resets = arm.Contains("Git.Exec", StringComparison.Ordinal)
                     && arm.Contains("\"reset\"", StringComparison.Ordinal)
                     && arm.Contains("\"--hard\"", StringComparison.Ordinal);
        if (!resets)
            complaints.Add("the rollback arm no longer runs git reset --hard, so the paragraph now " +
                           "over-claims — rewrite it from ControlDispatcher, do not soften it");
        else
        {
            MustSay("reset --hard", "the arm runs Git.Exec(repo, \"reset\", \"--hard\", sha)");
            if (StaleRollbackClaim().IsMatch(prose))
                complaints.Add("the paragraph still says rollback leaves code alone while the arm runs " +
                               "git reset --hard — that is the exact drift KS1.5 closed");
        }

        // 2. Where it resets TO.
        if (arm.Contains("CurrentStageStartHead", StringComparison.Ordinal))
            MustSay("CurrentStageStartHead", "the arm resets to state.CurrentStageStartHead, not to HEAD~1");

        // 3. Both refusals, counted from the source.
        var refusals = RefusalBranch().Matches(arm).Count;
        var stated = Regex.Matches(prose, "refus", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5)).Count;
        if (stated < refusals)
            complaints.Add($"the arm refuses on {refusals} distinct condition(s) and the paragraph " +
                           $"states {stated} — an unstated refusal reads to an operator as a verb that hung");

        // 4. The dirty-tree bargain: --force does not stash, it discards.
        if (arm.Contains("Git.IsDirty", StringComparison.Ordinal))
            MustSay("dirty", "the arm refuses on Git.IsDirty(repo)");
        if (arm.Contains("cmd.Force", StringComparison.Ordinal))
            MustSay("--force", "the arm reads cmd.Force as the override for that refusal");
        if (arm.Contains("discard", StringComparison.OrdinalIgnoreCase))
            MustSay("discard", "the arm's own log says --force discards the dirty working tree");

        // 5. Only outside a session; mid-session it is deferred, not dropped.
        var outsideSessionOnly = arm.Contains("when !inSession", StringComparison.Ordinal);
        var deferredMidSession = Regex.IsMatch(dispatcher, @"inSession\s*&&[^\n]*ControlAction\.Rollback",
            RegexOptions.None, TimeSpan.FromSeconds(5));
        if (outsideSessionOnly || deferredMidSession)
            MustSay("session", "the arm is guarded by `when !inSession` and a mid-session rollback is " +
                               "logged as taking effect after the session ends");

        // 6. The event it leaves behind, field for field.
        var emit = RollbackEmit().Match(arm);
        if (emit.Success)
        {
            MustSay("RollbackExecuted", "the arm emits it, and the event log is where a rollback is provable");
            foreach (Match f in Assigned().Matches(emit.Groups["init"].Value))
                MustSay(f.Groups["name"].Value, "the emitted RollbackExecuted sets it");
        }

        return complaints;
    }

    /// <summary>The pin itself.</summary>
    [Fact]
    public void ArchitectureDocDescribesTheRealRollback()
    {
        var complaints = RollbackDocComplaints(RollbackParagraph(Architecture()), Dispatcher());

        Assert.True(complaints.Count == 0,
            $"ARCHITECTURE.md's rollback paragraph has drifted from Core/Commands/ControlDispatcher.cs " +
            $"in {complaints.Count} place(s): {string.Join("; ", complaints)}");
    }

    /// <summary>A bar nobody has watched go red is a bar nobody knows the shape of. The seed is the
    /// sentence this checkpoint deleted, run through the same derivation.</summary>
    [Fact]
    public void GoesRedOnASeededStaleParagraph()
    {
        var complaints = RollbackDocComplaints(StaleParagraph, Dispatcher());

        Assert.NotEmpty(complaints);
        Assert.Contains(complaints, c => c.Contains("reset --hard", StringComparison.Ordinal));
        Assert.Contains(complaints, c => c.Contains("leaves code alone", StringComparison.Ordinal));
        Assert.Contains(complaints, c => c.Contains("refus", StringComparison.Ordinal));
    }

    /// <summary>The claim is gone from every doc this repo ships, not just from the one paragraph.
    /// <c>docs/dev/</c> and <c>docs/history/</c> are dated records of what was found when — the audit
    /// row that quotes the false sentence is the evidence that it existed, and deleting it would
    /// falsify the trail — so the sweep covers what a reader is handed, not what an era wrote down.</summary>
    [Fact]
    public void NoShippedDocClaimsRollbackLeavesCodeUntouched()
    {
        var offenders = new List<string>();
        foreach (var file in ShippedDocs())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                // Two lines wide: these docs wrap at ~98 columns and a claim can straddle the break.
                var window = i + 1 < lines.Length ? lines[i] + " " + lines[i + 1] : lines[i];
                if (!window.Contains("rollback", StringComparison.OrdinalIgnoreCase)) continue;
                if (!StaleRollbackClaim().IsMatch(window)) continue;
                offenders.Add($"{Path.GetFileName(file)}:{i + 1}");
            }
        }

        Assert.True(offenders.Count == 0,
            "these shipped docs still tell an operator that rollback spares the working tree, while " +
            "ControlDispatcher runs git reset --hard: " + string.Join(", ", offenders));
    }

    /// <summary>The converse: the three docs that DOCUMENT the verb have to name the reset. A verb
    /// table that lists rollback without the word is how the ARCHITECTURE sentence survived four
    /// eras — the danger was never contradicted anywhere the operator was looking.</summary>
    [Fact]
    public void ShippedDocsThatDocumentTheRollbackVerbSayItResets()
    {
        var arm = RollbackArm(Dispatcher());
        if (!arm.Contains("\"reset\"", StringComparison.Ordinal)) return;   // derived, not assumed

        foreach (var name in new[] { "cli.md", "operating.md", "quickstart.md" })
        {
            var says = File.ReadAllLines(Path.Combine(RepoRoot(), "docs", name))
                .Any(l => l.Contains("rollback", StringComparison.OrdinalIgnoreCase)
                          && l.Contains("reset", StringComparison.OrdinalIgnoreCase));
            Assert.True(says,
                $"docs/{name} names the rollback verb but no line of it says the verb resets the " +
                "working tree — ControlDispatcher's arm does exactly that");
        }
    }

    /// <summary>Everything a reader is handed: the repo-root pages and the top level of <c>docs/</c>.</summary>
    private static IEnumerable<string> ShippedDocs()
    {
        var root = RepoRoot();
        foreach (var f in Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly))
            yield return f;
        var docs = Path.Combine(root, "docs");
        if (!Directory.Exists(docs)) yield break;
        foreach (var f in Directory.EnumerateFiles(docs, "*.md", SearchOption.TopDirectoryOnly))
            yield return f;
    }
}
