using System.Net;
using System.Net.Sockets;

using Conductor.Core.Courier;
using Conductor.Core.Inbox;
using Conductor.Core.Integrations;
using Conductor.Courier;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit.Abstractions;

namespace Conductor.Tests;

/// <summary>PK3.3 / D4 - a live run names its own project to the courier, and a note about a plan the
/// owner never ran <c>courier allow</c> for is filed.
///
/// <para>Security note, restated from D4 because it is the reason this is safe: the allowlist stops a
/// daemon holding the bot token writing into arbitrary checkouts; a run already has write access to its
/// own checkout and the hello carries the install secret, so the entry adds no reach.</para></summary>
[Trait("Category", "Integration")]
public sealed class PK3_3CourierHelloTests : IDisposable
{
    private const string AdminChat = "99205495";
    private const string ScratchToken = "111111:pk33-scratch-token";

    private readonly string _tmp;
    private readonly string _stateHome;
    private readonly string _repo;
    private readonly ITestOutputHelper _out;
    private readonly List<IDisposable> _junk = [];

    public PK3_3CourierHelloTests(ITestOutputHelper output)
    {
        _out = output;
        _tmp = Path.Combine(Path.GetTempPath(), $"conductor-pk33-{Guid.NewGuid():N}");
        _stateHome = Path.Combine(_tmp, "state-home");
        _repo = Path.Combine(_tmp, "shared-repo");
        Directory.CreateDirectory(_stateHome);
        Directory.CreateDirectory(Path.Combine(_repo, ".conductor"));
    }

    public void Dispose()
    {
        foreach (var d in Enumerable.Reverse(_junk)) { try { d.Dispose(); } catch (Exception) { } }
        try { TestTemp.DeleteTree(_tmp); } catch (Exception) { }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>The owner allowed the repository once, under the plan that ran there then.</summary>
    private CourierSettings Allowed(RecordingBotApi bot)
    {
        var settings = new CourierSettings
        {
            ApiBaseUrl = bot.Root,
            PollIntervalSeconds = 1,
            Chats = [new CourierChat(AdminChat, "admin")],
            Projects = [new CourierProject("OldPlan", _repo)],
        };
        settings.Save(_stateHome);
        return settings;
    }

    /// <summary>A live scratch courier sharing <paramref name="settings"/> with <paramref name="source"/>'s
    /// daemon, as CourierProgram wires them.</summary>
    private void Listen(CourierSettings settings, ICourierSource source)
    {
        var port = FreePort();
        var listener = new CourierListener(() => CourierPresence.Current("Conductor Courier (pk33 scratch)", port),
            new CourierDesk(source, settings, _stateHome, _out.WriteLine), CourierSecret.Resolve(_stateHome), NullLogger.Instance, port);
        Assert.True(listener.TryStart(out var refusal), refusal);
        _junk.Add(listener);
        CourierPresence.Current("Conductor Courier (pk33 scratch)", port).Write(_stateHome);
    }

    /// <summary>A reply to a push this plan sent: the identity line is what routes it (DV3.4).</summary>
    private static void ReplyTo(RecordingBotApi bot, string plan, int messageId, string text) =>
        bot.QueueMessage("{\"message_id\":" + messageId + ",\"chat\":{\"id\":" + AdminChat + "},\"text\":\"" + text + "\","
            + "\"reply_to_message\":{\"message_id\":7,\"chat\":{\"id\":" + AdminChat + "},\"text\":\"" + plan + " · s1\\nsession 1 ended\"}}");

    private CourierClient Client()
    {
        var client = CourierClient.TryOpen(_stateHome, out var refusal);
        Assert.True(client is not null, refusal);
        _junk.Add(client);
        return client;
    }

    // -- the acceptance: a fresh plan name in an allowed repo files a note without courier allow --------

    [Fact]
    public async Task A_fresh_plan_in_an_allowed_repo_is_parked_until_the_run_says_hello_and_filed_after()
    {
        using var bot = new RecordingBotApi { HonourOffset = true };
        var settings = Allowed(bot);
        using var source = new TelegramCourierSource(settings, ScratchToken, NullLogger.Instance, _stateHome);
        var daemon = new CourierDaemon(source, settings, _stateHome, m => _out.WriteLine(m));
        Listen(settings, source);

        // Before: the repository is allowed, but under its OLD plan name. F-COUR-4, measured.
        ReplyTo(bot, "FreshPlan", 11, "a note before the hello");
        var before = await daemon.PollOnceAsync(CancellationToken.None);
        Assert.Equal((1, 0), (before.Parked, before.Filed));

        // The run's first session boundary: CourierIntroduction, exactly as RunContext calls it.
        var lines = new List<string>();
        var ack = await new CourierIntroduction(_stateHome).AtBoundaryAsync("FreshPlan", _repo, "run-pk33", lines.Add);
        foreach (var l in lines) _out.WriteLine("run log: " + l);
        Assert.True(ack is { Accepted: true }, ack?.Detail);
        Assert.Contains("added by run run-pk33", Assert.Single(lines), StringComparison.Ordinal);

        // After: the same kind of note is filed into the repository's inbox - no courier allow, no restart.
        ReplyTo(bot, "FreshPlan", 12, "a note after the hello");
        var after = await daemon.PollOnceAsync(CancellationToken.None);
        Assert.Equal((0, 1), (after.Parked, after.Filed));
        var note = Assert.Single(new InboxStore(Path.Combine(_repo, ".conductor")).All());
        Assert.Contains("a note after the hello", note.Text, StringComparison.Ordinal);
    }

    // -- the entry ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_entry_sits_beside_the_owners_marked_by_the_run_and_a_second_hello_adds_nothing()
    {
        using var bot = new RecordingBotApi();
        var settings = Allowed(bot);
        using var source = new TelegramCourierSource(settings, ScratchToken, NullLogger.Instance, _stateHome);
        Listen(settings, source);
        var client = Client();

        var first = await client.IntroduceAsync(new CourierHello(_repo, "FreshPlan", "run-a"));
        var second = await client.IntroduceAsync(new CourierHello(_repo + Path.DirectorySeparatorChar, "freshplan", "run-b"));
        _out.WriteLine(first.Detail + "\n" + second.Detail);

        Assert.True(first.Accepted, first.Detail);
        Assert.Contains("from now on - added by run run-a", first.Detail, StringComparison.Ordinal);
        Assert.True(second.Accepted, second.Detail);
        Assert.Contains("already files notes", second.Detail, StringComparison.Ordinal);

        var disk = CourierSettings.Load(_stateHome).Projects;
        Assert.Equal([new CourierProject("OldPlan", _repo), new CourierProject("FreshPlan", _repo, "run run-a")], disk);
        Assert.Equal(disk, settings.Projects);                         // the running courier reads the same list
        Assert.Equal(["FreshPlan", "OldPlan"], settings.Allowed().Select(p => p.Plan).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task What_the_owner_allowed_since_the_courier_started_survives_a_hello()
    {
        using var bot = new RecordingBotApi();
        var settings = Allowed(bot);                                  // the courier's startup copy
        using var source = new TelegramCourierSource(settings, ScratchToken, NullLogger.Instance, _stateHome);
        Listen(settings, source);
        var other = Path.Combine(_tmp, "other-repo");
        Directory.CreateDirectory(other);

        // `courier allow` while the courier runs edits the FILE, not the daemon's copy.
        var edited = CourierSettings.Load(_stateHome);
        edited.Projects.Add(new CourierProject("OwnerAllowed", other));
        edited.Save(_stateHome);

        var ack = await Client().IntroduceAsync(new CourierHello(_repo, "FreshPlan", "run-c"));

        Assert.True(ack.Accepted, ack.Detail);
        Assert.Equal(["OldPlan", "OwnerAllowed", "FreshPlan"], CourierSettings.Load(_stateHome).Projects.Select(p => p.Plan));
        Assert.Equal(["OldPlan", "OwnerAllowed", "FreshPlan"], settings.Projects.Select(p => p.Plan));
    }

    [Fact]
    public async Task A_hello_naming_a_path_that_is_not_there_is_refused_by_name_and_changes_nothing()
    {
        using var bot = new RecordingBotApi();
        var settings = Allowed(bot);
        using var source = new TelegramCourierSource(settings, ScratchToken, NullLogger.Instance, _stateHome);
        Listen(settings, source);
        var before = await File.ReadAllTextAsync(CourierHome.SettingsPathFor(_stateHome));

        var missing = await Client().IntroduceAsync(new CourierHello(Path.Combine(_tmp, "not-there"), "Ghost", "run-d"));
        var nameless = await Client().IntroduceAsync(new CourierHello(_repo, "  ", "run-d"));

        Assert.False(missing.Accepted);
        Assert.Contains("is not a directory on this machine", missing.Detail, StringComparison.Ordinal);
        Assert.False(nameless.Accepted);
        Assert.Contains("has to name the run's plan", nameless.Detail, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(CourierHome.SettingsPathFor(_stateHome)));
    }

    // -- the run's side ----------------------------------------------------------------------------

    [Fact]
    public async Task A_run_asks_once_it_is_answered_and_again_while_nobody_answers()
    {
        using var bot = new RecordingBotApi();
        var lines = new List<string>();
        var intro = new CourierIntroduction(_stateHome);

        // No courier configured on this machine: nothing is asked.
        Assert.Null(await intro.AtBoundaryAsync("FreshPlan", _repo, "run-e", lines.Add));

        // Configured but not running: not answered, so the next boundary asks again.
        var settings = Allowed(bot);
        Assert.Null(await intro.AtBoundaryAsync("FreshPlan", _repo, "run-e", lines.Add));
        Assert.False(intro.Answered);

        // Running: answered once, logged once, never asked again by this run.
        using var source = new TelegramCourierSource(settings, ScratchToken, NullLogger.Instance, _stateHome);
        Listen(settings, source);
        Assert.True((await intro.AtBoundaryAsync("FreshPlan", _repo, "run-e", lines.Add))?.Accepted);
        Assert.Null(await intro.AtBoundaryAsync("FreshPlan", _repo, "run-e", lines.Add));
        Assert.True(intro.Answered);
        Assert.Equal("courier: the courier files notes for FreshPlan at " + _repo + " from now on - added by run run-e", Assert.Single(lines));
    }
}
