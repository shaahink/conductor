using System.Globalization;
using Conductor.Core.Integrations;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>The queue's SURFACE sources: a channel, the courier, CI - something the plan relies on that
/// is not doing what the plan says. None of them is the run's own progress and all of them share
/// <see cref="RankChannel"/>; they live apart from the progress sources so each file has one job.</summary>
public static partial class OwnerQueue
{
    /// <summary>DV1.1 — an outbound channel the plan configured that cannot deliver.
    ///
    /// <para>The seventh source, and the first that is not about the run's own progress. The
    /// Karvansara edge run's github mirror was enabled with no token: the mirror said so twice, to a
    /// log file, and the queue — the surface whose entire purpose is "the things only you can do" —
    /// listed nothing, for twenty-three sessions, while $324 of work went unrecorded. Nobody but the
    /// owner can supply a token, so a dead channel is an owner obligation by definition, and it
    /// belongs here for exactly the same reason an unapproved gate does.</para>
    ///
    /// <para>Derived like everything else on this board: <see cref="ChannelHealthProbe"/> re-asks the
    /// plan and the environment on every render, so the entry disappears the moment the token
    /// appears — no clearing step, and no way for it to outlive the fault. A channel with no typeable
    /// command (a plan edit) carries <c>""</c>, which the renderer already words correctly.</para></summary>
    private static void CollectDeadChannels(PlanConfig plan, List<OwnerQueueItem> items)
    {
        foreach (var c in ChannelHealthProbe.Loud(ChannelHealthProbe.Collect(plan)))
        {
            items.Add(new OwnerQueueItem(
                Id: $"channel-{c.Channel}",
                Kind: "channel",
                Title: $"{c.Channel} is {c.Word} - {Clip(c.Detail, 200)}",
                // Named precisely. A dead channel unblocks no STAGE, and saying it did would be the
                // lie that costs the queue its credibility; what it costs is the record.
                Unblocks: c.State == ChannelState.Dead
                    ? $"nothing in the run - the run carries on. what it costs is the {c.Channel} record of it, which is being lost right now"
                    : $"nothing in the run - part of what the plan asked {c.Channel} for is not running",
                Command: c.FixCommand,
                // Derived state carries no first-seen stamp on purpose (see the type doc); the render
                // says "unknown" rather than inventing "just now" for a fault that may be hours old.
                SinceUtc: null,
                Rank: RankChannel,
                Detail: c.Fix.Length > 0 ? c.Fix : null));
        }
    }

    /// <summary>PK2.2 / D2(e) - the courier this run restarted. Not an obligation the run is waiting
    /// on: a restart is a silent death, the cause is still unknown, and the owner is who reads it. The
    /// title carries the count, so each restart is new to the queue and reaches the phone again.</summary>
    private static void CollectCourierRestarts(RunState state, List<OwnerQueueItem> items)
    {
        if (state.CourierRestarts is not { } restarts) return;
        items.Add(new OwnerQueueItem(
            Id: "courier-restarted",
            Kind: "courier",
            Title: Clip(restarts.Last, 200),
            Unblocks: "nothing in the run - the run carries on; what it costs is every note and push "
                    + "the courier missed while it was down, and a cause nobody has read yet",
            Command: "conductor courier status",
            SinceUtc: restarts.LastUtc,
            Rank: RankChannel,
            Detail: "restarted " + restarts.Count.ToString(CultureInfo.InvariantCulture)
                  + " time(s) by this run. The courier's log carries the death record and the "
                  + "scheduler's last result."));
    }

    /// <summary>CH1.3 - the run's gate battery and CI are not the same battery.
    ///
    /// <para>The eighth source, and the second that is not about the run's own progress. For the
    /// whole Divan era the phase gate passed 23 checkpoints while CI's windows leg was red on every
    /// commit of the era, and nothing compared them. Only the owner can edit a workflow file or a
    /// plan's gates, so a divergence between the two batteries is an owner obligation by the same
    /// definition a dead channel is - and it costs the same thing: not the run, but the meaning of
    /// the verdict the run just recorded.</para>
    ///
    /// <para>It shares <see cref="RankChannel"/> rather than taking a rank of its own: it is the same
    /// class of obligation - a surface the plan relies on is not doing what the plan says - and a new
    /// rank would renumber four others for no ordering anyone asked for.</para></summary>
    private static void CollectCiDivergence(PlanConfig plan, List<OwnerQueueItem> items)
    {
        foreach (var c in ChannelHealthProbe.Loud(CiAgreementProbe.Collect(plan)))
        {
            items.Add(new OwnerQueueItem(
                Id: c.Channel,
                Kind: "ci",
                Title: $"{c.Channel} is {c.Word} - {Clip(c.Detail, 200)}",
                Unblocks: "nothing in the run - the run carries on. what it costs is the meaning of "
                        + "every gate verdict it records, because the battery it passed is not the "
                        + "battery the branch is judged by",
                Command: c.FixCommand,
                SinceUtc: null,
                Rank: RankChannel,
                Detail: c.Fix.Length > 0 ? c.Fix : null));
        }
    }
}
