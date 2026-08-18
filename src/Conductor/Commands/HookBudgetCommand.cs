using System.ComponentModel;
using System.Text.Json;

using Conductor.Core;
using Conductor.Core.Events;

using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// B13.3 — the channel the cooperative soft-break was missing. Run by the agent CLI as a PostToolUse
/// hook; prints a wrap-up instruction into the session's context when, and only when, the orchestrator
/// has raised the soft-break signal for that session. KS7.2 gave the same hook a second, silent job:
/// recording the tool call that fired it (<c>--tool-events</c>).
/// </summary>
/// <remarks>
/// <para>The soft-break was written as a cooperative rail — spend most of the budget, then be asked to
/// land the current sub-task and hand off cleanly — and half of it was missing. The engine wrote
/// <c>.conductor/soft-break</c> and emitted an event, and NOTHING carried either to the agent: a
/// non-interactive <c>claude -p</c> session has no inbox, does not poll the state directory, and was
/// never told the file existed. So the nudge fired into a void every time, and the only rail that could
/// still act was the hard one, which is a kill. A budget with no cooperative half spends its whole
/// margin on discarded work.</para>
/// <para>A hook is the right carrier because it is the one thing that runs INSIDE the agent's loop on
/// the agent's own schedule. Attached to PostToolUse, it rides tool calls the session is making anyway,
/// so the notice arrives within one tool call of the signal without the engine having to interrupt
/// anything. It stays silent otherwise: no signal, no output, no tokens. Timing out or failing must
/// leave the session alone, so every failure path here exits 0 with nothing written.</para>
/// <para>KS7.2 — the same invocation now also RECORDS. The hook already runs once per tool call with
/// the full argument object on its stdin, so making it write that call to
/// <see cref="HookToolLog"/> costs no extra process: a second hook command would have doubled the
/// per-tool-call spawn count to deliver information this one was already being handed and throwing
/// away. The verb keeps its name — it is a hidden wire written by conductor and read by the agent
/// CLI, and renaming it would buy a compatibility story with no reader to serve.</para>
/// </remarks>
public sealed class HookBudgetCommand : AsyncCommand<HookBudgetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--state-dir <path>")]
        [Description("The run's state directory — the one holding the soft-break signal file.")]
        [DefaultValue(".conductor")]
        public string StateDir { get; init; } = ".conductor";

        [CommandOption("--tool-events <path>")]
        [Description("KS7.2 — append the tool call on stdin to this JSONL. Omitted: stdin is never read.")]
        public string? ToolEvents { get; init; }
    }

    /// <summary>The hook payload, for tests. Null means "read the real stdin", and stdin is only read
    /// when <c>--tool-events</c> asked for it — an invocation that does not record must not touch a
    /// stream it has no use for, because a hook that blocks on a handle the host never closes is a
    /// hung tool call in the agent's loop.</summary>
    internal string? Payload { get; init; }

    /// <summary>
    /// Where the hook's JSON goes; <see cref="Console.Out"/> when nothing is supplied, resolved at
    /// write time so a host that redirects the console still receives it.
    /// </summary>
    /// <remarks>Bug #26. The test used to capture this command's output with
    /// <c>Console.SetOut</c>, which is PROCESS-GLOBAL: under the full parallel suite another test's
    /// console writes landed in the same buffer and <c>JsonDocument.Parse</c> failed on them, so the
    /// test passed 10/10 alone and flaked in the battery. A writer the caller owns cannot be
    /// contaminated by whatever is running beside it.</remarks>
    internal TextWriter? Output { get; init; }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var payload = await ReadPayloadAsync(settings).ConfigureAwait(false);
        await RecordToolEventAsync(settings, payload).ConfigureAwait(false);
        try
        {
            // KS7.2: the notice is a PostToolUse output shape and must only be produced on a
            // PostToolUse. The same command is now registered on PreToolUse as well, and emitting a
            // block whose hookEventName does not match the event that ran it is how a hook goes from
            // silent-and-working to rejected-and-silent — the exact failure mode B13.3 was written
            // to end. No payload at all still means PostToolUse: that is the shape every caller
            // before this checkpoint used.
            if (!IsPostToolUse(payload)) return 0;
            if (SoftBreak.ReadSignal(settings.StateDir) is not { } signal) return 0;

            // K1.2: NOT one notice per session. It was, and across the Sarban face run's eleven
            // post-cap rollovers not one session stopped at the nudge — every one of them ran on to
            // the hard ceiling and was killed mid-turn. A rail announced once, hundreds of thousands
            // of tokens before the end, is a rail an agent deep in a task reads and forgets. It is
            // re-stated on a token step and on an interval (SoftBreak.ShouldRestate) until the session
            // ends, and each restatement quotes the budget that is left AT THAT MOMENT.
            var previous = SoftBreak.ReadDelivery(settings.StateDir);
            if (!SoftBreak.ShouldRestate(signal, previous, DateTime.UtcNow, out _)) return 0;
            var delivery = SoftBreak.RecordDelivery(settings.StateDir, previous, signal, DateTime.UtcNow);

            await (Output ?? Console.Out).WriteAsync(JsonSerializer.Serialize(new
            {
                hookSpecificOutput = new
                {
                    hookEventName = "PostToolUse",
                    additionalContext = SoftBreak.Notice(signal, delivery.Count),
                },
            })).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // A hook that cannot read a file must never be the reason a session ends.
            return 0;
        }
    }

    /// <summary>KS7.2 — the recording half. Runs BEFORE the soft-break check and swallows everything:
    /// the budget rail is the job this hook cannot be allowed to fail at, so a broken tool-events path
    /// costs the tool event and nothing else.</summary>
    private async Task RecordToolEventAsync(Settings settings, string? payload)
    {
        if (string.IsNullOrWhiteSpace(settings.ToolEvents) || payload is null) return;
        try
        {
            await HookToolLog.TryAppendFromHookPayloadAsync(settings.ToolEvents!, payload, DateTime.UtcNow).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
        }
    }

    /// <summary>The hook payload, or null when this invocation was not given one. stdin is read only
    /// when <c>--tool-events</c> asked for it: a hook that blocks on a handle its host never closes is
    /// a hung tool call in the agent's loop, and the budget rail worked for a whole era without ever
    /// looking at stdin.</summary>
    private async Task<string?> ReadPayloadAsync(Settings settings)
    {
        if (Payload is not null) return Payload;
        if (string.IsNullOrWhiteSpace(settings.ToolEvents)) return null;
        try { return await Console.In.ReadToEndAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ObjectDisposedException) { return null; }
    }

    /// <summary>True when the payload is a PostToolUse — or when there is no payload to read, which is
    /// how every pre-KS7.2 invocation of this hook arrives.</summary>
    private static bool IsPostToolUse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return true;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("hook_event_name", out var el)
                || el.ValueKind != JsonValueKind.String
                || string.Equals(el.GetString(), "PostToolUse", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return true;
        }
    }
}
