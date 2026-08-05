using Conductor.Core;
using Conductor.Models;
using CheckpointRow = Conductor.Core.CheckpointRow;

namespace Conductor.Tests;

/// <summary>
/// SF4.2 — a NEW owner-queue item pushes. The away-from-keyboard case is the whole point of the
/// stage: `.conductor/OWNER-QUEUE.md` and <c>GET /owner/queue</c> both require someone to be
/// LOOKING, and the situation this era was written for is the one where nobody is.
///
/// <para>Which makes the negative half load-bearing. The report write path runs many times per
/// session, so a notifier that fires on the whole queue instead of the new part of it would push the
/// same obligation on every write until the owner muted the bot — and a muted bot is worse than no
/// bot. The three cases below are new / already-announced / cleared, in that order.</para>
/// </summary>
public sealed class Sf42OwnerQueuePushTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-sf42push-{Guid.NewGuid():N}");
    private readonly PlanConfig _plan;
    private static readonly DateTime Now = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    public Sf42OwnerQueuePushTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".conductor"));
        _plan = new PlanConfig
        {
            Name = "sf42-push-test",
            Repo = _dir.Replace("\\", "/"),
            Tracker = "TRACKER.md",
            Agent = new AgentConfig { Command = "echo", Args = ["{prompt}"] },
            Stages = [new StageConfig { Id = "S1", Title = "Stage One", Sessions = 1 }],
        };
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_dir); } catch (IOException) { /* best effort */ }
    }

    private static TrackerSnapshot Track(string handoff = "", params CheckpointRow[] rows)
        => new() { HandoffBlock = handoff, Checkpoints = [.. rows] };

    /// <summary>Runs one report-boundary write and returns whatever the notifier was handed.</summary>
    private List<OwnerQueueItem> WriteAndCapture(RunState state, TrackerSnapshot track)
    {
        var captured = new List<OwnerQueueItem>();
        OwnerQueue.Write(_plan, state, track, _ => { }, Now, items => captured.AddRange(items));
        return captured;
    }

    // ---- new / unchanged / cleared ---------------------------------------------------------------

    [Fact]
    public void AnArrivingItem_IsAnnouncedOnce_ThenNeverAgain()
    {
        var state = new RunState { Status = RunStatus.Running, CurrentStage = "S1" };

        // 1. Nothing owed: nothing to say. A notifier that fires on an empty queue would page the
        //    owner to tell them they are free, which is noise with the same cost as an alert.
        Assert.Empty(WriteAndCapture(state, Track()));

        // 2. The agent escalates. This is the moment the owner is not watching.
        var handoff = "last: tried three approaches.\nHUMAN: pick the auth provider before I go further.";
        var first = WriteAndCapture(state, Track(handoff));
        var item = Assert.Single(first);
        Assert.Equal("human", item.Kind);
        Assert.Contains("auth provider", item.Title, StringComparison.OrdinalIgnoreCase);
        // The two fields that make an alert actionable rather than merely alarming.
        Assert.NotEmpty(item.Unblocks);

        // 3. The SAME obligation, on the next of many report writes in the same session. Silence.
        //    Without the diff this is where the bot starts repeating itself until it is muted.
        Assert.Empty(WriteAndCapture(state, Track(handoff)));
        Assert.Empty(WriteAndCapture(state, Track(handoff)));
    }

    [Fact]
    public void AClearedItem_DoesNotAnnounceAnything_AndLeavesTheFile()
    {
        var state = new RunState { Status = RunStatus.Running, CurrentStage = "S1" };
        var handoff = "HUMAN: approve the spend increase.";
        Assert.Single(WriteAndCapture(state, Track(handoff)));

        // The owner answers and the line goes. Clearing is good news the run makes on its own — it
        // must not arrive as a push, and it must not leave a ghost entry behind either.
        Assert.Empty(WriteAndCapture(state, Track("last: spend approved, carrying on.")));
        var file = File.ReadAllText(OwnerQueue.QueuePath(_plan));
        Assert.DoesNotContain("approve the spend increase", file, StringComparison.Ordinal);
        Assert.Contains("Nothing is waiting on you", file, StringComparison.Ordinal);
    }

    /// <summary>
    /// The trap this test exists for. Item ids are POSITIONAL — the first `HUMAN:` line is
    /// `human-1` whatever it says. Key on the id alone and this sequence is silent: the owner
    /// answers one question, the agent asks a completely different one in the same slot, and the new
    /// question — with a different answer, blocking different work — is never announced because
    /// `human-1` has "already been seen".
    /// </summary>
    [Fact]
    public void ADifferentQuestionInTheSameSlot_CountsAsNew()
    {
        var state = new RunState { Status = RunStatus.Running, CurrentStage = "S1" };
        Assert.Single(WriteAndCapture(state, Track("HUMAN: pick the auth provider.")));

        var second = WriteAndCapture(state, Track("HUMAN: the staging database is out of disk, please resize."));
        var item = Assert.Single(second);
        Assert.Contains("out of disk", item.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TwoItemsArrivingTogether_AreOneAnnouncement_NotTwo()
    {
        // Batching is a property of the callback contract, not of the caller: the notifier receives
        // the whole set so the owner gets one message listing both, rather than a burst.
        var state = new RunState { Status = RunStatus.Running, CurrentStage = "S1" };
        var calls = 0;
        var seen = new List<OwnerQueueItem>();
        OwnerQueue.Write(_plan, state,
            Track("HUMAN: pick the auth provider.\nHUMAN: approve the spend increase."),
            _ => { }, Now,
            items => { calls++; seen.AddRange(items); });

        Assert.Equal(1, calls);
        Assert.Equal(2, seen.Count);
    }

    // ---- the memory itself ------------------------------------------------------------------------

    /// <summary>
    /// The seen-set lives INSIDE the rendered file, not in a sidecar or in run state. That is a
    /// deliberate constraint — every entry in this queue is derived and nothing is stored, and a
    /// separate seen-set would be exactly the second source of truth that design avoids. So the
    /// failure mode has to be the safe one: lose the file, re-announce. Never go quiet.
    /// </summary>
    [Fact]
    public void DeletingTheQueueFile_ReAnnouncesInsteadOfGoingSilent()
    {
        var state = new RunState { Status = RunStatus.Running, CurrentStage = "S1" };
        var handoff = "HUMAN: pick the auth provider.";
        Assert.Single(WriteAndCapture(state, Track(handoff)));
        Assert.Empty(WriteAndCapture(state, Track(handoff)));

        File.Delete(OwnerQueue.QueuePath(_plan));

        Assert.Single(WriteAndCapture(state, Track(handoff)));
    }

    /// <summary>The marker is machine state in an owner-facing document, so it must be invisible:
    /// an HTML comment renders as nothing in every markdown viewer and in the Face.</summary>
    [Fact]
    public void TheSeenMarker_IsInvisibleInTheRenderedDocument()
    {
        var state = new RunState { Status = RunStatus.Running, CurrentStage = "S1" };
        WriteAndCapture(state, Track("HUMAN: pick the auth provider."));

        var file = File.ReadAllText(OwnerQueue.QueuePath(_plan));
        var marker = file.Split('\n').Single(l => l.Contains("conductor:owner-queue", StringComparison.Ordinal));
        Assert.StartsWith("<!--", marker.Trim(), StringComparison.Ordinal);
        Assert.EndsWith("-->", marker.Trim(), StringComparison.Ordinal);
    }

    /// <summary>The key must be stable across processes. <c>string.GetHashCode</c> is randomized per
    /// process in .NET, so keying on it would re-announce the entire queue every time the engine
    /// restarted — the exact noise this diff exists to prevent, arriving at the worst moment.</summary>
    [Fact]
    public void TheKey_IsStableAcrossProcesses()
    {
        // The expectation is NOT "whatever the code printed": it is FNV-1a-32 over
        // "human-1pick the auth provider", computed from the spec by an independent
        // implementation whose known-answer check (fnv1a("a") == e40c292c) matches the published
        // test vector. Pinning it here is what makes a future "harmless" tweak to the hash visible,
        // since the symptom otherwise is one silent re-announcement of every queue in existence.
        var item = new OwnerQueueItem("human-1", "human", "pick the auth provider", "the run", "", null, 1);
        Assert.Equal("73e5118a", OwnerQueue.Key(item));
    }

    [Fact]
    public void ReadKnownKeys_TreatsAnUnreadableOrLegacyFileAsNothingAnnounced()
    {
        // A queue file written before SF4.2 has no marker at all. It must read as "nothing has been
        // announced" so the first write after an upgrade re-announces, rather than as a parse error.
        Assert.Empty(OwnerQueue.ReadKnownKeys(null));
        Assert.Empty(OwnerQueue.ReadKnownKeys(""));
        Assert.Empty(OwnerQueue.ReadKnownKeys("# Owner queue\n\nno marker here\n"));
        Assert.Empty(OwnerQueue.ReadKnownKeys("<!-- conductor:owner-queue keys: aaaa never closed"));
    }

    // ---- the call sites ---------------------------------------------------------------------------

    /// <summary>
    /// Mechanical guard, in the shape SC1.1 established for the same class of bug. The notifier is
    /// opt-in per call site, and the report write path has four of them across three classes: a site
    /// that forgets the argument still writes a perfect OWNER-QUEUE.md and simply never tells anyone,
    /// which is invisible in review and invisible at runtime. So it is checked here instead of hoped
    /// for — every Reporter write in the engine passes a notifier.
    /// </summary>
    [Fact]
    public void EveryReporterWriteInTheEngine_PassesTheOwnerQueueNotifier()
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "Conductor"), "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            // Reporter.cs declares the methods and forwards the argument; it is not a call site.
            if (Path.GetFileName(file).Equals("Reporter.cs", StringComparison.Ordinal)) continue;

            foreach (var call in CallsTo(text, "Reporter.WriteAndPublish(").Concat(CallsTo(text, "Reporter.WriteReport(")))
            {
                if (!call.Contains("onNewOwnerItems", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}: {Collapse(call)}");
            }
        }

        Assert.True(offenders.Count == 0,
            "these report writes regenerate the owner queue but pass no notifier, so an item arriving "
            + "there reaches nobody: " + string.Join(" | ", offenders));
    }

    /// <summary>Each call to <paramref name="opening"/>, from the marker to its balanced close —
    /// the arguments span several lines at these sites, so a line-based scan would miss them.</summary>
    private static IEnumerable<string> CallsTo(string text, string opening)
    {
        var at = text.IndexOf(opening, StringComparison.Ordinal);
        while (at >= 0)
        {
            var depth = 0;
            var i = at + opening.Length - 1;
            for (; i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')' && --depth == 0) break;
            }
            yield return text[at..Math.Min(i + 1, text.Length)];
            at = text.IndexOf(opening, at + opening.Length, StringComparison.Ordinal);
        }
    }

    private static string Collapse(string s)
        => string.Join(" ", s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx"))) dir = dir.Parent;
        Assert.True(dir != null, "could not find the repo root from " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
