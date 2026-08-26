namespace Conductor.Core.Integrations;

/// <summary>One job in one workflow file, reduced to what CH1.3 compares.</summary>
/// <param name="File">Workflow file name, e.g. <c>ci.yml</c> — named in every finding, because the
/// answer to "CI runs a step you do not" is always an edit to a specific file.</param>
/// <param name="Job">The job key as written, e.g. <c>windows</c>.</param>
/// <param name="RunsOn">The <c>runs-on</c> value, e.g. <c>windows-latest</c>. Empty when the job did
/// not state one in a form this reader understands.</param>
/// <param name="Steps">Every <c>run:</c> block in the job, in order, as written.</param>
public sealed record CiJob(string File, string Job, string RunsOn, IReadOnlyList<string> Steps);

/// <summary>
/// CH1.3 — what the workflows in <c>.github/workflows</c> actually run, read off disk.
///
/// <para><b>Not a YAML parser, and it must not pretend to be one.</b> It scans for job keys,
/// <c>runs-on</c> and <c>run:</c> blocks and understands nothing else — no anchors, no flow
/// mappings, no composite actions. That is enough to answer "which commands does this job execute",
/// and a full YAML dependency for one comparison would be the wrong trade. The safety rule that
/// makes the naivety acceptable: <b>when it cannot find what it is looking for it says so</b>
/// (<see cref="CiAgreementProbe"/> reports "no CI job on this platform was found") rather than
/// returning an empty list that reads as agreement. Silence-as-green is the exact failure this
/// checkpoint exists to end.</para>
/// </summary>
public static class CiWorkflows
{
    /// <summary>Where GitHub looks, so where this looks.</summary>
    public const string WorkflowDir = ".github/workflows";

    /// <summary>Every job of every workflow file under <paramref name="repoRoot"/>, in file order.
    /// An unreadable file is skipped rather than thrown on — the report must render on a machine
    /// with a locked file as readily as on a clean one.</summary>
    public static IReadOnlyList<CiJob> Read(string repoRoot)
    {
        var dir = Path.Combine(repoRoot ?? "", WorkflowDir.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(dir)) return [];

        var jobs = new List<CiJob>();
        foreach (var file in Directory.EnumerateFiles(dir)
                     .Where(f => f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                              || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            jobs.AddRange(Parse(Path.GetFileName(file), lines));
        }
        return jobs;
    }

    /// <summary>The scan, exposed so it can be tested on text rather than on a directory.</summary>
    public static IReadOnlyList<CiJob> Parse(string file, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var jobs = new List<CiJob>();
        var inJobs = false;
        string? job = null;
        var runsOn = "";
        var steps = new List<string>();

        void Flush()
        {
            if (job is not null) jobs.Add(new CiJob(file, job, runsOn, steps));
            job = null; runsOn = ""; steps = [];
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith('#') || line.Trim().Length == 0) continue;

            var indent = Indent(line);
            var trimmed = line.Trim();

            // A step may be written `- run: x` or as `- name: …` followed by `run: x` on its own
            // line. Both are the same YAML; peel the sequence dash so one branch handles both, and
            // remember that a key behind a dash sits two columns further right than its line does —
            // the block-scalar dedent test below is measured from the KEY, not from the line.
            var keyIndent = indent;
            while (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                trimmed = trimmed[2..].TrimStart();
                keyIndent += 2;
            }

            // Top-level key: `jobs:` opens the section, anything else at column 0 closes it.
            if (indent == 0)
            {
                if (job is not null) Flush();
                inJobs = trimmed.StartsWith("jobs:", StringComparison.Ordinal);
                continue;
            }
            if (!inJobs) continue;

            // A job key is the only 2-space-indented bare key inside `jobs:`.
            if (keyIndent == 2 && trimmed.EndsWith(':'))
            {
                Flush();
                job = trimmed[..^1].Trim();
                continue;
            }
            if (job is null) continue;

            if (trimmed.StartsWith("runs-on:", StringComparison.Ordinal))
            {
                runsOn = trimmed["runs-on:".Length..].Trim().Trim('"', '\'');
                continue;
            }
            if (!trimmed.StartsWith("run:", StringComparison.Ordinal)) continue;

            var inline = trimmed["run:".Length..].Trim();
            if (inline is not ("|" or ">" or "|-" or ">-" or "|+" or ">+"))
            {
                if (inline.Length > 0) steps.Add(inline.Trim('"', '\''));
                continue;
            }

            // Block scalar: everything indented deeper than the `run:` key, until it dedents.
            var block = new List<string>();
            for (var k = i + 1; k < lines.Count; k++)
            {
                if (lines[k].Trim().Length == 0) { block.Add(""); continue; }
                if (Indent(lines[k]) <= keyIndent) break;
                block.Add(lines[k].Trim());
                i = k;
            }
            var text = string.Join("\n", block).Trim();
            if (text.Length > 0) steps.Add(text);
        }
        Flush();
        return jobs;
    }

    private static int Indent(string line)
    {
        var n = 0;
        while (n < line.Length && line[n] == ' ') n++;
        return n;
    }
}
