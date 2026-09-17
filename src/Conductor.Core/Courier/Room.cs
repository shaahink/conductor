using System.Text.Json.Serialization;

namespace Conductor.Core.Courier;

/// <summary>PK4.1 / D6 — a room: the chats a project talks to, the words its card ends with, and where
/// its voice is kept.
///
/// <para><b>Rooms are conductor's, and they stay private.</b> One file per project under the courier
/// home, <c>rooms/&lt;project-slug&gt;.json</c> — machine-level like everything else the courier owns,
/// so nothing about a room ever enters a repository. The chat ids of a stakeholder group and the voice
/// file that describes the people in it are exactly what a shared checkout must not carry
/// (F-COUR-9).</para>
///
/// <para><b>The voice is pointed at, never copied.</b> <see cref="Voice"/> is a path to wherever the
/// owner keeps the file; this type reads the path, not the file. The part of the voice every room
/// shares is in the tree as <c>docs/rooms/character.md</c>.</para>
///
/// <para><b>A room authorises nothing</b> (F-OBS-4). It says where the engine speaks; who may give an
/// order is still the terminal's business.</para></summary>
public sealed class Room
{
    /// <summary>The project's name — the room's name, the only thing <c>room list</c> prints.</summary>
    public string Project { get; set; } = "";

    /// <summary>The checkout this room belongs to, when it is known. An imported room has none: the
    /// old private home was keyed by the repo's folder name, and so is the fallback match.</summary>
    public string? Repo { get; set; }

    public RoomChats Chats { get; set; } = new();

    /// <summary>The card's closing words, in the project's own language (PK4.2 renders them).</summary>
    public RoomFooter Footer { get; set; } = new();

    /// <summary>The path to the room's voice file. Pointed at, never read into a repository.</summary>
    public string? Voice { get; set; }

    /// <summary>Where this room came from: the room file, or the fallback it was assembled from. Never
    /// written.</summary>
    [JsonIgnore]
    public string Source { get; set; } = "";
}

/// <summary>The two chats a room has. Ids stay strings, as <see cref="CourierChat"/> keeps them.</summary>
public sealed class RoomChats
{
    /// <summary>The owner's chat: findings, parks, anything that wants a hand on it.</summary>
    public string? Admin { get; set; }

    /// <summary>The stakeholder group: cards, never orders.</summary>
    public string? Observer { get; set; }
}

/// <summary>The strings a card closes with. Null means the card's own default.</summary>
public sealed class RoomFooter
{
    /// <summary>The counts line, e.g. "done of total, live live".</summary>
    public string? Counters { get; set; }

    /// <summary>The footer when the checkpoint is live.</summary>
    public string? Live { get; set; }

    /// <summary>The footer when it lands with a later deploy.</summary>
    public string? Pending { get; set; }
}
