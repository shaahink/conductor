using Conductor.Core;
using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Messaging;
using Conductor.Models;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// K5.4, templates — the shape of a push belongs to the owner.
///
/// <para>Every message this engine sends was a <c>StringBuilder</c> inside a method. To reorder a
/// line, drop the gates line, or put the cost first, an owner had to edit C# and rebuild the engine
/// that is driving their run. <c>plan.templatesDir</c> has existed for the agent prompts all along,
/// so the notification templates live in the same place under a <c>notify/</c> subdirectory — which
/// the prompt loader cannot see, because it resolves prompts by NAME.</para>
///
/// <para>The failure mode being designed against is specific and this repo has paid for it: an
/// unresolved placeholder in a PROMPT template is fatal by design, the refusal goes to stderr only,
/// and it fires when a stage first renders that template. A notification is the run's only voice, so
/// the same mistake here must degrade to the built-in and SAY so — never throw, and never ship a
/// message with a literal brace token in it.</para>
/// </summary>
public sealed class K5_4TemplateTests : IDisposable
{
    private const string ChatId = "939393";

    private readonly string _repo;
    private readonly string _templates;

    public K5_4TemplateTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-k54t-{Guid.NewGuid():N}");
        _templates = Path.Combine(_repo, "templates", "notify");
        Directory.CreateDirectory(_templates);
        Directory.CreateDirectory(Path.Combine(_repo, ".conductor"));

        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"),
            "# T\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
            "| K9.1 | first | DONE | abc1234 | e.md |\n| K9.2 | second | TODO | | |\n");

        SecretsStore.WriteTelegramToken(Path.Combine(_repo, ".conductor"), "k54t-test-token");
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_repo); } catch (Exception) { }
    }

    private PlanConfig Plan(string apiRoot) => new()
    {
        Name = "K54T",
        Repo = _repo,
        Tracker = "TRACKER.md",
        PlanFilePath = Path.Combine(_repo, "t.plan.json"),
        TemplatesDir = "templates",
        Stages = { new StageConfig { Id = "K9", Title = "Channels", Sessions = 1 } },
        Telegram = new TelegramConfig
        {
            AllowedChatIds = { ChatId }, PollIntervalSeconds = 60, ApiBaseUrl = apiRoot,
        },
    };

    private async Task<string> PushAsync(RecordingBotApi bot)
    {
        using var svc = new TelegramService(Plan(bot.Root), new RunState(), NullLogger<TelegramService>.Instance);
        await ((IHostedService)svc).StartAsync(CancellationToken.None);
        await svc.PushSessionEndAsync(new SessionEndPush(
            3, "K9", "Advanced", "engine-fast:OK", null, 0.5m, null, 0, [], false));
        await ((IHostedService)svc).StopAsync(CancellationToken.None);
        return Assert.Single(bot.Snapshot()).Text!;
    }

    [Fact]
    public async Task An_owner_template_replaces_the_built_in_composition()
    {
        using var bot = new RecordingBotApi();
        await File.WriteAllTextAsync(Path.Combine(_templates, "session-end.md"),
            "{cost}\nthe session said: {outcome}\n{gates}\n");

        var text = await PushAsync(bot);

        // The owner's ordering, not the engine's: cost first, gates last, no "gates:" label at all.
        // Past BOTH stamped lines — identity and context — which no template can drop.
        var body = text[(text.LastIndexOf("</i>\n", StringComparison.Ordinal) + 5)..];
        Assert.StartsWith("cost: $0.50", body, StringComparison.Ordinal);
        Assert.Contains("the session said: Advanced", body, StringComparison.Ordinal);
        Assert.DoesNotContain("gates:", body, StringComparison.Ordinal);
        Assert.EndsWith("engine-fast:OK", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_template_naming_a_fact_the_event_does_not_have_is_refused_not_shipped()
    {
        using var bot = new RecordingBotApi();
        await File.WriteAllTextAsync(Path.Combine(_templates, "session-end.md"),
            "{outcome} spent {sandwiches}\n");

        var text = await PushAsync(bot);

        // Not thrown — the notification path is the run's only voice. And not shipped either: a
        // literal "{sandwiches}" reaching the owner's chat is the failure this check exists for.
        Assert.DoesNotContain("{sandwiches}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("sandwiches", text, StringComparison.Ordinal);
        Assert.Contains("proof: gates engine-fast:OK", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_that_comes_out_blank_is_dropped_and_a_labelled_one_is_not()
    {
        using var bot = new RecordingBotApi();

        // No commits, no claims, no result, no remote: landed, result and report are all empty.
        var text = await PushAsync(bot);

        Assert.DoesNotContain("landed:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("result:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", text, StringComparison.Ordinal);
        // A fact sharing its line with a label keeps the label — that is the whole distinction.
        Assert.Contains("proof: gates engine-fast:OK", text, StringComparison.Ordinal);
    }

    // ── the template language, without a socket ──

    [Fact]
    public async Task A_doubled_brace_is_a_literal_brace_and_a_value_that_looks_like_one_is_left_alone()
    {
        var facts = new Dictionary<string, string>(StringComparer.Ordinal) { ["outcome"] = "{gates} in the value" };

        var rendered = await NotifyTemplate.RenderAsync("x", "{{outcome}} = {outcome}", facts, null, null);

        // The escape restores to a literal brace; the VALUE's braces are held and restored verbatim
        // rather than being substituted a second time.
        Assert.Equal("{outcome} = {gates} in the value", rendered);
    }

    [Fact]
    public async Task With_no_templates_dir_the_built_in_is_used_and_nothing_is_read_from_disk()
    {
        var rendered = await NotifyTemplate.RenderAsync("session-end", "built-in {outcome}",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["outcome"] = "ok" }, null, null);

        Assert.Equal("built-in ok", rendered);
        Assert.Null(NotifyTemplate.OverridePath(_repo, null, "session-end"));
    }

    /// <summary>The prompt loader resolves templates by NAME — <c>session.md</c>, <c>fix.md</c>,
    /// <c>packs/&lt;name&gt;.md</c> — so a notification template must not be able to land on one of
    /// those paths, where an unresolved placeholder would be fatal instead of merely refused.</summary>
    [Fact]
    public void An_override_lives_under_notify_where_the_prompt_loader_cannot_see_it()
    {
        var path = NotifyTemplate.OverridePath("/plans/karvan", "templates", "session-end");

        Assert.NotNull(path);
        Assert.Contains(Path.Combine("templates", "notify"), path!, StringComparison.Ordinal);
        Assert.EndsWith("session-end.md", path!, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.Combine("templates", "session.md"), path!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreadable_override_falls_back_rather_than_throwing()
    {
        // A directory where the loader expects a file: File.Exists is false, so this is the ordinary
        // "no override" path — the point is that it is not an exception.
        Directory.CreateDirectory(Path.Combine(_templates, "evidence.md"));

        var rendered = await NotifyTemplate.RenderAsync("evidence", "built-in {artifact}",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["artifact"] = "shot.png" },
            _repo, "templates");

        Assert.Equal("built-in shot.png", rendered);
    }
}
