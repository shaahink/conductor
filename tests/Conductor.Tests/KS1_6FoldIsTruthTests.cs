using System.Text;
using System.Text.RegularExpressions;

namespace Conductor.Tests;

/// <summary>
/// KS1.6 — the invariant KS1 installed, stated once as a bar the next session meets rather than as a
/// paragraph the next session reads: <b>the event fold is truth; a mutable snapshot column is a view;
/// every reader outside the engine folds or reconciles.</b>
///
/// <para>KS1.2 and KS1.3 each retired one reader that had trusted a column because it sat in a table.
/// <c>stages.session_count</c> has had no writer since v1, so it answered 0 for every run that ever
/// held a session, and the archive printed it. <c>runs.status</c> says what the last engine to write
/// the row believed, so a killed engine's run said <c>running</c> for ever, and the picker offered it.
/// Both were fixed. Neither fix stops the NEXT reader — a column in a table is the most trustworthy
/// looking thing in a database, and the pull toward one SELECT is strongest at the moment somebody is
/// adding a surface in a hurry.</para>
///
/// <para>So the forbidden set is declared here, explicitly, and every entry names the fold that
/// replaces it. The failure message is the point: it says which file, which column, and what to call
/// instead — the KS0.2 / KS1.2 scan idiom, generalised from one table to the rule behind all of
/// them.</para>
/// </summary>
public class KS1_6FoldIsTruthTests
{
    // --------------------------------------------------------------- the forbidden set

    /// <summary>One mutable snapshot column (or dropped table), how a reader spells a read of it, and
    /// the fold-derived thing to call instead.</summary>
    private sealed record MutableRead(string Column, string Replacement, Func<string, bool> ReadIn);

    /// <summary>A read of the runs table, and the word <c>status</c> in the same file's SQL. Two
    /// halves rather than one pattern because this tree builds SQL across concatenated literals: in
    /// <c>RunArchive.Runs()</c> the projection and its <c>FROM runs r</c> are eight lines apart, and a
    /// rule that only sees them adjacent would miss the one real reader in the repo.</summary>
    private static bool ReadsRunStatus(string sql) =>
        Rx(@"\b(?:from|join)\s+runs\b").IsMatch(sql) && Rx(@"\bstatus\b").IsMatch(sql);

    /// <summary>The stages side table. Bare <c>session_count</c> is deliberately NOT a pattern: the
    /// archive derives a run's session count as <c>(SELECT COUNT(*) FROM sessions) AS session_count</c>,
    /// which is a fold-shaped read wearing the dead column's name, and flagging it would teach the next
    /// session to rename the honest one.</summary>
    private static bool ReadsStages(string sql) =>
        Rx(@"\b(?:from|join)\s+stages\b|\bstages\s*\.\s*(?:status|session_count)\b").IsMatch(sql);

    /// <summary>The checkpoints table, dropped at schema v8 (ADR-0002). Qualified column references
    /// are listed by name rather than matched as <c>checkpoints.&lt;anything&gt;</c>, because prose in a
    /// string literal ends sentences too: "…runs the checkpoints. Then…" is not a SELECT.</summary>
    private static bool ReadsCheckpointsTable(string sql) =>
        Rx(@"\b(?:from|join)\s+checkpoints\b|\bcheckpoints\s*\.\s*(?:id|run_id|stage_id|status|title|commit_sha|evidence|confirmed)\b")
            .IsMatch(sql);

    private static readonly MutableRead[] Forbidden =
    [
        new("runs.status",
            "RunLiveness.Reconcile(...) - or RunHistoryRow.Status, which does it for you and keeps the " +
            "stored word beside it as StoredStatus (KS1.3). The column is a claim the last engine to " +
            "write the row made; a killed engine never gets to correct it.",
            ReadsRunStatus),

        new("stages.status / stages.session_count",
            "RunArchive.Stages(runId) - the KS1.2 fold over StageEntered / StageConfirmed / " +
            "SessionStarted. session_count has had no writer since v1 and answers 0 for every run.",
            ReadsStages),

        new("the checkpoints table (dropped at schema v8)",
            "TaskGraph.Fold(events) - RunArchive.Checkpoints(runId) is the worked example.",
            ReadsCheckpointsTable),
    ];

    // --------------------------------------------------------------- outside the engine

    /// <summary>"Outside the engine", mechanically: the directories where a READER lives — the CLI's
    /// commands, the control plane, the archive, and the fleet/picker surface. Everything under them
    /// renders to somebody. The run loop and the store are not here, and that is the whole
    /// distinction: the engine may read a column it just wrote, a reader may not.</summary>
    private static readonly string[] ReaderDirectories =
    [
        "src/Conductor/Commands",
        "src/Conductor/Http",
        "src/Conductor.Core/History",
        "src/Conductor.Core/Fleet",
    ];

    /// <summary>
    /// The explicit, small list of reads this rule does not govern — file pattern, then the column it
    /// is allowed to speak (<c>*</c> for all of them). Deliberately a PAIR rather than a set of file
    /// names: a whole-file waiver would quietly cover every line somebody adds to that file later,
    /// which is the failure mode KS0.2's line-level waiver was written to avoid.
    /// </summary>
    private static readonly (string File, string Column)[] Sanctioned =
    [
        // The store is the engine. It WROTE these columns, and QueryRun is how the run loop reads its
        // own row back; a prefix because the store is one class in eight partials and naming all eight
        // is a list nobody keeps in step.
        ("SqliteRunStore*", "*"),

        // The schema. Declaring a column, or dropping the table it lived in, is not consuming a view.
        ("src/Conductor.Core/Store/Migrations/*", "*"),

        // The archive's read-only door, and the only sanctioned read of runs.status in the repo. It
        // hands the STORED word out as ArchivedRun.Status so RunLiveness can reconcile it and
        // RunHistoryRow can print both (KS1.3: "the row says running" and "a run is running" are
        // different facts). It is scoped to that one column on purpose - a stages or checkpoints read
        // appearing in this same file is still an offender.
        ("RunArchive.cs", "runs.status"),
    ];

    // --------------------------------------------------------------- the bar

    [Fact]
    public void ReadersOutsideTheEngineDoNotConsumeMutableSnapshotColumns()
    {
        var offenders = Scan(OutsideTheEngine(RepoRoot())).ToList();

        Assert.True(offenders.Count == 0,
            "KS1.6 - the fold is truth, a snapshot column is a view. These readers consume one:\n" +
            string.Join("\n", offenders) +
            "\nA column in a table looks like a fact and is a cached opinion. Fold the event log, or " +
            "reconcile what the row claims, and the surface stops repeating a lie nobody has checked.");
    }

    /// <summary>Both spellings a real offender would use, for every entry in the forbidden set: the
    /// raw SQL literal, and the same read routed through <c>RunArchive.Query</c> / <c>IRunStore.Query</c>
    /// with the statement split across concatenated literals. A bar nobody has watched go red is a bar
    /// nobody knows the shape of.</summary>
    [Theory]
    [InlineData("runs.status", "var sql = \"SELECT run_id, status FROM runs ORDER BY started_utc DESC\";")]
    [InlineData("runs.status", "var rows = archive.Query(\n    \"SELECT r.run_id, r.status \" +\n    \"FROM runs r\");")]
    [InlineData("stages.status", "var sql = \"SELECT id, title, status FROM stages WHERE run_id = @id\";")]
    [InlineData("stages.session_count", "var rows = store.Query(\n    \"SELECT st.session_count \" +\n    \"FROM stages st WHERE st.run_id = @runId\");")]
    // The archive's own retired reader, verbatim as it stood on feat/karvansara before KS1.2. This is
    // the ordering claim made mechanical: the bar could not have gone green before that checkpoint,
    // and it is the exact shape it was written to keep out.
    [InlineData("stages.status", "var rows = Query(\n    \"SELECT id, title, status, session_count, started_utc, confirmed_utc \" +\n    \"FROM stages WHERE run_id = @runId ORDER BY COALESCE(started_utc, ''), id\");")]
    [InlineData("checkpoints", "var sql = \"SELECT id, status FROM checkpoints WHERE run_id = @id\";")]
    [InlineData("checkpoints", "var rows = archive.Query(\n    \"SELECT c.id, c.status \" +\n    \"FROM checkpoints c\");")]
    public void TheScanGoesRedOnASeededViolation(string expected, string source)
    {
        InTempDir(tmp =>
        {
            var seeded = Path.Combine(tmp, "HelpfulReader.cs");
            File.WriteAllText(seeded, source);

            var hit = Assert.Single(Scan([seeded]));
            Assert.Contains("HelpfulReader.cs", hit, StringComparison.Ordinal);
            Assert.Contains(expected, hit, StringComparison.Ordinal);
            // and it says what to do instead, which is the only reason a scan is worth having
            Assert.Contains("->", hit, StringComparison.Ordinal);
        });
    }

    /// <summary>The bar governs SNAPSHOT columns, not reading. Folding the log, counting sessions from
    /// the sessions table, and calling the fold-derived surfaces are all how a reader is SUPPOSED to
    /// answer the same questions — a rule that cannot tell them apart makes the right answer expensive
    /// and the wrong one easy.</summary>
    [Fact]
    public void AReadOnlyOfAFoldDerivedViewIsNotAnOffender()
    {
        InTempDir(tmp =>
        {
            var good = Path.Combine(tmp, "HonestReader.cs");
            File.WriteAllText(good,
                // the fold's own read - the log, in order
                "var e = archive.Query(\"SELECT payload FROM events WHERE run_id = @runId ORDER BY seq\");\n" +
                // a count derived from the sessions table, wearing the dead column's name as an alias
                "var n = archive.Query(\"SELECT (SELECT COUNT(*) FROM sessions s WHERE s.run_id = r.run_id) AS session_count FROM sessions r\");\n" +
                // and the fold-derived surfaces themselves: C# member access is not SQL
                "var stages = archive.Stages(runId);\n" +
                "var done = graph.Checkpoints().Count(c => c.Status == \"done\");\n" +
                "var word = row.Status;\n" +
                "var declared = plan.Stages.Select(s => s.Id).ToList();\n");
            Assert.Empty(Scan([good]));

            // A doc-comment EXPLAINING the rule cannot break the rule - including one that quotes the
            // offending SQL, which is exactly how this file's own summary is written.
            var explained = Path.Combine(tmp, "Explained.cs");
            File.WriteAllText(explained,
                "// never do this: var sql = \"SELECT status FROM runs\";\n" +
                "/// <summary>runs.status is a claim; see \"SELECT session_count FROM stages\".</summary>\n" +
                "/* var dead = archive.Query(\"SELECT * FROM checkpoints\"); */\n" +
                "var x = 1;\n");
            Assert.Empty(Scan([explained]));
        });
    }

    /// <summary>The sanctioned list, exercised against the real files rather than asserted about. The
    /// store's own partials and the migrations are handed straight to the scan - past the directory
    /// filter that would have excluded them anyway - so the waiver is a live claim: if somebody widens
    /// the reader directories tomorrow, the store does not turn red and the migrations do not either.</summary>
    [Fact]
    public void TheStoreAndItsMigrationsAreNotGovernedByThisRule()
    {
        var root = RepoRoot();
        var store = Directory.EnumerateFiles(Path.Combine(root, "src", "Conductor.Core", "Store"), "SqliteRunStore*.cs")
            .ToList();
        var migrations = Directory.EnumerateFiles(Path.Combine(root, "src", "Conductor.Core", "Store", "Migrations"), "*.sql")
            .ToList();

        // The fixture is only meaningful if those files really do speak the columns.
        Assert.Contains(store, f => ReadsRunStatus(SqlOf(f)));
        Assert.NotEmpty(migrations);

        Assert.Empty(Scan(store));
        Assert.Empty(Scan(migrations));
    }

    // --------------------------------------------------------------- the scan

    /// <summary>Every offender, one line each: the file, the column, and the fold that replaces it.
    /// Per FILE rather than per line, because the reads this rule hunts are assembled from literals
    /// that can sit anywhere in a method - a line number would be a guess dressed as a fact.</summary>
    private static IEnumerable<string> Scan(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            var sql = SqlOf(file);
            if (sql.Length == 0) continue;
            foreach (var rule in Forbidden)
            {
                if (!rule.ReadIn(sql)) continue;
                if (IsSanctioned(file, rule.Column)) continue;
                yield return $"  {Path.GetFileName(file)}: {rule.Column} -> {rule.Replacement}";
            }
        }
    }

    private static bool IsSanctioned(string file, string column) =>
        Sanctioned.Any(s => (s.Column == "*" || string.Equals(s.Column, column, StringComparison.Ordinal))
                            && PathMatches(file, s.File));

    /// <summary>A pattern with a path separator in it matches the file's PATH; one without matches its
    /// name. A single trailing <c>*</c> is the only wildcard, which is all this list has ever needed
    /// and all it should ever need - a glob language here is a way to write a waiver nobody can read.</summary>
    private static bool PathMatches(string file, string pattern)
    {
        var path = file.Replace('\\', '/');
        var subject = pattern.Contains('/', StringComparison.Ordinal) ? path : Path.GetFileName(path);
        return pattern.EndsWith('*')
            ? subject.Contains(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(subject, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> OutsideTheEngine(string root)
    {
        foreach (var dir in ReaderDirectories)
        {
            var path = Path.Combine(root, dir.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(path)) continue;
            foreach (var f in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
                if (f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
                yield return f;
            }
        }
    }

    /// <summary>
    /// The SQL a file actually contains: for a <c>.sql</c> file its body, and for a <c>.cs</c> file the
    /// text of every string literal, joined.
    /// <para>Walked once, character by character, rather than regexed twice. Comments are not code (the
    /// <c>ArchitectureBoundaryTests.CodeOnly</c> rule) and a strip-then-extract pass gets both halves of
    /// that wrong: a <c>//</c> inside a URL literal would truncate the literal, and a quote inside a
    /// comment would open one. One pass knows which it is in, so a doc-comment quoting the offending
    /// SELECT stays prose and a literal containing <c>https://</c> stays whole.</para>
    /// </summary>
    private static string SqlOf(string file)
    {
        var text = File.ReadAllText(file);
        if (Path.GetExtension(file).Equals(".sql", StringComparison.OrdinalIgnoreCase))
            return Rx(@"--[^\n]*").Replace(text, " ").ToLowerInvariant();

        var sql = new StringBuilder();
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++;
                i = Math.Min(text.Length, i + 2);
            }
            else if (c == '\'')
            {
                // A char literal: '"' is not the start of a string, and this is the only reason to know it.
                i++;
                while (i < text.Length && text[i] != '\'')
                    i += text[i] == '\\' ? 2 : 1;
                i++;
            }
            else if (c == '"' && i + 2 < text.Length && text[i + 1] == '"' && text[i + 2] == '"')
            {
                var open = 0;
                while (i < text.Length && text[i] == '"') { open++; i++; }
                var start = i;
                while (i < text.Length && !IsQuoteRun(text, i, open)) i++;
                sql.Append(text, start, i - start).Append('\n');
                i = Math.Min(text.Length, i + open);
            }
            else if (c == '"' && i > 0 && text[i - 1] == '@')
            {
                i++;
                var start = i;
                while (i < text.Length && !(text[i] == '"' && (i + 1 >= text.Length || text[i + 1] != '"')))
                    i += text[i] == '"' ? 2 : 1;
                sql.Append(text, start, Math.Min(i, text.Length) - start).Append('\n');
                i++;
            }
            else if (c == '"')
            {
                i++;
                var start = i;
                while (i < text.Length && text[i] != '"' && text[i] != '\n')
                    i += text[i] == '\\' ? 2 : 1;
                sql.Append(text, start, Math.Min(i, text.Length) - start).Append('\n');
                i++;
            }
            else i++;
        }
        return sql.ToString().ToLowerInvariant();
    }

    private static bool IsQuoteRun(string text, int i, int count)
    {
        if (text[i] != '"') return false;
        for (var k = 0; k < count; k++)
            if (i + k >= text.Length || text[i + k] != '"') return false;
        return true;
    }

    private static Regex Rx(string pattern) => new(
        pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(5));

    private static void InTempDir(Action<string> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var tmp = Path.Combine(Path.GetTempPath(), "conductor-ks16arch-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(tmp);
        try { body(tmp); }
        finally { try { Directory.Delete(tmp, recursive: true); } catch (IOException) { } }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("could not locate repo root (Conductor.slnx)");
    }
}
