using System.Text.Json;
using System.Text.Json.Serialization;

using Conductor.Core.Inbox;
using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Store;

namespace Conductor.Core.Courier;

/// <summary>One project the courier may file against. The repo is carried rather than derived
/// because two clones of one plan are two projects (<see cref="ProjectRef"/>'s rule), and the
/// courier must be able to tell them apart without a run to ask.</summary>
/// <param name="Plan">The plan name — what a push's identity line says and what a person types.</param>
/// <param name="Repo">The checkout. Its <c>.conductor</c> is where notes land.</param>
public sealed record CourierProject(string Plan, string Repo);

/// <summary>One chat the courier answers, and what it is allowed to do. Same two-value shape the
/// plan's chat entries have, spelled out again here rather than shared: the courier has no plan, and
/// a machine-level daemon reading a per-project file for its permissions is how a project's own
/// config comes to decide who may write to every other project on the disk.</summary>
/// <param name="ChatId">The Telegram chat id, as a string — group ids are negative and long.</param>
/// <param name="Profile">admin, operator, observer. Only admin may file (<see cref="ChatProfiles"/>).</param>
public sealed record CourierChat(string ChatId, string? Profile);

/// <summary>DV4.1 / findings §1.4-B — what the courier is allowed to do, on this machine.
///
/// <para><b>The allowlist is explicit, and that was the decision.</b> Routing inside a run reads the
/// state catalogue — every (repo, plan) this machine has ever run — because a run can only file
/// against itself or somewhere the owner named in that chat. A machine-level daemon holding the bot
/// token has no such limit: it could write into every checkout the catalogue remembers, including
/// ones abandoned a year ago, from any chat it answers. So the courier files against a project only
/// if the project is written down HERE, by hand, and a note for anything else is parked in the
/// dead-letter box with the reason — never guessed at, never filed somewhere close.</para>
///
/// <para>An absent file is a courier that answers nobody and files nowhere, which is the correct
/// posture for a daemon that has not been configured: it starts, it says what is missing, and it
/// does not begin polling a token on behalf of chats nobody listed.</para></summary>
public sealed class CourierSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The projects this courier may file against. Empty means none — see the type remarks.</summary>
    public List<CourierProject> Projects { get; set; } = [];

    /// <summary>The chats it answers. A message from anywhere else is not replied to and not filed;
    /// an unlisted chat cannot make this machine download bytes.</summary>
    public List<CourierChat> Chats { get; set; } = [];

    /// <summary>Seconds between polls when the long poll returns something. The long poll itself
    /// holds for thirty seconds, so an idle courier is not spinning at this rate.</summary>
    public int PollIntervalSeconds { get; set; } = 4;

    /// <summary>The API root, for a rig. Same seam the plan's telegram block has, at machine level,
    /// so a courier under test never dials the real Bot API.</summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>Reads the settings, or an empty set when there are none. Never throws: a courier that
    /// refuses to start because somebody left a trailing comma is a courier that stops answering the
    /// phone, so a broken file is reported through <see cref="Refusal"/> and treated as empty.</summary>
    public static CourierSettings Load(string? stateHomeRoot = null)
    {
        var path = CourierHome.SettingsPathFor(stateHomeRoot);
        try
        {
            if (!File.Exists(path)) return new CourierSettings();
            return JsonSerializer.Deserialize<CourierSettings>(File.ReadAllText(path), Json)
                ?? new CourierSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new CourierSettings { Unreadable = ex.Message };
        }
    }

    /// <summary>Why the file on disk could not be read, or null. Set only by <see cref="Load"/>.</summary>
    [JsonIgnore]
    public string? Unreadable { get; set; }

    public void Save(string? stateHomeRoot = null)
    {
        var path = CourierHome.SettingsPathFor(stateHomeRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.Write(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>What stops this courier from doing its job, in one sentence, or null. The same
    /// name-what-is-missing shape the rest of the repo refuses with — never a bare false.</summary>
    public string? Refusal()
    {
        if (Unreadable is { Length: > 0 } broken)
            return $"{CourierHome.SettingsFileName} could not be read ({broken}). "
                 + "Fix it or delete it; the courier will not guess what it said.";
        if (PollIntervalSeconds <= 0)
            return $"pollIntervalSeconds is {PollIntervalSeconds}; it has to be a positive number of "
                 + "seconds (the default is 4).";
        if (Chats.Count == 0)
            return "no chats are listed, so there is nobody to answer. Add one with "
                 + "`conductor courier chat --id <chat-id>`.";
        if (Projects.Count == 0)
            return "no projects are allowed, so there is nowhere to file. Add one with "
                 + "`conductor courier allow --repo <path>`.";

        // ChatProfiles' rule, not a new one: an unrecognised profile is refused by NAME, never read
        // as admin. "Unrecognised means default" is precisely how a chat ends up with more surface
        // than anybody asked it to have, and here that surface is every project on the disk.
        foreach (var chat in Chats)
            if (chat.Profile is { Length: > 0 } named && ChatProfiles.TryParse(named) is null)
                return $"chat {chat.ChatId} has profile \"{named}\", which is not one of: "
                     + string.Join(", ", ChatProfiles.Names) + ".";

        return null;
    }

    /// <summary>The allowlist as routing sees it — and NOTHING else. This is the one method that
    /// makes the explicit-allowlist decision real: <see cref="ProjectDirectory"/> is handed this list
    /// verbatim instead of reading the state catalogue, so a project that is not written down here
    /// cannot be resolved, cannot be selected with <c>/project</c>, and cannot be filed against.</summary>
    public IReadOnlyList<ProjectRef> Allowed() =>
        [.. Projects
            .Where(p => !string.IsNullOrWhiteSpace(p.Repo))
            .Select(p => new ProjectRef(
                Plan: p.Plan ?? "",
                Repo: p.Repo,
                Slug: StateHome.SlugFor(p.Repo, p.Plan),
                StateDir: Path.Combine(p.Repo, StateHome.ScratchDirName),
                Present: Directory.Exists(p.Repo)))];

    /// <summary>The profile a chat has, or null when it is not listed at all. Null is the answer that
    /// means "do not reply either" — an unlisted chat gets silence, because a bot that argues with
    /// strangers tells them it exists.</summary>
    public ChatProfile? ProfileFor(string? chatId)
    {
        if (chatId is not { Length: > 0 }) return null;
        foreach (var chat in Chats)
        {
            if (!string.Equals(chat.ChatId, chatId, StringComparison.Ordinal)) continue;

            // An unnamed profile is admin — the same reading the plan's bare allowedChatIds get, so a
            // person who lists one chat id and nothing else gets the behaviour they expected. Anything
            // NAMED has already been through Refusal(); the fallback here is Observer, the safe one,
            // and it is unreachable rather than a policy.
            if (chat.Profile is not { Length: > 0 } named) return ChatProfile.Admin;
            return ChatProfiles.TryParse(named) ?? ChatProfile.Observer;
        }
        return null;
    }
}
