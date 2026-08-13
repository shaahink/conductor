using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Conductor.Core.Store;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// KS0.2 — <c>conductor run close</c> and <c>conductor run adopt</c>.
/// <para>An engine that is killed never writes its own ending, so its <c>runs</c> row says
/// <c>running</c> for ever. There was no verb for that: the Karvan run's row had to be corrected with
/// <b>hand-edited SQL in two databases</b>, and the procedure was written into
/// <c>.conductor/WATCH-HANDOFF.md</c> so the next person could repeat it. This is the replacement.
/// It refuses to write a store a live engine is using, it stamps the instant the run actually
/// stopped rather than the instant you noticed, and it leaves a note in the event spine saying who
/// did it and why — three things hand SQL does none of.</para>
/// <para>Registered as a hidden top-level command and reached as two words, because <c>run</c> itself
/// has to keep starting runs; see the rewrite in <c>Program.cs</c>.</para>
/// </summary>
public sealed class RunRecordCommand : Command<RunRecordCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<verb>")]
        [Description("close (write a terminal status) or adopt (annotate, leaving the status alone).")]
        public string Verb { get; init; } = "";

        [CommandArgument(1, "[run]")]
        [Description("Run id, or any unambiguous prefix of one.")]
        public string? Run { get; init; }

        [CommandOption("--status <STATUS>")]
        [Description("close only: closed (default), completed, or aborted.")]
        public string Status { get; init; } = RunRecord.Closed;

        [CommandOption("--reason <TEXT>")]
        [Description("Why the record is being changed. Goes into the run's event spine verbatim.")]
        public string? Reason { get; init; }

        [CommandOption("--ended <ISO8601>")]
        [Description("close only: when the run stopped. Default is its last recorded activity.")]
        public string? Ended { get; init; }

        [CommandOption("--home <PATH>")]
        [Description("Read a state home other than this machine's.")]
        public string? Home { get; init; }

        [CommandOption("--dry-run")]
        [Description("Say what would change and write nothing.")]
        public bool DryRun { get; init; }

        [CommandOption("--json")]
        [Description("Machine-readable output.")]
        public bool Json { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var verb = settings.Verb.ToLowerInvariant();
        if (verb is not ("close" or "adopt")) return Help(verb);
        if (string.IsNullOrWhiteSpace(settings.Run)) return Help(verb, "which run? pass a run id or a prefix of one.");

        var root = string.IsNullOrWhiteSpace(settings.Home) ? StateHome.Root : Path.GetFullPath(settings.Home);
        var matches = RunRecordMaintenance.Find(root, settings.Run);

        if (matches.Count == 0) return Fail(settings, $"no run in this machine's catalogue starts with '{settings.Run}'.");
        if (matches.Count > 1)
            return Fail(settings, $"'{settings.Run}' names {matches.Count} runs: "
                                  + string.Join(", ", matches.Select(m => $"{Short(m.RunId)} ({m.Slug})"))
                                  + ". Use more of the id.");

        var match = matches[0];

        // Before anything else, including --dry-run. A dry run that answers "would close" for a store
        // no run of this verb will ever write is not a preview, it is a wrong answer - and this is
        // exactly the question an operator runs the dry run to ask.
        if (match.Live)
            return Fail(settings,
                $"{Short(match.RunId)} lives in {match.Slug}, which a live engine is using - a record is "
                + "never changed under the engine that owns it. Stop that run (or wait for it) and try again.");

        return verb == "close" ? Close(match, settings) : Adopt(match, settings);
    }

    // ------------------------------------------------------------------------------ close

    private static int Close(RunRecordMatch match, Settings settings)
    {
        if (RunRecord.IsTerminal(match.Status))
            return Fail(settings, $"{Short(match.RunId)} is already {match.Status} - nothing to close.");

        var ended = ResolveEnded(match, settings, out var endedSource);
        if (ended is null)
            return Fail(settings, $"--ended '{settings.Ended}' is not a timestamp this can read (try 2026-08-05T21:40:00Z).");

        if (settings.DryRun)
        {
            var preview = $"would close {Short(match.RunId)} ({match.PlanName}) in {match.Slug}: "
                          + $"{match.Status} -> {settings.Status}, ended {ended:O} ({endedSource})";
            return Say(settings, true, preview);
        }

        var outcome = RunRecordMaintenance.Close(
            match, settings.Status, ended.Value, Who(), settings.Reason, TimeProvider.System);
        return Say(settings, outcome.Ok,
                   outcome.Ok
                       ? $"{Short(match.RunId)} ({match.PlanName}) in {match.Slug}: {match.Status} -> "
                         + $"{settings.Status}, ended {ended:O} ({endedSource}). {outcome.Message}"
                       : outcome.Message);
    }

    /// <summary>When did it stop? The operator's word first, then the last thing the run is recorded
    /// as having done, and only then the clock — because "now" is the one answer that is certainly
    /// wrong for a run that died weeks ago, and every duration computed from the row inherits
    /// it.</summary>
    private static DateTimeOffset? ResolveEnded(RunRecordMatch match, Settings settings, out string source)
    {
        if (!string.IsNullOrWhiteSpace(settings.Ended))
        {
            source = "as given";
            return DateTimeOffset.TryParse(settings.Ended, CultureInfo.InvariantCulture,
                                           DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                                           out var given)
                ? given
                : null;
        }

        if (RunRecordMaintenance.LastActivityUtc(match.Db, match.RunId) is { } last)
        {
            source = "its last recorded activity";
            return last;
        }

        source = "now - the run left nothing else behind";
        return DateTimeOffset.UtcNow;
    }

    // ------------------------------------------------------------------------------ adopt

    private static int Adopt(RunRecordMatch match, Settings settings)
    {
        var note = settings.Reason;
        if (string.IsNullOrWhiteSpace(note))
            return Fail(settings, "adopt needs --reason: an annotation nobody can read is not provenance.");

        if (settings.DryRun)
            return Say(settings, true,
                       $"would annotate {Short(match.RunId)} ({match.PlanName}) in {match.Slug}, "
                       + $"leaving status {match.Status}: {note}");

        var outcome = RunRecordMaintenance.Adopt(match, Who(), note, TimeProvider.System);
        return Say(settings, outcome.Ok,
                   outcome.Ok
                       ? $"{Short(match.RunId)} ({match.PlanName}) in {match.Slug} annotated, status left "
                         + $"{match.Status}. {outcome.Message}"
                       : outcome.Message);
    }

    // ------------------------------------------------------------------------------ shape

    /// <summary>Provenance is worth nothing if it says "the CLI". Machine and user are what tell two
    /// operators of the same catalogue apart, and the engine build is what tells you which code
    /// wrote it.</summary>
    private static string Who() =>
        $"{Environment.UserName}@{Environment.MachineName} (conductor {Conductor.Core.BuildInfo.Current.Full})";

    private static string Short(string runId) => runId[..Math.Min(8, runId.Length)];

    private static int Fail(Settings settings, string message) => Say(settings, false, message);

    private static int Say(Settings settings, bool ok, string message)
    {
        if (settings.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new RunRecordJson(ok, message),
                                                       RunRecordJsonContext.Default.RunRecordJson));
            return ok ? 0 : 1;
        }

        AnsiConsole.MarkupLine(ok
            ? $"[green]{Markup.Escape(message)}[/]"
            : $"[red]{Markup.Escape(message)}[/]");
        return ok ? 0 : 1;
    }

    private static int Help(string verb, string? why = null)
    {
        AnsiConsole.MarkupLine(why is null
            ? $"[red]unknown run-record verb '{Markup.Escape(verb)}'.[/]"
            : $"[red]{Markup.Escape(why)}[/]");
        AnsiConsole.MarkupLine("[grey]conductor run close <id> [[--status closed|completed|aborted]] [[--reason ...]][/]");
        AnsiConsole.MarkupLine("[grey]conductor run adopt <id> --reason \"...\"[/]");
        return 2;
    }
}

/// <summary>The <c>--json</c> shape of a record change.</summary>
public sealed record RunRecordJson(bool Ok, string Message);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RunRecordJson))]
internal sealed partial class RunRecordJsonContext : JsonSerializerContext;
