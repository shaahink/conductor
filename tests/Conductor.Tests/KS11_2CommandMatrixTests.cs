using System.Text.RegularExpressions;

using Conductor.Core;
using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Planning;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// KS11.2's headline exit — CHAPAR CH-3, measured rather than sampled.
///
/// <para>"The observer surface is closed" is the kind of claim that is easy to write and easy to be
/// wrong about: a sampled test covering <c>/pause</c> and <c>/inject</c> passes happily while
/// <c>/kill</c> or a callback button walks straight through. So the surface is a LIST
/// (<see cref="SurfaceCommands.All"/>), every test below walks the whole list against both profiles,
/// and <see cref="Every_command_literal_in_the_router_is_in_the_catalogue"/> scans the router's own
/// source so a verb added without a profile decision fails the build.</para>
///
/// <para>The router is a pure function of (text, profile, twoWay, armed) — no channel, no HTTP, no
/// store — which is what makes an exhaustive matrix cheap enough to be exhaustive.</para>
/// </summary>
public sealed class KS11_2CommandMatrixTests : IDisposable
{
    private readonly string _repo;
    private readonly CommandRouter _router;

    public KS11_2CommandMatrixTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-ks11m-{Guid.NewGuid():N}", "rig");
        Directory.CreateDirectory(_repo);
        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"),
            "# Rig\n\n## Checkpoints\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n"
            + "| Z1.1 | first | DONE | abc1234 | e.md |\n");

        var plan = new PlanConfig
        {
            Name = "Rig",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "Z1", Title = "The seam", Sessions = 1 } },
        };
        var state = new RunState { RunId = "rig-run", SessionCounter = 1, CurrentStage = "Z1" };
        var composer = new MessageComposer(plan, state, ProgressProviderFactory.Create(plan), null, _ => { });
        _router = new CommandRouter(composer, plan);
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(Directory.GetParent(_repo)!.FullName); } catch (Exception) { }
    }

    // ────────────────── the matrix: every verb x every profile ──────────────────

    /// <summary>CH-3's closed set, from the observer's side. Every browse verb answers or stays
    /// silent; every other verb is REFUSED, and the refusal names the verb.</summary>
    [Fact]
    public void Every_command_answered_for_an_observer_is_a_browse_command()
    {
        var wrong = new List<string>();
        foreach (var cmd in SurfaceCommands.All)
        {
            var outcome = _router.Route(cmd.Verb, ChatProfile.Observer, twoWay: true, injectionArmed: false);

            if (cmd.Scope != SurfaceScope.Browse)
            {
                if (outcome.Action != SurfaceAction.Refuse)
                    wrong.Add($"{cmd.Verb} ({cmd.Scope}) gave {outcome.Action}, expected Refuse");
                else if (!outcome.Text!.Contains(cmd.Verb, StringComparison.Ordinal))
                    wrong.Add($"{cmd.Verb} was refused without naming itself: {outcome.Text}");
                continue;
            }

            // A browse verb is never refused. An implemented one answers; one CH-3 names but no
            // checkpoint has built yet is silent, exactly as it is for an admin today. /start
            // answers with KS11.3's onboarding, which is composed asynchronously and so has an
            // action of its own — it is still an answer, and still never a refusal.
            var expected = !cmd.Implemented ? SurfaceAction.None
                : cmd.Verb == "/start" ? SurfaceAction.Onboard
                : SurfaceAction.Reply;
            if (outcome.Action != expected)
                wrong.Add($"{cmd.Verb} (browse, implemented={cmd.Implemented}) gave {outcome.Action}, expected {expected}");
        }

        Assert.True(wrong.Count == 0, "the observer surface is not what CH-3 says:\n  " + string.Join("\n  ", wrong));
    }

    /// <summary>The invariant that matters more than any single verb: no input from an observer
    /// chat, of any shape, ever produces an action that moves or steers the run. Walked over the
    /// whole catalogue with and without arguments, with two-way on and off, armed and not.</summary>
    [Fact]
    public void No_observer_input_of_any_shape_reaches_control_or_injection()
    {
        SurfaceAction[] forbidden =
            [SurfaceAction.Control, SurfaceAction.ConfirmControl, SurfaceAction.Inject, SurfaceAction.ArmInjection];

        var breaches = new List<string>();
        foreach (var cmd in SurfaceCommands.All)
        {
            foreach (var text in new[] { cmd.Verb, cmd.Verb + " now", cmd.Verb.ToUpperInvariant() })
            {
                foreach (var twoWay in new[] { true, false })
                {
                    foreach (var armed in new[] { true, false })
                    {
                        var outcome = _router.Route(text, ChatProfile.Observer, twoWay, armed);
                        if (forbidden.Contains(outcome.Action))
                            breaches.Add($"'{text}' twoWay={twoWay} armed={armed} -> {outcome.Action}");
                    }
                }
            }
        }

        // Plain prose while armed is the other way in: an observer that is somehow armed must not be
        // able to inject by typing a sentence.
        foreach (var armed in new[] { true, false })
        {
            var outcome = _router.Route("re-run the gate", ChatProfile.Observer, twoWay: true, injectionArmed: armed);
            if (forbidden.Contains(outcome.Action)) breaches.Add($"plain text armed={armed} -> {outcome.Action}");
        }

        Assert.True(breaches.Count == 0,
            "an observer reached the steering wheel:\n  " + string.Join("\n  ", breaches));
    }

    /// <summary>The other half of the matrix, and the back-compat half: an admin's answer to every
    /// verb is what it was before profiles existed. Nothing here is refused — Refuse is a shape
    /// admin chats can never see.</summary>
    [Fact]
    public void An_admin_is_refused_nothing_and_routes_exactly_as_before()
    {
        var wrong = new List<string>();
        foreach (var cmd in SurfaceCommands.All)
        {
            foreach (var twoWay in new[] { true, false })
            {
                var outcome = _router.Route(cmd.Verb, ChatProfile.Admin, twoWay, injectionArmed: false);
                if (outcome.Action == SurfaceAction.Refuse)
                    wrong.Add($"{cmd.Verb} twoWay={twoWay} refused an ADMIN: {outcome.Text}");

                var expected = (cmd.Scope, cmd.Implemented, twoWay) switch
                {
                    (SurfaceScope.Browse, false, _) => SurfaceAction.None,
                    (SurfaceScope.Browse, true, _) when cmd.Verb == "/start" => SurfaceAction.Onboard,
                    (SurfaceScope.Browse, true, _) => SurfaceAction.Reply,
                    (SurfaceScope.Steer, _, _) when cmd.Verb == "/chat" => SurfaceAction.Reply,
                    // DV3.4: bare "/project" is a QUESTION - which project do notes here go to -
                    // so unlike bare "/inject" it has an answer of its own.
                    (SurfaceScope.Steer, _, _) when cmd.Verb == "/project" => SurfaceAction.Project,
                    // DV5.1: bare "/cloud" is a question too - is this repo in a state a cloud
                    // session could clone - so it answers rather than falling through to silence.
                    (SurfaceScope.Steer, _, _) when cmd.Verb == "/cloud" => SurfaceAction.Cloud,
                    // Bare "/inject" is not the "/inject <text>" prefix, so it falls through to the
                    // control router, which does not know it: silence, two-way or not.
                    (SurfaceScope.Steer, _, _) => SurfaceAction.None,
                    (SurfaceScope.Control, _, false) => SurfaceAction.None,
                    (SurfaceScope.Control, _, true) when cmd.Verb is "/skip" or "/abort" or "/kill"
                        => SurfaceAction.ConfirmControl,
                    (SurfaceScope.Control, _, true) => SurfaceAction.Control,
                    _ => throw new InvalidOperationException($"catalogue grew a scope this matrix does not cover: {cmd.Scope}"),
                };
                if (outcome.Action != expected)
                    wrong.Add($"{cmd.Verb} twoWay={twoWay} gave {outcome.Action}, expected {expected}");
            }
        }

        Assert.True(wrong.Count == 0, "the admin surface moved:\n  " + string.Join("\n  ", wrong));
    }

    /// <summary>The hole KS11.2 found while reading: pushes fan out to every configured chat, so a
    /// confirmation keyboard raised by the owner lands in the observer's chat too — and
    /// <c>RouteCallback</c> took no profile at all, so pressing it wrote control.json.</summary>
    [Theory]
    [InlineData("abort:deadbeef:confirmed")]
    [InlineData("kill:deadbeef:confirmed")]
    [InlineData("skip:deadbeef:confirmed")]
    [InlineData("inject:1")]
    [InlineData("chat:1")]
    [InlineData("cancel:deadbeef")]
    public void No_button_press_from_an_observer_chat_does_anything(string data)
    {
        var outcome = _router.RouteCallback(data, ChatProfile.Observer);

        Assert.Equal(SurfaceAction.Refuse, outcome.Action);
        Assert.Null(outcome.ControlAction);

        // The admin press is untouched — this is a gate, not a rewrite of the callback surface.
        Assert.NotEqual(SurfaceAction.Refuse, _router.RouteCallback(data, ChatProfile.Admin).Action);
    }

    /// <summary>An unknown slash command stays silent for an observer, exactly as for an admin: a
    /// bot that answers every stray slash in a busy group is a bot that gets removed from it.</summary>
    [Theory]
    [InlineData("/deploy")]
    [InlineData("/status@someotherbot")]
    [InlineData("hello everyone")]
    public void An_unknown_command_from_an_observer_is_met_with_silence(string text)
    {
        Assert.Equal(SurfaceAction.None,
            _router.Route(text, ChatProfile.Observer, twoWay: true, injectionArmed: false).Action);
    }

    // ────────────────── end to end, through a channel ──────────────────

    /// <summary>The router is a decision; this is the whole surface acting on it. An observer chat
    /// types the most destructive verb there is, and what leaves the channel is one named refusal
    /// while the control file is never written — which is the fact that matters, since the control
    /// file is what the engine actually obeys.</summary>
    [Fact]
    public async Task An_observer_abort_leaves_one_refusal_on_the_wire_and_no_control_file()
    {
        var channel = new FakeChannel();
        var controlWrites = new List<string>();
        var surface = SurfaceOver(channel, (action, _, _) => { controlWrites.Add(action); return Task.CompletedTask; });

        await surface.HandleMessageAsync("-100123456", ChatProfile.Observer, "/abort", CancellationToken.None);
        await surface.HandleCallbackAsync("-100123456", ChatProfile.Observer, "abort:x:confirmed", CancellationToken.None);

        Assert.Empty(controlWrites);
        Assert.Equal(2, channel.Sent.Count);
        Assert.Contains("/abort is a control command", channel.Sent[0].Text, StringComparison.Ordinal);
        Assert.Contains("observer", channel.Sent[1].Text, StringComparison.Ordinal);
    }

    /// <summary>And the same chat asking a browse verb gets a real answer — a closed surface that
    /// answered nothing would pass every test above and be useless.</summary>
    [Fact]
    public async Task An_observer_asking_status_gets_the_run_status()
    {
        var channel = new FakeChannel();
        var surface = SurfaceOver(channel, (_, _, _) => Task.CompletedTask);

        await surface.HandleMessageAsync("-100123456", ChatProfile.Observer, "/status", CancellationToken.None);

        Assert.Contains("Rig", Assert.Single(channel.Sent).Text, StringComparison.Ordinal);
    }

    private RemoteSurface SurfaceOver(FakeChannel channel, Func<string, bool, string?, Task> writeControl)
    {
        var plan = new PlanConfig
        {
            Name = "Rig",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "Z1", Title = "The seam", Sessions = 1 } },
        };
        var state = new RunState { RunId = "rig-run", SessionCounter = 1, CurrentStage = "Z1" };
        var composer = new MessageComposer(plan, state, ProgressProviderFactory.Create(plan), null, _ => { });
        return new RemoteSurface(channel, composer, new CommandRouter(composer, plan), state, null,
            writeControl, (_, _) => { });
    }

    /// <summary>CH-1's fake — one observer chat, and a list of what left.</summary>
    private sealed class FakeChannel : IMessageChannel
    {
        public string Name => "fake";
        public bool IsLive => true;
        public bool AllowsControl => true;
        public IReadOnlyList<ChatTarget> Targets => [new ChatTarget("-100123456", ChatProfile.Observer)];
        public List<OutboundMessage> Sent { get; } = [];

        public Task EnqueueAsync(OutboundMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task SendAsync(OutboundMessage message, CancellationToken ct)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    // ────────────────── what makes "exhaustive" falsifiable ──────────────────

    /// <summary>The catalogue is only exhaustive if it covers the router. This scans
    /// <c>CommandRouter.cs</c> for every slash literal it compares against and fails if one is not
    /// in <see cref="SurfaceCommands.All"/> — so the next verb added to the router cannot default
    /// open by being forgotten here.</summary>
    [Fact]
    public void Every_command_literal_in_the_router_is_in_the_catalogue()
    {
        var code = Regex.Replace(RouterSource(), @"/\*.*?\*/|//[^\n]*", " ",
            RegexOptions.Singleline, TimeSpan.FromSeconds(5));

        var literals = Regex.Matches(code, "\"(?<verb>/[a-z]+)[ \"]",
                RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5))
            .Select(m => m.Groups["verb"].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(literals);
        var missing = literals.Where(v => SurfaceCommands.All.All(c => c.Verb != v)).ToList();
        Assert.True(missing.Count == 0,
            "the router knows a verb the catalogue does not, so nothing decided who may use it: "
            + string.Join(", ", missing));
    }

    /// <summary>And the ratchet the other way: a catalogue entry that claims to be implemented, and
    /// is not in the router, is a lie the matrix above would happily assert against.</summary>
    [Fact]
    public void Every_implemented_catalogue_entry_is_actually_in_the_router()
    {
        var source = RouterSource();

        var absent = SurfaceCommands.All
            .Where(c => c.Implemented && !source.Contains($"\"{c.Verb}", StringComparison.Ordinal))
            .Select(c => c.Verb)
            .ToList();

        Assert.True(absent.Count == 0,
            "catalogue entries claim a handler the router does not have: " + string.Join(", ", absent));
    }

    private static string RouterSource() => File.ReadAllText(Path.Combine(RepoRoot(),
        "src", "Conductor.Core", "Integrations", "Messaging", "CommandRouter.cs"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
