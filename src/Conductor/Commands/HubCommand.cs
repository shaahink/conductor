using System.ComponentModel;
using System.Diagnostics;

using Conductor.Core.Fleet;
using Conductor.Core.Planning;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// KS2.1 — what bare <c>conductor</c> does now.
///
/// <para>It used to print forty-one verbs. That is a table of contents handed to someone who asked to
/// come in: correct, complete, and no answer to any question they had. The questions people actually
/// arrive with are "is anything running", "what did this machine do before", "is there a plan here",
/// and "what can I do" — so bare <c>conductor</c> answers those four and offers the four things worth
/// doing about them.</para>
///
/// <para><b>Reached by rewrite, not by a default command.</b> Program.cs turns an empty argv into
/// <c>hub</c>; <c>SetDefaultCommand</c> is deliberately not used. With a default command in place,
/// Spectre parses an unknown first token as that command's ARGUMENT, so <c>conductor nosuchverb</c>
/// would stop being an error and start being a hub launch with a stray word — and every scripted verb
/// call is on the other side of that behaviour. The rewrite fires only on a genuinely empty argv, so
/// <c>--help</c>, <c>--version</c>, every verb and every mistyped one reach the parser exactly as
/// before. The verb itself is hidden for the same reason: <c>--help</c>'s list must not move.</para>
///
/// <para><b>It never resolves "the" plan.</b> <see cref="PlanSettings.ResolvePlanPath"/> prompts on an
/// ambiguous directory and throws on an empty one. The front door may do neither — a machine with no
/// plan anywhere is exactly where someone types this — so plans are DISCOVERED
/// (<see cref="PlanDiscovery"/>) and listed, however many there are, and zero is a normal outcome.</para>
///
/// <para><b>Redirected output is a different question.</b> A pipe is not a person: it gets the board on
/// stdout and exit 0, never a picker and never a prompt, so <c>conductor | cat</c> in a script cannot
/// hang waiting for a keystroke nobody is there to press.</para>
///
/// <para><b>The board itself is not this file's</b> (KS2.5). Gathering lives in
/// <see cref="MachineBoard"/> and drawing in <see cref="HubView"/>, because <c>status</c> in a
/// directory that names no plan answers the same question and must not answer it differently.</para>
/// </summary>
public sealed class HubCommand : AsyncCommand<HubCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        // Deliberately NOT PlanSettings: -p would imply this verb resolves a plan, and the hub's whole
        // point is that it works in a directory that has none, one, or eleven.
        [CommandOption("--timeout <MS>")]
        [Description("Per-port probe budget in milliseconds (default 2500). Raise it if a busy engine is missing from the board.")]
        public int? TimeoutMs { get; init; }
    }

    /// <summary>The branch, as a rule rather than an <c>if</c> buried in a method: a board goes to
    /// anything that is not a person at a terminal. Either handle being redirected is enough — output
    /// through a pipe means nobody sees a prompt, input through a pipe means nobody can answer it.</summary>
    public static bool PrefersBoard(bool outputRedirected, bool inputRedirected) =>
        outputRedirected || inputRedirected;

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var cwd = Directory.GetCurrentDirectory();
        var timeout = TimeSpan.FromMilliseconds(
            settings.TimeoutMs is > 0 ? settings.TimeoutMs.Value : FleetScan.DefaultProbeTimeout.TotalMilliseconds);
        var (model, fleet) = await MachineBoard
            .GatherAsync(cwd, MachineBoard.Discover(cwd), timeout, DateTime.UtcNow).ConfigureAwait(false);

        foreach (var line in HubView.Board(model)) Console.WriteLine(line);

        if (PrefersBoard(Console.IsOutputRedirected, Console.IsInputRedirected)) return 0;

        var chosen = Ask();
        return chosen is null ? 0 : await ActAsync(chosen.Value, model, fleet).ConfigureAwait(false);
    }

    // ── the four ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The menu. Null means quit, which is the way out and not a fifth action.</summary>
    private static HubActionKind? Ask()
    {
        var labels = HubActions.All.Select(a => $"{a.Label} — {a.Hint}").Append(HubActions.QuitLabel).ToArray();
        var picked = AnsiConsole.Prompt(
            new SelectionPrompt<string>().Title("what now?").AddChoices(labels));
        var index = Array.IndexOf(labels, picked);
        return index >= 0 && index < HubActions.All.Count ? HubActions.All[index].Kind : null;
    }

    private static async Task<int> ActAsync(HubActionKind kind, HubModel model, IReadOnlyList<FleetRun> fleet) => kind switch
    {
        HubActionKind.Attach => await AttachAsync(model, fleet).ConfigureAwait(false),
        HubActionKind.Start => await StartAsync(model).ConfigureAwait(false),
        HubActionKind.PlanNew => await SiblingAsync("init").ConfigureAwait(false),
        _ => await SiblingAsync("history").ConfigureAwait(false),
    };

    /// <summary>Open the Face on a live run, through the same launcher <c>conductor face</c> uses so
    /// the two doors cannot drift into two token rules.</summary>
    private static async Task<int> AttachAsync(HubModel model, IReadOnlyList<FleetRun> fleet)
    {
        var choices = model.Attachable;
        if (choices.Count == 0)
        {
            AnsiConsole.MarkupLine($"[grey]nothing to attach to — no run is answering on ports {HubView.Ports}.[/]");
            return 0;
        }

        var row = choices.Count == 1 ? choices[0] : AnsiConsole.Prompt(
            new SelectionPrompt<HubRunRow>()
                .Title("attach to which run?")
                .UseConverter(r => $"{r.Label}  {r.PlanName}  {r.ShortRunId}  {HubView.Status(r)}  {r.BaseUrl}")
                .AddChoices(choices));

        var run = fleet.FirstOrDefault(f => f.Port == row.Port && FleetScan.SameDir(f.StateDir, row.StateDir));
        var token = run is null ? null : await FleetScan.ReadTokenAsync(run).ConfigureAwait(false);
        // KS2.4: the fleet travels with the attach, so a Face opened from the front door can switch to
        // another run — or open a finished one read-only — without coming back out through the hub.
        return await FaceCommand.AttachAsync(row.BaseUrl, token,
            await FaceCommand.EnvelopeAsync(fleet, localStateDir: null).ConfigureAwait(false)).ConfigureAwait(false);
    }

    /// <summary>Start a run from a plan here. KS2.3: the itinerary first — <c>journey</c> writes no
    /// state and spawns no agent — then, on a yes, the SAME detached spawn <c>run --detach</c> uses,
    /// and the Face attaches to the URL the child published. The order lives in
    /// <see cref="HubLaunch.StartFlowAsync"/> where a test can hold it still.</summary>
    private static async Task<int> StartAsync(HubModel model)
    {
        if (model.Plans.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]no plans here. [/][yellow]conductor init[/][grey] scaffolds one.[/]");
            return 0;
        }

        var plan = model.Plans.Count == 1 ? model.Plans[0] : AnsiConsole.Prompt(
            new SelectionPrompt<HubPlanRow>()
                .Title("start which plan?")
                .UseConverter(p => $"{p.Name}  ({p.Path})")
                .AddChoices(model.Plans));

        var full = Path.GetFullPath(plan.Path);
        return await HubLaunch.StartFlowAsync(
            full,
            p => SiblingAsync("journey", "-p", p),
            () => AnsiConsole.Confirm($"launch [yellow]{Markup.Escape(plan.Name)}[/] detached now?"),
            p => HubLaunch.LaunchDetachedAsync(p, CancellationToken.None),
            FaceCommand.AttachAsync,
            line => AnsiConsole.MarkupLine($"[grey]{Markup.Escape(line)}[/]")).ConfigureAwait(false);
    }

    /// <summary>Run another verb of THIS binary, sharing the console. The hub is a door onto the CLI,
    /// not a second implementation of it — <see cref="RunDetach.ResolveSelf"/> is the same resolution
    /// the detached launcher uses, so an installed binary and a <c>dotnet run</c> both work.</summary>
    private static async Task<int> SiblingAsync(params string[] argv)
    {
        var (exe, prefix, error) = RunDetach.ResolveSelf();
        if (error is not null)
        {
            AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(error)}");
            return 1;
        }

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Directory.GetCurrentDirectory(),
        };
        foreach (var a in prefix) psi.ArgumentList.Add(a);
        foreach (var a in argv) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi);
        if (proc is null) return 1;
        await proc.WaitForExitAsync().ConfigureAwait(false);
        return proc.ExitCode;
    }
}
