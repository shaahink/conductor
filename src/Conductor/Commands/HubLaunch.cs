using System.Globalization;
using System.Text.Json;

using Conductor.Models;

namespace Conductor.Commands;

/// <summary>What a hub launch hands back to the flow. <see cref="BaseUrl"/> and <see cref="Token"/>
/// come from the child's own discovery file by way of <see cref="DetachOutcome.Info"/> — the hub
/// never predicts a port, because the engine scans forward when its preference is taken and a
/// predicted URL is a plausible lie exactly when a second run is live. A null <see cref="BaseUrl"/>
/// with <see cref="Ok"/> true is an engine that is alive but not yet attachable; <see cref="Detail"/>
/// is the plain-text sentence the hub prints either way.</summary>
public sealed record HubLaunchResult(bool Ok, string? BaseUrl, string? Token, string Detail);

/// <summary>
/// KS2.3 — the hub's start action, past the itinerary.
///
/// <para>KS2.1 stopped at "start it with <c>conductor run -p …</c>" — a door that pointed at another
/// door. Now confirming launches the engine DETACHED through <see cref="RunDetach.SpawnAsync"/>, the
/// same spawn/handshake/settle path <c>run --detach</c> uses, and then attaches the Face to the URL
/// the child published. One code path, because the sarban field log's hand-rolled
/// <c>Start-Process … -RedirectStandardError</c> incantation is exactly what this checkpoint retires:
/// a launch shape that lived in a doc instead of the engine forgot its own hard-won lessons (the
/// capture log, the pid check, the settle) every time it was retyped.</para>
///
/// <para><b>The order is the contract.</b> Preview first (<c>journey</c> — no state written, no agent
/// spawned), confirm second, spawn third, attach last and only to a URL that was read back. The flow
/// takes its steps as functions so a test can prove that order without a terminal or a process.</para>
/// </summary>
public static class HubLaunch
{
    /// <summary>The start flow: itinerary, consent, detached spawn, attach. Each step is injected —
    /// the real wiring lives in <c>HubCommand.StartAsync</c>, and the tests prove the ORDER: nothing
    /// spawns before the preview has rendered and the person has said yes, and nothing attaches
    /// except to a launch that measurably survived.</summary>
    public static async Task<int> StartFlowAsync(
        string planPath,
        Func<string, Task<int>> previewAsync,
        Func<bool> confirm,
        Func<string, Task<HubLaunchResult>> launchAsync,
        Func<string, string?, Task<int>> attachAsync,
        Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(previewAsync);
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(launchAsync);
        ArgumentNullException.ThrowIfNull(attachAsync);
        ArgumentNullException.ThrowIfNull(say);

        // The itinerary BEFORE anything is spawned — and a plan whose journey cannot even render is
        // not a plan to offer a launch button for.
        var previewed = await previewAsync(planPath).ConfigureAwait(false);
        if (previewed != 0) return previewed;

        if (!confirm())
        {
            say($"not launched. start it yourself with: conductor run -p {planPath} --detach");
            return 0;
        }

        var result = await launchAsync(planPath).ConfigureAwait(false);
        say(result.Detail);
        if (!result.Ok) return 1;
        // Alive but not attachable (plane not published inside the handshake window, or disabled):
        // the detail already says where to look, and a hub that waited longer would just be a worse
        // `conductor face` — which is the tool for exactly this moment.
        if (string.IsNullOrEmpty(result.BaseUrl)) return 0;
        return await attachAsync(result.BaseUrl, result.Token).ConfigureAwait(false);
    }

    /// <summary>The real launcher: default run shape (headless, no face, control plane on its default
    /// preference — the bound port is read back, not assumed), through the shared detach path.</summary>
    public static async Task<HubLaunchResult> LaunchDetachedAsync(string planPath, CancellationToken ct)
    {
        PlanConfig plan;
        try { plan = PlanConfig.Load(planPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                      or InvalidOperationException or ArgumentException)
        {
            return new HubLaunchResult(false, null, null, $"cannot load plan: {e.Message}");
        }

        var outcome = await RunDetach.SpawnAsync(new RunCommand.Settings(), planPath, plan, ct).ConfigureAwait(false);
        return ResultOf(outcome);
    }

    /// <summary>Maps the measured spawn outcome onto the flow's vocabulary. Pure, and public for the
    /// tests: these four sentences are the whole difference between "attached", "alive, look here",
    /// and "dead, look here" — the three answers a person who just said yes can be given.</summary>
    public static HubLaunchResult ResultOf(DetachOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        if (!outcome.SpawnOk)
            return new HubLaunchResult(false, null, null, outcome.Error ?? "detach failed");

        var pid = outcome.Pid.ToString(CultureInfo.InvariantCulture);
        if (outcome.Info is { } info)
            return new HubLaunchResult(true, info.BaseUrl, info.Token,
                $"run detached — pid {pid} · {info.BaseUrl} · console: {outcome.DetachLog}");

        return outcome.EngineAlive
            ? new HubLaunchResult(true, null, null,
                $"the engine (pid {pid}) is alive but has not published its control plane yet — attach later with conductor face; console: {outcome.DetachLog}")
            : new HubLaunchResult(false, null, null,
                $"the engine (pid {pid}) exited before its control plane was usable — console: {outcome.DetachLog}");
    }
}
