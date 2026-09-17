using System.ComponentModel;
using System.Globalization;

using Conductor.Core;
using Conductor.Core.Courier;
using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Store;
using Conductor.Http;
using Conductor.Models;

using Microsoft.Extensions.Logging;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// DV4.1 / findings §1.4-B — <c>conductor courier</c>: one bot, always awake, outliving the run.
///
/// <para>Every other verb in this CLI is about a project. This one is about the MACHINE: it owns
/// <c>CONDUCTOR_TELEGRAM_TOKEN</c>, polls whether or not anything is running, and files each note
/// into whichever project it is about. That is the whole answer to findings §1.2 — feedback should
/// be possible when you have it, not when a run happens to be up.</para>
///
/// <para><b>The allowlist is explicit and it is not the state catalogue.</b> A run may file against
/// itself; a daemon holding the bot token could write into every checkout this machine remembers, so
/// it files only against projects written down with <c>courier allow</c>. Anything else is parked in
/// the dead-letter box with the reason, never guessed at.</para>
///
/// <para><b>The 24-hour limit (§6.3), which belongs in front of a person's eyes and not only in a
/// design document.</b> Telegram keeps an undelivered update for 24 hours. A voice note sent on
/// Friday night to a laptop that sleeps until Monday is gone — not dropped by conductor, never handed
/// over by Telegram. The courier narrows the gap from "no run live" to "machine on"; it cannot do
/// better from this machine, and <c>courier status</c> says so out loud.</para>
/// </summary>
public sealed partial class CourierCommand : AsyncCommand<CourierCommand.Settings>
{
    /// <summary>The line <c>conductor --help</c> shows. It lives here rather than inline in
    /// <c>Program.cs</c> for a measured reason: that file sits AT CA1505's maintainability floor
    /// (KS8.2 recorded it at MI 19, and the bar is 20), so a long string literal added to it fails
    /// the build. A named constant referenced from there costs almost nothing.</summary>
    public const string VerbDescription =
        "DV4.1: the courier - one bot, always awake, outliving the run. Owns "
      + "CONDUCTOR_TELEGRAM_TOKEN, polls whether or not a run is live, and files each note into a "
      + "project on its EXPLICIT allowlist. `courier status|run|install|uninstall|restart|stop|allow "
      + "--repo PATH|deny --repo PATH|chat --id ID|unchat --id ID`.";

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[VERB]")]
        [Description("status (default), run, install, uninstall, restart, stop, allow, deny, chat, unchat.")]
        public string Verb { get; init; } = "status";

        [CommandOption("--repo <PATH>")]
        [Description("allow/deny: the checkout the courier may file notes into.")]
        public string? Repo { get; init; }

        [CommandOption("--plan <NAME>")]
        [Description("allow: the plan name a push's identity line carries, when it is not the folder name.")]
        public string? Plan { get; init; }

        [CommandOption("--id <CHAT_ID>")]
        [Description("chat/unchat: the chat id the courier answers. Group ids are negative.")]
        public string? ChatId { get; init; }

        [CommandOption("--profile <NAME>")]
        [Description("chat: admin (may file) or observer (read-only). Defaults to admin.")]
        public string? Profile { get; init; }

        [CommandOption("--task-name <NAME>")]
        [Description("install/uninstall/restart/stop/status: the scheduled task. Defaults to \"Conductor Courier\".")]
        public string? TaskName { get; init; }

        [CommandOption("--exe <PATH>")]
        [Description("install: the engine binary the task runs. Defaults to this one.")]
        public string? Exe { get; init; }

        [CommandOption("--no-start")]
        [Description("install: register the task without starting it now.")]
        public bool NoStart { get; init; }

        [CommandOption("--once")]
        [Description("run: poll once and exit, instead of polling until stopped.")]
        public bool Once { get; init; }

        [CommandOption("--json")]
        [Description("Machine-readable output.")]
        public bool Json { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Verb.Trim().ToLowerInvariant() switch
        {
            "" or "status" => await StatusAsync(settings).ConfigureAwait(false),
            "run" => await RunAsync(settings).ConfigureAwait(false),
            "install" => await InstallAsync(settings).ConfigureAwait(false),
            "uninstall" => await UninstallAsync(settings).ConfigureAwait(false),
            "restart" => await RestartAsync(settings).ConfigureAwait(false),
            "stop" => await StopAsync(settings).ConfigureAwait(false),
            "allow" => Allow(settings),
            "deny" => Deny(settings),
            "chat" => Chat(settings),
            "unchat" => Unchat(settings),
            var other => Unknown(other),
        };
    }

    private static int Unknown(string verb)
    {
        AnsiConsole.MarkupLine($"[red]error:[/] `conductor courier {Markup.Escape(verb)}` is not a thing. "
            + "Try [yellow]status[/], [yellow]run[/], [yellow]install[/], [yellow]uninstall[/], "
            + "[yellow]restart[/], [yellow]stop[/], [yellow]allow[/], [yellow]deny[/], "
            + "[yellow]chat[/] or [yellow]unchat[/].");
        return 1;
    }

    // ── status ──────────────────────────────────────────────────────────────────────────────

    private static async Task<int> StatusAsync(Settings settings)
    {
        var courier = CourierSettings.Load();
        var offset = new CourierOffset();
        var token = Token();
        var task = new CourierTask(settings.TaskName);
        var state = await task.StateAsync().ConfigureAwait(false);
        var stale = CourierProtocol.RefuseStale(state.Running);

        if (settings.Json)
        {
            AnsiConsole.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                dir = CourierHome.DirFor(),
                hasToken = token is { Length: > 0 },
                offset = offset.Read(),
                projects = courier.Allowed().Select(p => new { p.Plan, p.Repo, p.Slug, p.Present }),
                chats = courier.Chats,
                refusal = Blocker(courier, token),
                retentionHours = 24,
                protocol = CourierProtocol.Version,
                task = new { state.Name, state.Registered, state.SchedulerState },
                running = state.Running,
                stale,
                port = state.Running?.Port,
                unreachable = CourierEndpoint.Unreachable(state.Running),
                secret = new
                {
                    path = CourierHome.SecretPathFor(),
                    present = CourierSecret.Read() is { Length: > 0 },
                    exposed = CourierSecret.ProtectionComplaint(),
                },
                // Bug #93: the record of what the courier did, so a dead one can be read.
                log = new
                {
                    path = CourierHome.LogPathFor(),
                    lastWriteUtc = CourierLog.At().LastWriteUtc(),
                    tail = CourierLog.At().Tail(),
                },
            }, PlanConfig.JsonOpts));
            return 0;
        }

        AnsiConsole.MarkupLine("[bold]courier[/] [dim]" + Markup.Escape(CourierHome.DirFor()) + "[/]");
        AnsiConsole.MarkupLine("[dim]token:[/] " + (token is { Length: > 0 }
            ? "[green]set[/] [dim](" + TelegramCourierSource.TokenEnvVar + ")[/]"
            : "[red]missing[/] [dim]— set " + TelegramCourierSource.TokenEnvVar + "[/]"));
        AnsiConsole.MarkupLine("[dim]poll:[/] " + Markup.Escape(offset.Describe())
            + " [dim]· every " + courier.PollIntervalSeconds.ToString(CultureInfo.InvariantCulture) + "s[/]");

        var allowed = courier.Allowed();
        AnsiConsole.MarkupLine("[dim]projects:[/] " + (allowed.Count == 0
            ? "[yellow]none[/] [dim]— `conductor courier allow --repo <path>`[/]"
            : string.Join(", ", allowed.Select(p =>
                Markup.Escape(p.Name) + (p.Present ? "" : " [red](repo missing)[/]")))));
        AnsiConsole.MarkupLine("[dim]chats:[/] " + (courier.Chats.Count == 0
            ? "[yellow]none[/] [dim]— `conductor courier chat --id <chat-id>`[/]"
            : string.Join(", ", courier.Chats.Select(c =>
                Markup.Escape(c.ChatId) + " [dim](" + Markup.Escape(c.Profile ?? ChatProfiles.AdminName) + ")[/]"))));

        AnsiConsole.MarkupLine("[dim]task:[/] " + (state.Registered
            ? "[green]" + Markup.Escape(state.Name) + "[/]"
              + (state.SchedulerState is { Length: > 0 } sched ? " [dim](" + Markup.Escape(sched) + ")[/]" : "")
            : "[yellow]not installed[/] [dim]— `conductor courier install` registers it at your logon[/]"));
        AnsiConsole.MarkupLine("[dim]running:[/] " + (state.Running is { } live
            ? "[green]yes[/] [dim]" + Markup.Escape(live.Describe()) + "[/]"
            : "[yellow]no[/] [dim]— nothing is polling for this machine[/]")
            + " [dim]· this build speaks protocol "
            + CourierProtocol.Version.ToString(CultureInfo.InvariantCulture) + "[/]");

        // §6.5: the seam a run pushes through, and the file that is its whole access control. A
        // secret nothing has locked down is a secret every process running as this user already has,
        // so it is said out loud here rather than assumed by the code that wrote it.
        AnsiConsole.MarkupLine("[dim]loopback:[/] " + (state.Running?.Port is > 0
            ? "[green]" + Markup.Escape(CourierEndpoint.BaseUrl(state.Running.Port!.Value)) + "[/]"
            : "[yellow]none[/] [dim]- runs on this machine cannot push through it[/]")
            + " [dim]· secret " + (CourierSecret.Read() is { Length: > 0 } ? "present" : "absent") + "[/]");
        if (CourierSecret.ProtectionComplaint() is { Length: > 0 } exposed)
            AnsiConsole.MarkupLine("[red]secret exposed:[/] " + Markup.Escape(exposed));

        // §6.4: the one process designed to outlive a reinstall is the one that keeps running the
        // engine it started with. Say so by name, with the command, before anything talks to it.
        if (stale is { Length: > 0 } skew)
            AnsiConsole.MarkupLine("[red]stale courier:[/] " + Markup.Escape(skew));

        // Bug #93: the last lines the courier wrote about itself — the first thing to read when
        // `running: no` on a machine whose task says it started. "No log" is a finding, not a blank.
        var log = CourierLog.At();
        var tail = log.Tail();
        AnsiConsole.MarkupLine("[dim]log:[/] " + Markup.Escape(log.Path) + (log.LastWriteUtc() is { } wrote
            ? " [dim](last write " + wrote.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "Z)[/]"
            : " [yellow](no log yet - a courier from before bug #93, or one that never started)[/]"));
        foreach (var line in tail)
            AnsiConsole.MarkupLine("  [dim]" + Markup.Escape(line) + "[/]");

        AnsiConsole.MarkupLine(VerdictLine(Blocker(courier, token), state.Running));

        AnsiConsole.MarkupLine("[dim]" + Markup.Escape(RetentionNotice) + "[/]");
        return 0;
    }

    /// <summary>Bug #87: the last line of <c>courier status</c> was decided from the blocker alone, so
    /// a status that had just printed <c>running: yes pid N</c> ended with "`conductor courier run`
    /// starts polling" — naming the command that would start a SECOND instance. The presence the same
    /// verb read a moment earlier is the branch it was missing, and it is consulted first: a live
    /// instance is polling whatever this shell's environment says, and a blocker seen from here is
    /// then a note about starting another, not a verdict on the one that is up.</summary>
    internal static string VerdictLine(string? blocker, CourierPresence? running)
    {
        if (running is { } live)
            return "[green]polling[/] [dim]— pid " + live.Pid.ToString(CultureInfo.InvariantCulture)
                 + " is already polling for this machine; `conductor courier restart` replaces it, "
                 + "`conductor courier stop` ends it.[/]"
                 + (blocker is { Length: > 0 } ? " [yellow](this shell could not start another: " + Markup.Escape(blocker) + ")[/]" : "");
        if (blocker is { Length: > 0 })
            return "[yellow]not ready:[/] " + Markup.Escape(blocker);
        return "[green]ready[/] [dim]— `conductor courier run` starts polling.[/]";
    }

    /// <summary>Findings §6.3 - the wording is core's, shared with the courier binary that prints it
    /// at startup.</summary>
    internal const string RetentionNotice = CourierDaemon.RetentionNotice;

    private static string? Blocker(CourierSettings courier, string? token) => courier.StartBlocker(token);

    private static string? Token() => TelegramCourierSource.TokenFromEnvironment();
    // ── the allowlist ───────────────────────────────────────────────────────────────────────

    private static int Allow(Settings settings)
    {
        if (settings.Repo is not { Length: > 0 } repo)
        {
            AnsiConsole.MarkupLine("[red]error:[/] `conductor courier allow` needs [yellow]--repo <PATH>[/].");
            return 1;
        }

        var full = Path.GetFullPath(repo);
        if (!Directory.Exists(full))
        {
            // Refused rather than accepted-and-warned: an allowlist entry for a path that is not
            // there is a note parked every time instead of filed, discovered a week later.
            AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(full)} is not a directory on this machine.");
            return 1;
        }

        var courier = CourierSettings.Load();
        var plan = settings.Plan?.Trim() ?? PlanNameIn(full) ?? "";
        courier.Projects.RemoveAll(p => SameRepo(p.Repo, full));
        courier.Projects.Add(new CourierProject(plan, full));
        courier.Save();

        AnsiConsole.MarkupLine("[green]allowed[/] " + Markup.Escape(plan.Length > 0 ? plan : Path.GetFileName(full))
            + " [dim]" + Markup.Escape(full) + "[/]");
        AnsiConsole.MarkupLine("[dim]slug " + Markup.Escape(StateHome.SlugFor(full, plan)) + " · "
            + Markup.Escape(CourierHome.SettingsPathFor()) + "[/]");
        return 0;
    }

    private static int Deny(Settings settings)
    {
        if (settings.Repo is not { Length: > 0 } repo)
        {
            AnsiConsole.MarkupLine("[red]error:[/] `conductor courier deny` needs [yellow]--repo <PATH>[/].");
            return 1;
        }

        var full = Path.GetFullPath(repo);
        var courier = CourierSettings.Load();
        var removed = courier.Projects.RemoveAll(p => SameRepo(p.Repo, full));
        courier.Save();

        AnsiConsole.MarkupLine(removed > 0
            ? "[green]denied[/] " + Markup.Escape(full) + " [dim]— notes for it are parked from now on.[/]"
            : "[yellow]nothing to do[/] [dim]— " + Markup.Escape(full) + " was not on the allowlist.[/]");
        return 0;
    }

    private static int Chat(Settings settings)
    {
        if (settings.ChatId is not { Length: > 0 } chatId)
        {
            AnsiConsole.MarkupLine("[red]error:[/] `conductor courier chat` needs [yellow]--id <CHAT_ID>[/].");
            return 1;
        }

        var profile = settings.Profile?.Trim();
        if (profile is { Length: > 0 } && ChatProfiles.TryParse(profile) is null)
        {
            AnsiConsole.MarkupLine($"[red]error:[/] profile [yellow]{Markup.Escape(profile)}[/] is not one of: "
                + string.Join(", ", ChatProfiles.Names) + ".");
            return 1;
        }

        var courier = CourierSettings.Load();
        courier.Chats.RemoveAll(c => string.Equals(c.ChatId, chatId, StringComparison.Ordinal));
        courier.Chats.Add(new CourierChat(chatId, profile is { Length: > 0 } ? profile : ChatProfiles.AdminName));
        courier.Save();

        AnsiConsole.MarkupLine("[green]listening to[/] " + Markup.Escape(chatId)
            + " [dim](" + Markup.Escape(profile ?? ChatProfiles.AdminName) + ")[/]");
        return 0;
    }

    private static int Unchat(Settings settings)
    {
        if (settings.ChatId is not { Length: > 0 } chatId)
        {
            AnsiConsole.MarkupLine("[red]error:[/] `conductor courier unchat` needs [yellow]--id <CHAT_ID>[/].");
            return 1;
        }

        var courier = CourierSettings.Load();
        var removed = courier.Chats.RemoveAll(c => string.Equals(c.ChatId, chatId, StringComparison.Ordinal));
        courier.Save();

        AnsiConsole.MarkupLine(removed > 0
            ? "[green]stopped listening to[/] " + Markup.Escape(chatId)
            : "[yellow]nothing to do[/] [dim]— " + Markup.Escape(chatId) + " was not listed.[/]");
        return 0;
    }

    private static bool SameRepo(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                      Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                      StringComparison.OrdinalIgnoreCase);

    /// <summary>The plan name out of a repo's plan file, so `courier allow --repo .` usually needs no
    /// <c>--plan</c>. Best effort by design: the name is what a push's identity line says, and if the
    /// plan cannot be read the entry still works — it is matched on the repo folder name instead.</summary>
    private static string? PlanNameIn(string repo)
    {
        try
        {
            var path = Path.Combine(repo, "conductor.plan.json");
            if (!File.Exists(path)) return null;
            return PlanConfig.Load(path).Name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
