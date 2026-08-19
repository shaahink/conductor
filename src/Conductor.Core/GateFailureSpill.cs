using System.Globalization;

namespace Conductor.Core;

/// <summary>
/// KS7.5 — a failed gate's output goes to a FILE and an excerpt goes in the prompt.
/// </summary>
/// <remarks>
/// <para><b>What this is worth, measured rather than hoped.</b> Real composed prompts in this repo run
/// 17.7k–26.3k characters (~4.4k–6.6k tokens) against a mean turn of 135k–195k tokens, so the whole
/// prompt is 3–4% of a turn and a gate block is 0.2–0.6% of it. Trimming the prompt cannot move this
/// project's 66% cache-read bill — that bill is what accumulates DURING a session. So this is not
/// sold as a saving. It is sold as the right shape: the fix session gets the whole gate output
/// available at a path instead of a 4,000-character truncation it cannot see past, and pays for the
/// excerpt on every turn rather than for the tail.</para>
/// <para>Before: <c>GateRunner.FailureDetails</c> pasted up to 4,000 characters PER FAILED GATE into
/// the fix prompt, and anything past that was gone — a fix session facing a 200-failure build log saw
/// the last 4,000 characters and no way to reach the rest.</para>
/// <para>The excerpt is the TAIL, not the head: a build or test log puts its verdict at the bottom.</para>
/// </remarks>
public static class GateFailureSpill
{
    /// <summary>Characters of each failed gate kept in the prompt. Enough for the summary line and the
    /// few lines above it that name the failing test; the rest is one Read away.</summary>
    public const int ExcerptChars = 700;

    /// <summary>Where the full outputs go, relative to the state dir.</summary>
    public const string DirName = "gate-output";

    /// <summary>
    /// Renders the fix prompt's gate-failure block, spilling each gate's full output to
    /// <paramref name="stateDir"/>/<see cref="DirName"/> and naming the file in the block.
    /// </summary>
    /// <param name="results">The battery's results; passing and skipped gates are ignored.</param>
    /// <param name="stateDir">The run's state dir. Null or unwritable = no spill, and the block falls
    /// back to the excerpt alone — a prompt is never blocked on a disk problem.</param>
    /// <param name="sessionNumber">Stamped into the filename so a stage's successive attempts do not
    /// overwrite each other's evidence.</param>
    public static string Render(IEnumerable<GateResult> results, string? stateDir, int sessionNumber)
    {
        ArgumentNullException.ThrowIfNull(results);
        // KS4.2: a regressing gate PASSED — it is red because a check that used to pass is gone — so
        // the ordinary filter drops it and the fix session is told "(no gate output captured)" about
        // the one failure it most needs explained. This is the same lesson KS4.1 paid for: the
        // engine has two fix-brief renderers and only one of them is on the path a real run takes.
        // KS4.3 walks the same lesson: the mutation class is the second failure shape whose gate
        // EXITED 0, so it joins the filter here on the same predicate rather than at one renderer.
        var failed = results.Where(r => (!r.Passed && !r.Skipped) || r.HasClassFailure).ToList();
        if (failed.Count == 0) return "";

        var dir = Prepare(stateDir);
        var parts = new List<string>(failed.Count);
        foreach (var r in failed)
        {
            // Nothing to spill: the gate's own output is a success message. What the fix session
            // needs is the class's finding, and all of it fits in the prompt.
            if (r.HasRegressions) { parts.Add(GateRunner.RegressionDetail(r)); continue; }
            if (r.HasMutationShortfall) { parts.Add(GateRunner.MutationDetail(r)); continue; }
            var path = dir is null ? null : Spill(dir, r, sessionNumber);
            var excerpt = r.Tail.Length > ExcerptChars ? "…" + r.Tail[^ExcerptChars..] : r.Tail;
            // SC4.1: say it was retried. A fix session that knows the gate failed TWICE does not waste
            // its first move re-running it to see whether the battery was just unlucky.
            var retried = r.Retried ? ", failed twice — retried once" : "";
            var head = $"### Gate `{r.Name}` FAILED (exit {r.ExitCode.ToString(CultureInfo.InvariantCulture)}, " +
                       $"{r.Duration.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}s{retried})";

            var body = $"{head}\n```\n{excerpt}\n```";
            if (path is not null && r.Tail.Length > excerpt.Length)
                body += $"\nFull output ({r.Tail.Length.ToString(CultureInfo.InvariantCulture)} chars): `{path}` " +
                        "— read it only if the excerpt above does not name the cause.";
            else if (path is not null)
                body += $"\nFull output: `{path}` (the excerpt above is all of it).";

            parts.Add(body);
        }

        return string.Join("\n\n", parts);
    }

    private static string? Prepare(string? stateDir)
    {
        if (string.IsNullOrWhiteSpace(stateDir)) return null;
        try
        {
            var dir = Path.Combine(stateDir, DirName);
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string? Spill(string dir, GateResult r, int sessionNumber)
    {
        try
        {
            var safe = string.Concat(r.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
            var path = Path.Combine(dir, $"s{sessionNumber.ToString("000", CultureInfo.InvariantCulture)}-{safe}.log");
            // Sync I/O at a sync boundary: PendingFix is built inside the verdict engine's synchronous
            // decision path, and a few KB of gate text is not worth making that path async.
#pragma warning disable MA0045 // sync write at the verdict engine sync decision path - see the two lines above
            File.WriteAllText(path, r.Tail);
#pragma warning restore MA0045
            return path;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
