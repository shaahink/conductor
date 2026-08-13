using System.Text.RegularExpressions;

namespace Conductor.Tests;

/// <summary>
/// KS0.2's durable half. Shipping <c>conductor run close</c> retires the hand-SQL procedure only for
/// as long as nobody writes the next one, and the pull towards hand SQL is strong precisely when it
/// matters: something is wrong with a row at two in the morning and one UPDATE would fix it.
///
/// <para>The cost is not hypothetical. The Karvan run's record was corrected with hand-edited SQL in
/// two databases, and the procedure was written into <c>.conductor/WATCH-HANDOFF.md</c> for the next
/// person to repeat — a repair that takes no backup, checks no liveness (it will happily write a
/// store a live engine is using), leaves no provenance, and cannot be tested. This scan is what keeps
/// the second one from being written: every write to <c>runs</c> goes through the store, where those
/// four properties are somebody's job.</para>
/// </summary>
public class KS0_2NoRunsUpdateOutsideTheStoreTests
{
    /// <summary>The only files allowed to write the table. Not a directory: the store is one class,
    /// and "anything under Store/" would let a new maintenance helper reach past
    /// <c>IRunStore</c>.</summary>
    private static readonly string[] Sanctioned = ["SqliteRunStore.Sessions.cs"];

    [Fact]
    public void NothingOutsideTheStoreWritesTheRunsTable()
    {
        var offenders = Scan(ShippedFiles(RepoRoot())).ToList();

        Assert.True(offenders.Count == 0,
            "these write the runs table without going through the store: " +
            string.Join("; ", offenders) +
            " - use IRunStore (UpdateRunStatus / CloseRunRecord / RecordRunEnd), which backs up " +
            "nothing it need not, refuses a store a live engine is using, and journals who changed " +
            "what. That is the whole of KS0.2.");
    }

    /// <summary>The scan is only worth having if it can fail, and a bar nobody has watched go red is
    /// a bar nobody knows the shape of. Both spellings a real offender would use are tried.</summary>
    [Theory]
    [InlineData("UPDATE runs SET status = 'completed' WHERE run_id = @id")]
    [InlineData("update  runs\n   set ended_utc = '2026-08-05'")]
    public void TheScanGoesRedOnASeededViolation(string sql)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "conductor-ks02arch-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(tmp);
        try
        {
            var seeded = Path.Combine(tmp, "HelpfulFixer.cs");
            File.WriteAllText(seeded, "var sql = \"" + sql.Replace("\n", "\\n", StringComparison.Ordinal) + "\";");
            Assert.Single(Scan([seeded]));

            // and a store method that only READS the table is not an offender, or the bar is noise
            var reader = Path.Combine(tmp, "Reader.cs");
            File.WriteAllText(reader, "var sql = \"SELECT status FROM runs WHERE run_id = @id\";");
            Assert.Empty(Scan([reader]));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch (IOException) { }
        }
    }

    // ------------------------------------------------------------------------------- the scan

    /// <summary>Multiline because SQL in this tree is built across concatenated string literals, and
    /// an UPDATE whose table name landed on the next line would otherwise walk straight through.</summary>
    private static readonly Regex WritesRuns = new(
        @"\bupdate\s+runs\b|\bdelete\s+from\s+runs\b|\binsert\s+(or\s+\w+\s+)?into\s+runs\b",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// The one way past this bar, and it is deliberately loud. Some files carry write-SQL as a
    /// <b>payload</b> rather than as a write: <c>tools/sf1/sf1-2-live-proof.ps1</c> fires
    /// <c>DELETE FROM runs</c> at the control plane precisely to prove the endpoint answers 404 and
    /// no longer executes anything. Excluding that file by path would have quietly excluded every
    /// future line in it too, so the waiver rides the line itself: greppable, one line wide, and it
    /// has to say why.
    /// </summary>
    private const string Waiver = "runs-write-scan:allow";

    /// <summary>Line by line, plus each adjacent pair, so SQL split across two concatenated literals
    /// is still seen. A waiver is honoured on the matching line or the line either side of it — the
    /// comment above the payload is where a person would naturally write it, and three lines is a
    /// small enough blast radius to read at a glance.</summary>
    private static IEnumerable<string> Scan(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            if (Sanctioned.Contains(Path.GetFileName(file), StringComparer.Ordinal)) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var window = i + 1 < lines.Length ? lines[i] + "\n" + lines[i + 1] : lines[i];
                var m = WritesRuns.Match(window);
                if (!m.Success) continue;

                var waived = false;
                for (var j = Math.Max(0, i - 1); j <= Math.Min(lines.Length - 1, i + 1); j++)
                    waived |= lines[j].Contains(Waiver, StringComparison.Ordinal);
                if (waived) continue;

                yield return $"{Path.GetFileName(file)}:{i + 1} ({m.Value.Trim()})";
                break;
            }
        }
    }

    /// <summary>Everything this repo ships and could run against a real store: the engine, and the
    /// operator scripts under <c>tools/</c> — bug #35 is a live reminder that those reach real
    /// databases. Tests are excluded on purpose: a fixture that seeds a phantom row by writing one is
    /// how the phantom gets reproduced, and reproducing the defect is the point of a fixture. The
    /// migrations are excluded because creating the table is not writing a row.</summary>
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
