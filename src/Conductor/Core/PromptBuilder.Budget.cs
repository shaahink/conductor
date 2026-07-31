using Conductor.Models;

namespace Conductor.Core;

public sealed partial class PromptBuilder
{
    /// <summary>B13.4: what this session may spend and how to spend it. Empty when the plan sets no
    /// per-session ceiling, so a run without a budget reads exactly as it did before.</summary>
    /// <remarks>
    /// <para>An agent cannot see its own token usage, so left to itself it optimises for thoroughness
    /// at any price — and the price is not linear. A session is billed roughly turns × context, and
    /// context only ever grows, so the two-hundredth turn of a session costs several times the tenth.
    /// Measured on this machine: a 281-turn session read 47M cached tokens for 244k of output, and
    /// three quarters of that bill was the last half of the session re-reading its own history.</para>
    /// <para>Which makes the expensive habits the ordinary ones — reading a whole 100KB design document
    /// when two sections were wanted, re-reading a file already in context, exploring past the point of
    /// knowing. None of them look wasteful while they happen; each one is paid for again on every
    /// subsequent turn. The rules below are the few that actually move the number, and they are stated
    /// as budget rather than as virtue so the agent can trade against them: the goal is not a cheap
    /// session, it is a session that lands work and then stops.</para>
    /// </remarks>
    private string BudgetSection()
    {
        if (_plan.Limits.MaxSessionTokens is not { } max || max <= 0) return "";
        var soft = _plan.Limits.SoftBreakRatio is { } r and > 0 and <= 1.0 ? r : 0.8;
        return $"""
            ## Session budget — {max / 1_000_000.0:0.##}M tokens

            This session has a token ceiling and the orchestrator enforces it. At about
            {soft * 100:0}% you will be told to wrap up; at 100% the session is ended where it stands.
            Committed work survives that, uncommitted work does not.

            Your cost is roughly (number of turns) x (size of your context), and your context only
            grows. So the last stretch of a long session costs several times the first, and anything
            you pull into context early is paid for again on every turn after it.

            Spend it like this:
            - LAND WORK IN COMMITS, EARLY AND OFTEN. A committed checkpoint is banked; an uncommitted
              one is at risk for the rest of the session. Never save up a single commit for the end.
            - Read what you need, not what exists. Open the SECTIONS of a large design document that
              your checkpoint names — not the whole file. Prefer grep/search to opening files whole.
            - Never re-read a file already in this context. Scroll back instead; it is still there and
              re-reading it charges you twice for the same bytes.
            - Do not re-explore ground the handoff already covers. It was written for you by the last
              session precisely so you would not have to.
            - Do not run the same verification twice. One gate run, then act on it.

            The opposite failure is just as real and costs more: a session that reads a few files,
            declares the situation complex and exits WITHOUT landing a checkpoint has spent its whole
            context and delivered nothing, and the next session starts from the same place. Deliver
            something real, prove it, commit it, write the handoff. If the stage is genuinely blocked,
            say so with evidence in the handoff — that is a result, and it also ends the session.
            """;
    }
}
