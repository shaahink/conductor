using Conductor.Core.Store;

namespace Conductor.Core.Courier;

/// <summary>DV4.1 / findings §1.4-B — where the courier keeps what it owns.
///
/// <para>Machine-level, not project-level, and that is the whole architectural claim: the courier
/// outlives every run, so nothing of its own may live in a <c>.conductor</c> that belongs to one
/// repo. It sits beside the two things DV3.4 already put at the state home for exactly this reason —
/// <c>chat-routes.json</c> (the sticky selections) and <c>dead-letter/</c> (the notes nobody could
/// file) — because those were always going to be read by a process with no plan in front of it.</para>
///
/// <para>Five paths, and the split between the first two is deliberate. <c>courier.json</c> is
/// EDITED BY A PERSON (which projects may be filed against, which chats may talk to it); the offset
/// is written by the poll loop several times a minute. One file for both would mean the hot writer
/// rewriting the human's file all day, and the first crash mid-write would take the allowlist with
/// it.</para></summary>
public static class CourierHome
{
    /// <summary>The courier's own directory under the state home.</summary>
    public const string DirName = "courier";

    /// <summary>What a person configures: the project allowlist and the chats.</summary>
    public const string SettingsFileName = "courier.json";

    /// <summary>What the poll loop advances. Its own file — see the type remarks.</summary>
    public const string OffsetFileName = "offset.json";

    /// <summary>DV4.2 — what the RUNNING courier says about itself: pid, protocol, engine, task.
    /// Written at startup, cleared on the way out; see <see cref="CourierPresence"/> for why a file
    /// rather than a socket answers this question before DV4.3's listener exists.</summary>
    public const string PresenceFileName = "courier.run.json";

    /// <summary>DV4.3 - the per-install shared secret a run proves itself with at the loopback
    /// hello. Its own file rather than a field in <see cref="SettingsFileName"/> for one reason:
    /// courier.json is EDITED BY A PERSON and gets pasted into a bug report, and a secret in a file
    /// people paste is a secret that leaks. See <see cref="CourierSecret"/> for why the file's
    /// permissions, not the call that set them, are the boundary.</summary>
    public const string SecretFileName = "courier.secret";

    /// <summary>Where inbound media lands before it is adopted into a project's inbox. Under the
    /// state home rather than any repo: at download time the courier does not yet know which project
    /// the note is for, and a file written into the wrong repo is a file in a public checkout.</summary>
    public const string MediaDirName = "media";

    /// <param name="stateHomeRoot">The machine's state home, or null for the resolved one.</param>
    public static string DirFor(string? stateHomeRoot = null) =>
        Path.Combine(Root(stateHomeRoot), DirName);

    public static string SettingsPathFor(string? stateHomeRoot = null) =>
        Path.Combine(DirFor(stateHomeRoot), SettingsFileName);

    public static string OffsetPathFor(string? stateHomeRoot = null) =>
        Path.Combine(DirFor(stateHomeRoot), OffsetFileName);

    public static string PresencePathFor(string? stateHomeRoot = null) =>
        Path.Combine(DirFor(stateHomeRoot), PresenceFileName);

    public static string SecretPathFor(string? stateHomeRoot = null) =>
        Path.Combine(DirFor(stateHomeRoot), SecretFileName);

    public static string MediaDirFor(string? stateHomeRoot = null) =>
        Path.Combine(DirFor(stateHomeRoot), MediaDirName);

    private static string Root(string? stateHomeRoot) =>
        string.IsNullOrWhiteSpace(stateHomeRoot) ? StateHome.Root : stateHomeRoot;
}
