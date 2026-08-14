using System.Globalization;

using Conductor.Core.Fleet;

namespace Conductor.Commands;

/// <summary>
/// KS2.1 — the caravanserai, drawn.
///
/// <para><b>One board, both branches.</b> A terminal gets these exact lines and then a prompt; a pipe
/// gets these exact lines and exit 0. They are not two renderings that have to be kept in step,
/// because the first thing that happens to a status board with two implementations is that the one
/// nobody watches starts lying.</para>
///
/// <para><b>Plain text, deliberately.</b> Every cell here is a repo path, a plan name or a status word
/// that came off a wire or out of someone else's plan file, and Spectre's markup would read a literal
/// <c>[</c> in any of them as the start of a style tag — the same trap that makes an unescaped verb
/// description throw at startup (Program.cs:49). A board printed through <c>Console.WriteLine</c>
/// cannot be crashed by a plan called <c>website[v2]</c>, and the machine reading it through a pipe
/// gets exactly what the human sees.</para>
/// </summary>
public static class HubView
{
    /// <summary>The board: state home, live runs, past runs, plans here, and what can be done. Pure —
    /// same model in, same lines out, no clock and no console.</summary>
    public static IReadOnlyList<string> Board(HubModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var lines = new List<string>
        {
            "conductor — this machine's caravanserai",
            "",
            "  state home  " + model.StateHomeRoot,
            "  here        " + model.Cwd,
            "",
        };

        Runs(lines, model);
        Plans(lines, model);
        Actions(lines);
        return lines;
    }

    private static void Runs(List<string> lines, HubModel model)
    {
        var live = model.LiveRuns;
        var past = model.PastRuns;
        var all = model.Runs;

        // One measure over BOTH halves: live and past runs share a column grid because they are one
        // list, and a past row indented differently from a live one reads as a different table.
        var wLabel = Width(all.Select(r => r.Label), 8, 24);
        var wPlan = Width(all.Select(r => r.PlanName), 4, 22);
        // 34 is `ps`'s status width. One vocabulary and one size across both surfaces, so a reader who
        // learned to spot a parked run in one does not have to learn it again in the other.
        var wStatus = Width(all.Select(Status), 6, 34);

        lines.Add("live runs");
        if (live.Count == 0)
        {
            lines.Add($"  nothing answering on ports {Ports}");
        }
        else
        {
            foreach (var r in live)
            {
                lines.Add("  " + string.Join("  ",
                    Pad(r.Label, wLabel),
                    Pad(r.PlanName, wPlan),
                    Pad(r.ShortRunId, 8),
                    Pad(r.StageId.Length > 0 ? r.StageId : "-", 6),
                    Pad(Status(r), wStatus),
                    Pad(Progress(r), 7),
                    Pad(r.Port > 0 ? ":" + r.Port.ToString(CultureInfo.InvariantCulture) : "no plane", 8),
                    Pad(r.Pid > 0 ? "pid " + r.Pid.ToString(CultureInfo.InvariantCulture) : "", 10),
                    r.When).TrimEnd());
            }
        }

        lines.Add("");
        lines.Add("past runs");
        if (past.Count == 0)
        {
            lines.Add("  this machine remembers no finished runs yet");
        }
        else
        {
            foreach (var r in past)
            {
                // KS2.2: a row whose database this engine could not open is listed like any other, and
                // the reason rides on the END of the line — after the last column, where a long path
                // cannot push anything sideways. Dropping the row instead would make a deleted database
                // indistinguishable from a run this machine never had.
                var why = r.Problem.Length > 0 ? "  " + r.Problem : "";
                lines.Add("  " + string.Join("  ",
                    Pad(r.Label, wLabel),
                    Pad(r.PlanName, wPlan),
                    Pad(r.ShortRunId, 8),
                    Pad(Status(r), wStatus),
                    Pad(Progress(r), 7),
                    Pad(Money(r.CostUsd), 8),
                    r.When + why).TrimEnd());
            }

            // KS2.5: a page is not a machine. When the cap bit, the board says which of the two it is
            // doing — "showing 8 of 23" is the difference between "that run is not on this machine" and
            // "that run is on the second page".
            lines.Add(model.PastTruncated
                ? $"  showing {past.Count.ToString(CultureInfo.InvariantCulture)} of {model.PastTotal.ToString(CultureInfo.InvariantCulture)} — conductor history has the rest"
                : "  conductor history — the rest of what this machine remembers");
        }

        lines.Add("");
    }

    private static void Plans(List<string> lines, HubModel model)
    {
        lines.Add("plans here");
        if (model.Plans.Count == 0)
        {
            // Zero is a normal outcome, not a failure, and this is the sentence that says so. The old
            // front door printed forty-one verbs at exactly this moment and answered none of it.
            lines.Add("  no plans here — conductor init scaffolds one");
        }
        else
        {
            var wName = Width(model.Plans.Select(p => p.Name), 4, 30);
            foreach (var p in model.Plans)
                lines.Add("  " + Pad(p.Name, wName) + "  " + p.Path);
        }

        lines.Add("");
    }

    private static void Actions(List<string> lines)
    {
        lines.Add("what you can do here");
        var w = Width(HubActions.All.Select(a => a.Label), 6, 12);
        foreach (var a in HubActions.All)
            lines.Add("  " + Pad(a.Label, w) + "  " + a.Hint);
        lines.Add("");
        lines.Add("  conductor --help lists every verb.");
    }

    /// <summary>The port window the fleet lives in, spelled the way the probe scans it.</summary>
    public static string Ports =>
        $"{FleetScan.FirstPort.ToString(CultureInfo.InvariantCulture)}-{(FleetScan.FirstPort + FleetScan.PortSpan - 1).ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The status cell: the reconciled word, plus what a parked run is waiting for. Never
    /// re-derived here — see <see cref="HubRunRow.Status"/>.</summary>
    public static string Status(HubRunRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var status = row.Status.Length > 0 ? row.Status : "unknown";
        return string.IsNullOrWhiteSpace(row.Attention) ? status : $"{status} ({row.Attention})";
    }

    private static string Progress(HubRunRow row) =>
        row.Total > 0
            ? $"{row.Done.ToString(CultureInfo.InvariantCulture)}/{row.Total.ToString(CultureInfo.InvariantCulture)}"
            : "";

    private static string Money(decimal usd) =>
        usd > 0 ? "$" + usd.ToString("0.00", CultureInfo.InvariantCulture) : "";

    private static int Width(IEnumerable<string> cells, int min, int max)
    {
        var widest = cells.Select(c => (c ?? "").Length).DefaultIfEmpty(0).Max();
        return Math.Clamp(widest, min, max);
    }

    /// <summary>Clip first, then pad — a cell wider than its column must not push every column after
    /// it sideways, and a cell padded before it is clipped is just a clipped cell with the padding cut
    /// off again.</summary>
    private static string Pad(string? s, int width) =>
        PsCommand.Shorten(s ?? "", width).PadRight(width);
}
