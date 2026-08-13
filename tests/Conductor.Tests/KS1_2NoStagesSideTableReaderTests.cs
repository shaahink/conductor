using System.Text.RegularExpressions;

namespace Conductor.Tests;

/// <summary>
/// KS1.2's durable half. The <c>stages</c> side table is a VIEW the engine writes for older readers;
/// the truth is the event fold (<c>RunArchive.Stages</c>), the same way the dropped
/// <c>checkpoints</c> table's truth became <c>TaskGraph</c> at schema v8. This scan is what keeps
/// the next reader out: the table's <c>session_count</c> column has had no writer since v1 — it
/// reads 0 for every run that ever held a session — and a column that sits in a table gets trusted
/// precisely because it sits in a table. Nothing shipped may SELECT from it again.
/// </summary>
public class KS1_2NoStagesSideTableReaderTests
{
    /// <summary>The only file allowed to touch the table — its writer
    /// (<c>InitializeStage</c>/<c>ConfirmStage</c>). Not a directory, for KS0.2's reason: a
    /// directory-wide waiver is how a new helper reaches past the store.</summary>
    private static readonly string[] Sanctioned = ["SqliteRunStore.Sessions.cs"];

    [Fact]
    public void NothingReadsTheStagesSideTable()
    {
        var offenders = Scan(ShippedFiles(RepoRoot())).ToList();

        Assert.True(offenders.Count == 0,
            "these read the stages side table: " + string.Join("; ", offenders) +
            " - fold the event log instead (RunArchive.Stages / TaskGraph). The table is a view the" +
            " engine writes for older readers; its session_count column has no writer and always" +
            " answers 0. That is the whole of KS1.2.");
    }

    /// <summary>Both spellings a real offender would use: the raw SQL literal, and the same read
    /// routed through a <c>Query(...)</c> projection split across concatenated literals — the shape
    /// the archive's own retired reader had.</summary>
    [Theory]
    [InlineData("var sql = \"SELECT id, title, status, session_count FROM stages WHERE run_id = @id\";")]
    [InlineData("var rows = archive.Query(\n    \"SELECT st.session_count \" +\n    \"FROM stages st WHERE st.run_id = @runId\");")]
    public void TheScanGoesRedOnASeededViolation(string source)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "conductor-ks12arch-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(tmp);
        try
        {
            var seeded = Path.Combine(tmp, "HelpfulReader.cs");
            File.WriteAllText(seeded, source);
            Assert.Single(Scan([seeded]));

            // The writer's SQL is not a read — the table stays written (KS1.2 retires READS, not
            // the table) — and neither is C# that happens to say plan.Stages.
            var writer = Path.Combine(tmp, "Writer.cs");
            File.WriteAllText(writer,
                "var a = \"INSERT OR REPLACE INTO stages (id, run_id) VALUES (@id, @runId)\";\n" +
                "var b = \"UPDATE stages SET status = 'done' WHERE id = @id\";\n" +
                "var c = plan.Stages.Select(s => s.Id).ToList();");
            Assert.Empty(Scan([writer]));

            // And a doc-comment EXPLAINING the rule cannot break the rule: comments are stripped.
            var commented = Path.Combine(tmp, "Explained.cs");
            File.WriteAllText(commented, "// never write: SELECT session_count FROM stages\nvar x = 1;");
            Assert.Empty(Scan([commented]));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch (IOException) { }
        }
    }

    // ------------------------------------------------------------------------------- the scan

    /// <summary>A read of the table, in the shapes SQL actually takes in this tree: FROM/JOIN over
    /// the table, or a qualified reach for the dead column. Singleline because SQL here is built
    /// across concatenated string literals and the table name can land on the next line.</summary>
    private static readonly Regex ReadsStages = new(
        @"\bfrom\s+stages\b|\bjoin\s+stages\b|\bstages\s*\.\s*session_count\b",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(5));

    /// <summary>Comments are not reads (the ArchitectureBoundaryTests.CodeOnly rule): each comment is
    /// replaced by its own newlines so line numbers in the failure message stay true.</summary>
    private static readonly Regex Comments = new(
        @"/\*.*?\*/|//[^\n]*", RegexOptions.Singleline | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(5));

    private static string[] CodeLines(string file)
    {
        var text = File.ReadAllText(file);
        if (Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            text = Comments.Replace(text, m => new string('\n', m.Value.Count(c => c == '\n')));
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    }

    /// <summary>Line by line, plus each adjacent pair, so SQL split across two concatenated literals
    /// is still seen — the KS0_2 scan's idiom.</summary>
    private static IEnumerable<string> Scan(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            if (Sanctioned.Contains(Path.GetFileName(file), StringComparer.Ordinal)) continue;
            var lines = CodeLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var window = i + 1 < lines.Length ? lines[i] + "\n" + lines[i + 1] : lines[i];
                var m = ReadsStages.Match(window);
                if (!m.Success) continue;
                yield return $"{Path.GetFileName(file)}:{i + 1} ({m.Value.Trim()})";
                break;
            }
        }
    }

    /// <summary>Everything this repo ships and could run against a real store — the same surface
    /// KS0_2 scans. Tests are excluded on purpose (a fixture proving the side table still says 0
    /// must be allowed to read it); the migrations are excluded because declaring the table is not
    /// reading a row.</summary>
    private static IEnumerable<string> ShippedFiles(string root)
    {
        foreach (var dir in new[] { "src", "tools" })
        {
            var path = Path.Combine(root, dir);
            if (!Directory.Exists(path)) continue;
            foreach (var f in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
            {
                if (Path.GetExtension(f) is not (".cs" or ".ps1" or ".psm1" or ".sh" or ".mjs" or ".js")) continue;
                if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
                if (f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
                yield return f;
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("could not locate repo root (Conductor.slnx)");
    }
}
