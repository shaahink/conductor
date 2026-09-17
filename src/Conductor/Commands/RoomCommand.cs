using System.ComponentModel;
using System.Text.RegularExpressions;

using Conductor.Core.Courier;

using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>PK4.1 / D6 — <c>conductor room</c>: the rooms this machine speaks in.
///
/// <para>Machine-level like <c>courier</c> and <c>say</c>: a room lives in the courier home, never in
/// a checkout, so no plan is read. <c>--repo</c> names the checkout a room belongs to; the room's file
/// is keyed by its project name.</para>
///
/// <para><b>Nothing here prints a chat id.</b> A stakeholder group's id is the owner's, and this
/// verb's output is the kind that gets pasted into a bug report or an evidence file. <c>list</c> prints
/// names, <c>show</c> prints whether each chat is set. The room file itself is where the number is.</para></summary>
public sealed partial class RoomCommand : Command<RoomCommand.Settings>
{
    /// <summary>What <c>--help</c> and Program.cs print.</summary>
    public const string VerbDescription =
        "PK4.1: the rooms this machine speaks in, kept in the courier home and never in a repo - `room list`, "
      + "`room show [[--repo PATH|--project NAME]]`, `room add --repo PATH [[--admin ID]] [[--observer ID]] [[--voice PATH]]`, "
      + "`room import [[--from DIR]]` (the one-time move from ~/.claude/telegram).";

    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[VERB]")]
        [Description("list (default), show, add, import.")]
        public string Verb { get; init; } = "list";

        [CommandOption("--repo <PATH>")]
        [Description("show/add: the checkout the room belongs to. show defaults to the current directory.")]
        public string? Repo { get; init; }

        [CommandOption("--project <NAME>")]
        [Description("show/add: the room's name. add defaults to the repo's folder name.")]
        public string? Project { get; init; }

        [CommandOption("--admin <CHAT_ID>")]
        [Description("add: the owner's chat.")]
        public string? Admin { get; init; }

        [CommandOption("--observer <CHAT_ID>")]
        [Description("add: the stakeholder group the card goes to. Group ids are negative.")]
        public string? Observer { get; init; }

        [CommandOption("--voice <PATH>")]
        [Description("add: the room's voice file. Pointed at, never copied.")]
        public string? Voice { get; init; }

        [CommandOption("--counters <TEXT>")]
        [Description("add: the card's counts line.")]
        public string? Counters { get; init; }

        [CommandOption("--footer-live <TEXT>")]
        [Description("add: the card's footer when the checkpoint is live.")]
        public string? FooterLive { get; init; }

        [CommandOption("--footer-pending <TEXT>")]
        [Description("add: the card's footer when it lands with a later deploy.")]
        public string? FooterPending { get; init; }

        [CommandOption("--from <DIR>")]
        [Description("import: the old private home to read. Defaults to ~/.claude/telegram.")]
        public string? From { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings) =>
        Run(settings, Console.Out, stateHomeRoot: null);

    /// <summary>The verb, with the state home exposed for a test.</summary>
    /// <returns>0 done, 1 refused.</returns>
    internal static int Run(Settings s, TextWriter output, string? stateHomeRoot)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(output);
        return s.Verb.Trim().ToLowerInvariant() switch
        {
            "" or "list" => List(s, output, stateHomeRoot),
            "show" => Show(s, output, stateHomeRoot),
            "add" => Add(s, output, stateHomeRoot),
            "import" => Import(s, output, stateHomeRoot),
            var other => Refuse(output, $"unknown room verb '{other}'. Use list, show, add or import."),
        };
    }

    private static int List(Settings s, TextWriter output, string? stateHomeRoot)
    {
        var (rooms, unreadable) = Rooms.List(stateHomeRoot);
        var dir = Rooms.DirFor(stateHomeRoot);
        output.WriteLine(rooms.Count == 0 ? $"no rooms yet · {dir}" : $"rooms ({rooms.Count}) · {dir}");
        foreach (var room in rooms) output.WriteLine("  " + room.Project);
        foreach (var file in unreadable) output.WriteLine($"  unreadable: {file} (no project, or not JSON)");

        // D6's offer: what the old private home still holds that is not a room yet.
        var source = s.From ?? RoomImport.DefaultSource();
        var waiting = RoomImport.Offers(source, stateHomeRoot).Where(o => o.Room is not null && !o.AlreadyARoom)
            .Select(o => o.Folder).ToList();
        if (waiting.Count > 0)
            output.WriteLine($"not imported yet from {source}: {string.Join(", ", waiting)} - "
                + "`conductor room import` brings them in; the voice files stay where they are.");
        return 0;
    }

    private static int Show(Settings s, TextWriter output, string? stateHomeRoot)
    {
        var repo = Path.GetFullPath(s.Repo ?? Directory.GetCurrentDirectory());
        var room = s.Project is { Length: > 0 } project
            ? Rooms.Read(Rooms.PathFor(project, stateHomeRoot))
            : Rooms.Find(repo, stateHomeRoot);
        if (room is null)
        {
            output.WriteLine($"no room for {s.Project ?? repo} - a run there falls back to its plan's telegram.chats, "
                + "then this machine's `courier chat` entries. `conductor room add` makes one.");
            return 0;
        }

        output.WriteLine($"room {room.Project} · {room.Source}");
        output.WriteLine("  repo      " + (room.Repo ?? "(none - matched by folder name)"));
        output.WriteLine("  admin     " + (room.Chats.Admin is null ? "not set" : "set"));
        output.WriteLine("  observer  " + (room.Chats.Observer is null ? "not set - no card is posted" : "set"));
        output.WriteLine("  voice     " + (room.Voice is null ? "none" : room.Voice + (File.Exists(room.Voice) ? "" : " (missing)")));
        output.WriteLine("  counters  " + (room.Footer.Counters ?? "(default)"));
        output.WriteLine("  live      " + (room.Footer.Live ?? "(default)"));
        output.WriteLine("  pending   " + (room.Footer.Pending ?? "(default)"));
        return 0;
    }

    private static int Add(Settings s, TextWriter output, string? stateHomeRoot)
    {
        if (s.Repo is not { Length: > 0 } && s.Project is not { Length: > 0 })
            return Refuse(output, "`conductor room add` needs --repo <PATH> or --project <NAME>.");

        string? repo = null;
        if (s.Repo is { Length: > 0 } given)
        {
            repo = Path.GetFullPath(given);
            if (!Directory.Exists(repo)) return Refuse(output, $"{repo} is not a directory on this machine.");
        }

        var name = s.Project?.Trim() is { Length: > 0 } p
            ? p
            : Path.GetFileName(repo!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (Rooms.SlugFor(name).Length == 0)
            return Refuse(output, $"'{name}' has no letter or digit to name a room by; pass --project.");

        foreach (var (flag, id) in new[] { ("--admin", s.Admin), ("--observer", s.Observer) })
        {
            if (id is { Length: > 0 } && !ChatId().IsMatch(id.Trim()))
                return Refuse(output, $"{flag} must be a numeric chat id (a group's is negative).");
        }

        string? voice = null;
        if (s.Voice is { Length: > 0 } v)
        {
            voice = Path.GetFullPath(v);
            if (!File.Exists(voice)) return Refuse(output, $"the voice file {voice} is not there.");
        }

        var room = Rooms.Read(Rooms.PathFor(name, stateHomeRoot))
            ?? (s.Project is null && repo is not null ? Rooms.Find(repo, stateHomeRoot) : null)
            ?? new Room { Project = name };
        room.Repo = repo ?? room.Repo;
        room.Chats.Admin = s.Admin?.Trim() ?? room.Chats.Admin;
        room.Chats.Observer = s.Observer?.Trim() ?? room.Chats.Observer;
        room.Voice = voice ?? room.Voice;
        room.Footer.Counters = s.Counters ?? room.Footer.Counters;
        room.Footer.Live = s.FooterLive ?? room.Footer.Live;
        room.Footer.Pending = s.FooterPending ?? room.Footer.Pending;

        output.WriteLine($"room {room.Project} saved · {Rooms.Save(room, stateHomeRoot)}");
        return 0;
    }

    private static int Import(Settings s, TextWriter output, string? stateHomeRoot)
    {
        var source = s.From ?? RoomImport.DefaultSource();
        var offers = RoomImport.Offers(source, stateHomeRoot);
        if (offers.Count == 0)
        {
            output.WriteLine($"nothing to import - no */{RoomImport.ConfigFileName} under {source}");
            return 0;
        }

        var imported = RoomImport.Import(offers, stateHomeRoot).ToHashSet(StringComparer.Ordinal);
        foreach (var offer in offers)
        {
            output.WriteLine(offer switch
            {
                { Problem: { } problem } => $"  skipped   {offer.Folder}: {problem}",
                { AlreadyARoom: true } => $"  kept      {offer.Folder} (already a room; not overwritten)",
                _ when imported.Contains(offer.Folder) => $"  imported  {offer.Folder}"
                    + (offer.Room?.Voice is null ? "" : " (voice pointed at, not copied)"),
                _ => $"  skipped   {offer.Folder}",
            });
        }
        output.WriteLine($"{imported.Count} imported into {Rooms.DirFor(stateHomeRoot)}");
        return 0;
    }

    private static int Refuse(TextWriter output, string why)
    {
        output.WriteLine("error: " + why);
        return 1;
    }

    [GeneratedRegex(@"^-?\d+$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ChatId();
}
