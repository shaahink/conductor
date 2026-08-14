using System.Globalization;

using Conductor.Core.Fleet;
using Conductor.Core.Planning;

namespace Conductor.Commands;

/// <summary>
/// KS2.5 — what <c>conductor status</c> answers when the directory does not name a plan.
///
/// <para>It used to answer with an exception. In a directory with no plan file the resolver threw "No
/// plan found"; in one with several — this repo has eleven under <c>plans/</c> — it threw <i>Multiple
/// plan files found and output is not interactive to prompt</i> the moment output was redirected, and
/// interrogated the reader with a picker when it was not. Both are refusals, and <c>status</c> is the
/// verb people type when they do not know what is going on: refusing to say anything is the least
/// useful thing it can do at exactly the moment it is asked. Worse, the throw ran through the crash
/// handler, so asking a question in the wrong directory left a <c>crash-*.log</c> behind.</para>
///
/// <para><b>The question widens instead of failing.</b> "Status of what?" has an answer even when no
/// plan is named: the machine. The fleet probe says what is running, the catalogue says what ran, and
/// the plans here are listed rather than chosen between. It is the same board the hub prints
/// (<see cref="HubView"/>), gathered by the same code (<see cref="MachineBoard"/>), because two
/// renderings of one truth is how one of them starts lying.</para>
///
/// <para><b>The branch is taken before anything can prompt.</b> <see cref="PlanForStatus"/> is pure and
/// consults no console: whether output is redirected cannot change which branch runs, only what a
/// terminal would have done with it. That is what makes "the multiple-plan-files error is unreachable
/// from status" a claim a test can hold, on a TTY as well as through a pipe.</para>
///
/// <para><b>The note goes to stderr.</b> The board is stdout's answer and stays the whole of it, so a
/// script reading <c>conductor status</c> gets a board and not a board with an apology on top.</para>
/// </summary>
public static class StatusBoard
{
    /// <summary>
    /// Which plan <c>status</c> is about, or null for "this directory does not answer that question".
    ///
    /// <para>Precedence is KS0.3's, unchanged and deliberately borrowed rather than re-stated:
    /// <see cref="PlanResolution.Decide"/> is the one rule, so <c>status</c> cannot drift from the other
    /// thirty verbs about which plan it means. Only the ENDING differs — where the shared shell prompts
    /// or throws, this returns null and the caller shows the machine.</para>
    ///
    /// <para><paramref name="exists"/> guards one real case: a <c>CONDUCTOR_PLAN</c> left over from a
    /// run whose plan file has since been deleted or renamed. The variable still resolves, the load
    /// still throws, and the honest answer is the same "no plan resolves here" the empty directory
    /// gets. An explicit <c>-p</c> is exempt: a path someone typed and got wrong is an error to be
    /// told about, not a reason to quietly change the subject.</para>
    /// </summary>
    public static string? PlanForStatus(
        string? explicitPlan, string? envPlan,
        IReadOnlyList<PlanDiscovery.Candidate> candidates, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);

        if (!string.IsNullOrWhiteSpace(explicitPlan)) return explicitPlan;

        var path = PlanResolution.Decide(explicitPlan: null, envPlan, candidates).Path;
        if (string.IsNullOrWhiteSpace(path)) return null;
        return exists(path) ? path : null;
    }

    /// <summary>The sentence that says why a reader who asked about a plan is looking at a machine.
    /// Without it the board is a non-sequitur; with it, it is an answer plus the way to narrow it.</summary>
    public static string Why(int plansHere) => plansHere switch
    {
        0 => "no plan resolves here — showing this machine instead. " +
             "conductor status -p <plan> reports one run; conductor init scaffolds a plan.",
        1 => "the plan here does not resolve — showing this machine instead. " +
             "conductor status -p <plan> reports one run.",
        _ => plansHere.ToString(CultureInfo.InvariantCulture) +
             " plans here and nothing chooses between them — showing this machine instead. " +
             "conductor status -p <plan> reports one run.",
    };

    /// <summary>Prints the machine. Exit 0 always: "nothing is running and nothing ran" is an answer,
    /// and every gathering failure inside <see cref="MachineBoard"/> degrades to a quieter board rather
    /// than a refusal.</summary>
    /// <param name="cwd">Where the reader is standing — named on the board, and where plans are found.</param>
    /// <param name="plans">Already discovered, so the note can be written before the probe's latency.</param>
    public static async Task<int> RenderAsync(
        string cwd, IReadOnlyList<PlanDiscovery.Candidate> plans, TimeSpan probeTimeout)
    {
        ArgumentNullException.ThrowIfNull(plans);

        await Console.Error.WriteLineAsync("note: " + Why(plans.Count)).ConfigureAwait(false);

        var (model, _) = await MachineBoard
            .GatherAsync(cwd, plans, probeTimeout, DateTime.UtcNow).ConfigureAwait(false);

        // Plain text, like the hub's board: every cell is a repo path or a plan name off someone else's
        // disk, and Spectre would read a literal '[' in one of them as the start of a style tag.
        foreach (var line in HubView.Board(model)) Console.WriteLine(line);
        return 0;
    }

    /// <summary>The probe budget, bounded by <see cref="FleetScan.DefaultProbeTimeout"/>. This is the
    /// floor of <c>status</c>'s latency on the no-plan branch and the reason the probe runs on that
    /// branch only — a verb that answers in under a second from a database must not start paying for
    /// twenty sockets on the path where it already knows which database to open.</summary>
    public static TimeSpan ProbeTimeout => FleetScan.DefaultProbeTimeout;
}
