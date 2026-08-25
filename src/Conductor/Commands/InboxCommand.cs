using System.ComponentModel;
using System.Globalization;

using Conductor.Core.Inbox;
using Conductor.Models;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// DV3.3 — the inbox, from a terminal: <c>conductor inbox list|show|prune</c>.
///
/// <para>It exists because something already promised it did. <c>InboxBattery</c> tells a session
/// "`conductor inbox list` shows the whole inbox" when a note is clipped or a note did not fit the
/// battery's cap — and until now that verb did not exist at all (bug #74). A prompt that names a
/// command which is not there is worse than one that names nothing: the reader spends their turn
/// finding that out.</para>
///
/// <para><b>prune is the only deletion path in this system</b> (findings §6.1). Nothing else removes
/// a note, its audio or its transcript: not reading one, not marking it seen, not starting a new
/// run. That is what makes the inbox trustworthy enough to hold the only copy of something the owner
/// said — and it is why prune shows what it would take and does nothing until <c>--yes</c>.</para>
/// </summary>
public sealed class InboxCommand : AsyncCommand<InboxCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandArgument(0, "[VERB]")]
        [Description("list (default), show, add, transcribe, or prune.")]
        public string Verb { get; init; } = "list";

        [CommandOption("--file <PATH>")]
        [Description("add: an audio file or document on disk to file as a note. Copied into the inbox.")]
        public string? File { get; init; }

        [CommandOption("--text <TEXT>")]
        [Description("add: the note's words, or a caption for --file.")]
        public string? Text { get; init; }

        [CommandOption("--all")]
        [Description("transcribe: every note that has audio and no transcript.")]
        public bool All { get; init; }

        [CommandOption("--id <ID>")]
        [Description("One note, by its id. With `show`, prints it in full; with `prune`, takes just that one.")]
        public long? Id { get; init; }

        [CommandOption("--unseen")]
        [Description("list: only notes no session has been handed yet.")]
        public bool Unseen { get; init; }

        [CommandOption("--full")]
        [Description("list: print every note's whole text rather than a one-line summary.")]
        public bool Full { get; init; }

        [CommandOption("--json")]
        [Description("Machine-readable output.")]
        public bool Json { get; init; }

        [CommandOption("--seen")]
        [Description("prune: every note a session has already read. The safe bulk choice - nothing unread is touched.")]
        public bool Seen { get; init; }

        [CommandOption("--older-than <DAYS>")]
        [Description("prune: notes received more than this many days ago.")]
        public int? OlderThanDays { get; init; }

        [CommandOption("--yes")]
        [Description("prune: actually delete. Without it, prune only prints what it would take.")]
        public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var store = new InboxStore(plan.StateDir);

        // Async for one verb only: `transcribe` waits on an external speech model, which is minutes
        // rather than milliseconds. Everything else here is a directory listing.
        return settings.Verb.ToLowerInvariant() switch
        {
            "" or "list" => List(store, settings),
            "show" => Show(store, settings),
            "add" => Add(store, settings),
            "transcribe" => await Transcribe(store, settings, plan).ConfigureAwait(false),
            "prune" => Prune(store, settings),
            var other => Unknown(other),
        };
    }

    private static int Unknown(string verb)
    {
        AnsiConsole.MarkupLine($"[red]error:[/] `conductor inbox {Markup.Escape(verb)}` is not a thing. "
            + "It is [yellow]list[/], [yellow]show --id N[/], [yellow]add --file[/], "
            + "[yellow]transcribe[/] or [yellow]prune[/].");
        return 1;
    }

    private static int List(InboxStore store, Settings settings)
    {
        var notes = settings.Unseen ? store.Unseen() : store.All();
        var cursor = store.ReadCursor();

        if (settings.Json)
        {
            AnsiConsole.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                dir = store.Dir,
                seenThroughId = cursor.SeenThroughId,
                notes,
            }, PlanConfig.JsonOpts));
            return 0;
        }

        if (notes.Count == 0)
        {
            AnsiConsole.MarkupLine(settings.Unseen
                ? "[dim]No unread notes.[/] " + Markup.Escape(store.Dir)
                : "[dim]The inbox is empty.[/] " + Markup.Escape(store.Dir));
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("id");
        table.AddColumn("received (UTC)");
        table.AddColumn("kind");
        table.AddColumn("");
        table.AddColumn(settings.Full ? "note" : "summary");

        foreach (var note in notes)
        {
            table.AddRow(
                note.Id.ToString(CultureInfo.InvariantCulture),
                note.ReceivedUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                Markup.Escape(note.Kind),
                Flags(note, cursor.SeenThroughId),
                Markup.Escape(settings.Full ? Body(note) : note.Summary));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[dim]{notes.Count} note(s) · {Markup.Escape(store.Dir)} · "
            + $"read through id {cursor.SeenThroughId.ToString(CultureInfo.InvariantCulture)} · "
            + "nothing here is ever deleted except by [/][yellow]conductor inbox prune[/]");
        return 0;
    }

    /// <summary>The two facts about a note that are not in its text: whether a session has read it,
    /// and whether audio is sitting there with nobody having read it out.</summary>
    private static string Flags(InboxNote note, long seenThrough)
    {
        var flags = new List<string>();
        if (note.Id > seenThrough) flags.Add("[green]unread[/]");
        if (note.Transcribed) flags.Add(note.TranscriptConfidence is { } c
            ? "[dim]transcript " + (c * 100).ToString("0", CultureInfo.InvariantCulture) + "%[/]"
            : "[dim]transcript[/]");
        if (note.Untranscribed) flags.Add("[yellow]untranscribed[/]");
        return string.Join(" ", flags);
    }

    private static string Body(InboxNote note)
    {
        var text = note.Text.Trim();
        if (text.Length == 0) text = "(no words)";
        return note.MediaPath is { Length: > 0 } m ? text + "\n[file] " + m : text;
    }

    /// <summary>One note, whole — what the battery's "CLIPPED" header points at.</summary>
    private static int Show(InboxStore store, Settings settings)
    {
        if (settings.Id is not { } id)
        {
            AnsiConsole.MarkupLine("[red]error:[/] `conductor inbox show` needs [yellow]--id N[/].");
            return 1;
        }

        var note = store.All().FirstOrDefault(n => n.Id == id);
        if (note is null)
        {
            AnsiConsole.MarkupLine($"[red]error:[/] no note {id.ToString(CultureInfo.InvariantCulture)} "
                + $"in {Markup.Escape(store.Dir)}.");
            return 1;
        }

        if (settings.Json)
        {
            AnsiConsole.WriteLine(System.Text.Json.JsonSerializer.Serialize(note, PlanConfig.JsonOpts));
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold]note {id.ToString(CultureInfo.InvariantCulture)}[/] · "
            + $"{note.ReceivedUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}Z · "
            + Markup.Escape(note.Kind));
        if (note.MediaPath is { Length: > 0 } media)
            AnsiConsole.MarkupLine("[dim]file:[/] " + Markup.Escape(Path.Combine(store.Dir, media)));
        if (note.TranscriptPath is { Length: > 0 } transcript)
            AnsiConsole.MarkupLine("[dim]transcript:[/] " + Markup.Escape(Path.Combine(store.Dir, transcript))
                + (note.TranscriptConfidence is { } c
                    ? " [dim]· confidence " + (c * 100).ToString("0", CultureInfo.InvariantCulture) + "%[/]"
                    : ""));
        else if (note.Untranscribed)
            AnsiConsole.MarkupLine("[yellow]untranscribed[/] [dim]— the audio is kept; set "
                + "courier.transcribe.command to read it out[/]");

        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine(note.Text.Trim().Length > 0 ? note.Text : "(no words)");
        return 0;
    }

    /// <summary>DV3.3 - a note filed from this machine rather than from a phone. The owner has an
    /// .ogg on disk (an exported voice message, a meeting recording) and the inbox is where a note
    /// about this project belongs; insisting it travel through a messenger first would be strange.
    ///
    /// <para>It goes through <see cref="InboxStore.Append"/> like every other note - same dedup,
    /// same atomic write, same index - because a second way to write a note would be a second thing
    /// to keep correct.</para></summary>
    private static int Add(InboxStore store, Settings settings)
    {
        var text = settings.Text?.Trim() ?? "";
        if (settings.File is not { Length: > 0 } && text.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]error:[/] `conductor inbox add` needs [yellow]--file PATH[/] "
                + "or [yellow]--text[/].");
            return 1;
        }

        string? mediaRel = null;
        var kind = InboxNote.TextKind;
        var id = store.All().Select(n => n.Id).DefaultIfEmpty(0).Max() + 1;

        if (settings.File is { Length: > 0 } path)
        {
            if (!System.IO.File.Exists(path))
            {
                AnsiConsole.MarkupLine($"[red]error:[/] no file at {Markup.Escape(path)}.");
                return 1;
            }

            var mediaDir = Path.Combine(store.Dir, "media");
            Directory.CreateDirectory(mediaDir);
            var name = id.ToString(CultureInfo.InvariantCulture) + "-" + Path.GetFileName(path);
            System.IO.File.Copy(path, Path.Combine(mediaDir, name), overwrite: false);
            mediaRel = "media/" + name;
            kind = AudioExtensions.Contains(Path.GetExtension(path)) ? "voice" : "document";
        }

        var note = new InboxNote(id, DateTime.UtcNow, "local", kind, text, MediaPath: mediaRel);
        if (!store.Append(note))
        {
            AnsiConsole.MarkupLine($"[red]error:[/] a note {id.ToString(CultureInfo.InvariantCulture)} "
                + "is already filed.");
            return 1;
        }

        AnsiConsole.MarkupLine($"[green]filed[/] note {id.ToString(CultureInfo.InvariantCulture)} in "
            + Markup.Escape(store.Dir)
            + (note.Untranscribed ? " [dim]- run `conductor inbox transcribe --id "
                + id.ToString(CultureInfo.InvariantCulture) + "` to read it out[/]" : ""));
        return 0;
    }

    /// <summary>What the sender is PROMISED when no command was configured: the audio is kept and can
    /// be transcribed later. Bug #74 was that exact shape of promise with nothing behind it, so this
    /// is the verb that makes the sentence true.</summary>
    private static async Task<int> Transcribe(InboxStore store, Settings settings, PlanConfig plan)
    {
        var transcriber = new LocalCommandTranscriber(plan.Courier?.Transcribe,
            m => AnsiConsole.MarkupLine("[dim]" + Markup.Escape(m) + "[/]"));

        if (!transcriber.Configured)
        {
            AnsiConsole.MarkupLine("[red]error:[/] no transcribe command is configured. Set "
                + "[yellow]courier.transcribe.command[/] in the plan, or the "
                + $"[yellow]{TranscribeConfig.CommandEnvVar}[/] environment variable. "
                + "This repo ships one: [dim]python tools/transcribe/whisper-json.py[/]");
            return 1;
        }

        var targets = settings.Id is { } id
            ? store.All().Where(n => n.Id == id).ToList()
            : settings.All ? store.All().Where(n => n.Untranscribed).ToList() : null;

        if (targets is null)
        {
            AnsiConsole.MarkupLine("[red]error:[/] `conductor inbox transcribe` needs "
                + "[yellow]--id N[/] or [yellow]--all[/].");
            return 1;
        }

        if (targets.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]Nothing to transcribe.[/]");
            return 0;
        }

        var failures = 0;
        foreach (var note in targets)
            failures += await TranscribeOne(store, transcriber, note).ConfigureAwait(false);
        return failures > 0 ? 1 : 0;
    }

    /// <summary>One note, transcribed and attached. Every outcome is printed, including the ones
    /// that leave the note untranscribed - the audio is kept in all of them.</summary>
    private static async Task<int> TranscribeOne(InboxStore store, LocalCommandTranscriber transcriber, InboxNote note)
    {
        var idText = note.Id.ToString(CultureInfo.InvariantCulture);
        if (note.MediaPath is not { Length: > 0 } media)
        {
            AnsiConsole.MarkupLine($"[yellow]skipped[/] {idText}: no audio.");
            return 0;
        }

        var audio = Path.Combine(store.Dir, media.Replace('/', Path.DirectorySeparatorChar));
        AnsiConsole.MarkupLine($"[dim]transcribing {Markup.Escape(audio)} ...[/]");

        var outcome = await transcriber.TranscribeAsync(audio, CancellationToken.None).ConfigureAwait(false);

        if (!outcome.HasWords || outcome.Transcript is not { } transcript)
        {
            AnsiConsole.MarkupLine($"[red]not transcribed[/] {idText}: "
                + Markup.Escape(outcome.Detail ?? "the command produced nothing")
                + " [dim]- the audio is kept[/]");
            return 1;
        }

        var stored = store.AttachTranscript(note.Id, transcript, transcriber.ConfidenceFloor);
        AnsiConsole.MarkupLine($"[green]transcribed[/] {idText} "
            + $"[dim]({Markup.Escape(transcript.ConfidenceLine(transcriber.ConfidenceFloor))})[/]");
        AnsiConsole.WriteLine(stored?.Text ?? transcript.Marked(transcriber.ConfidenceFloor));
        return 0;
    }

    /// <summary>What counts as audio when a file is added from disk. Voice and audio are the kinds
    /// the transcribe path looks at, so a .ogg added here has to land as one.</summary>
    private static readonly HashSet<string> AudioExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".ogg", ".oga", ".opus", ".mp3", ".m4a", ".wav", ".flac", ".webm" };

    /// <summary>THE only deletion path (findings §6.1). Deliberately awkward: it needs a filter, it
    /// prints what it would take, and it does nothing at all without <c>--yes</c>. Retention on a
    /// machine holding the only copy of somebody's voice is a decision, not a default.</summary>
    private static int Prune(InboxStore store, Settings settings)
    {
        var all = store.All();
        var seenThrough = store.ReadCursor().SeenThroughId;

        var chosen = Chosen(all, settings, seenThrough);
        if (chosen is null)
        {
            AnsiConsole.MarkupLine("[red]error:[/] `conductor inbox prune` needs to be told WHAT: "
                + "[yellow]--id N[/], [yellow]--seen[/] (every note a session has read) or "
                + "[yellow]--older-than DAYS[/].");
            return 1;
        }

        if (chosen.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]Nothing matches — nothing pruned.[/]");
            return 0;
        }

        var files = chosen.SelectMany(store.FilesOf).ToList();
        foreach (var note in chosen)
            AnsiConsole.MarkupLine($"  {note.Id.ToString(CultureInfo.InvariantCulture)} · "
                + $"{note.ReceivedUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} · "
                + $"{Markup.Escape(note.Kind)} · {Markup.Escape(note.Summary)}");

        if (!settings.Yes)
        {
            AnsiConsole.MarkupLine($"[yellow]{chosen.Count} note(s), {files.Count} file(s) would be "
                + "deleted.[/] Nothing was. Add [yellow]--yes[/] to do it.");
            return 0;
        }

        var removed = chosen.Sum(store.Prune);
        AnsiConsole.MarkupLine($"[green]pruned[/] {chosen.Count} note(s), {removed} file(s) deleted "
            + $"from {Markup.Escape(store.Dir)}.");
        return 0;
    }

    /// <summary>Which notes a prune would take, or null when the caller named no filter at all.
    /// Null rather than "everything": a bare <c>prune</c> that emptied the inbox would be one typo
    /// away from losing every note the owner ever left.</summary>
    private static List<InboxNote>? Chosen(IReadOnlyList<InboxNote> all, Settings settings, long seenThrough)
    {
        if (settings.Id is { } id) return [.. all.Where(n => n.Id == id)];

        var filtered = all.AsEnumerable();
        var anyFilter = false;

        if (settings.Seen) { filtered = filtered.Where(n => n.Id <= seenThrough); anyFilter = true; }
        if (settings.OlderThanDays is { } days)
        {
            var cutoff = DateTime.UtcNow.AddDays(-Math.Abs(days));
            filtered = filtered.Where(n => n.ReceivedUtc < cutoff);
            anyFilter = true;
        }

        return anyFilter ? [.. filtered] : null;
    }
}
