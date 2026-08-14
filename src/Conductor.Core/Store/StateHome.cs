using System.Security.Cryptography;
using System.Text;

namespace Conductor.Core.Store;

/// <summary>
/// K3.1: where a run's <c>run.db</c> lives. Before K3.1 the answer was one hard-coded line —
/// <c>PlanConfig.StateDir => Path.Combine(Repo, ".conductor")</c> — and <c>.conductor/.gitignore</c>
/// is a bare <c>*</c>, so every session, cost, gate, bug and event died with the machine. Clone the
/// repo elsewhere and the project had no past.
/// <para>The store now lives in a machine-level home (<c>%LOCALAPPDATA%\conductor</c> on Windows,
/// <c>$XDG_DATA_HOME/conductor</c> or <c>~/.local/share/conductor</c> elsewhere) under one directory
/// per (repo path + plan name), indexed by <see cref="StateCatalogue"/>. <c>.conductor/</c> keeps
/// what genuinely belongs to the working tree: per-run scratch (logs, transcripts, evidence), the
/// discovery files a live run publishes (<c>control-plane.json</c>, the engine lock), and the tracked
/// deliverables (<c>REPORT.md</c>, <c>followups.md</c>, <c>handovers/</c>).</para>
/// <para><b>Not a synced folder, and that is decided.</b> SQLite on OneDrive or Dropbox corrupts
/// under concurrent writers, and this machine already runs two engines at once.</para>
/// </summary>
public static class StateHome
{
    /// <summary>Overrides the machine-level root wholesale. Set this and every derived path moves
    /// with it — the escape hatch for tests, for a second disk, and for a rig that must not touch
    /// the operator's real history.</summary>
    public const string HomeEnvVar = "CONDUCTOR_STATE_HOME";

    /// <summary>Overrides the resolved database FILE, ignoring root and catalogue entirely. Highest
    /// precedence, because it is the bluntest: "this exact db, no derivation".</summary>
    public const string RunDbEnvVar = "CONDUCTOR_RUN_DB";

    /// <summary>A repo-local pointer at <c>&lt;repo&gt;/.conductor/state-pointer.json</c>. This is
    /// the seam the lanes plan needs: a lane worktree has a different repo path, so it derives a
    /// different slug, but dropping a pointer in it makes it read and write the SAME run as the
    /// primary tree. Explicit beats clever.</summary>
    public const string PointerFileName = "state-pointer.json";

    /// <summary>The scratch/discovery directory inside the working tree. Unchanged by K3.1 — only
    /// <c>run.db</c> left it.</summary>
    public const string ScratchDirName = ".conductor";

    public const string RunDbFileName = "run.db";

    /// <summary>Where runs live under the root. One level of nesting so the root can also hold the
    /// catalogue and, later, cross-run artifacts without them looking like runs.</summary>
    public const string RunsDirName = "runs";

    /// <summary>The OS-appropriate root, ignoring <see cref="HomeEnvVar"/>. Windows uses
    /// <c>%LOCALAPPDATA%</c> (local, not roaming — a SQLite file must never roam); elsewhere XDG's
    /// data home, whose documented default is <c>~/.local/share</c>.</summary>
    public static string DefaultRoot
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "conductor");
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var b = string.IsNullOrWhiteSpace(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
                : xdg;
            return Path.Combine(b, "conductor");
        }
    }

    /// <summary><see cref="HomeEnvVar"/> if set, else <see cref="DefaultRoot"/>.</summary>
    public static string Root
        => Environment.GetEnvironmentVariable(HomeEnvVar) is { Length: > 0 } h
            ? Path.GetFullPath(h)
            : DefaultRoot;

    /// <summary>The catalogue file for a given root.</summary>
    public static string CataloguePathFor(string root) => Path.Combine(root, StateCatalogue.FileName);

    /// <summary>The repo-local pointer path for a repo.</summary>
    public static string PointerPathFor(string repo)
        => Path.Combine(repo, ScratchDirName, PointerFileName);

    /// <summary>The legacy (pre-K3.1) database path for a repo — still read by the migration, and
    /// still written by any older engine on this machine, which is why the import copies rather
    /// than moves.</summary>
    public static string LegacyDbPathFor(string repo)
        => Path.Combine(repo, ScratchDirName, RunDbFileName);

    /// <summary>Normalises a repo path for keying: absolute, no trailing separator, and case-folded
    /// on Windows because <c>C:\Code\conductor</c> and <c>C:\code\conductor</c> are one directory
    /// there. Two spellings of one repo MUST NOT produce two histories.</summary>
    public static string NormalizeRepo(string repo)
    {
        if (string.IsNullOrWhiteSpace(repo)) return "";
        var full = Path.GetFullPath(repo)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // A drive root trims to "C:" — put the separator back so the key is a real path.
        if (full.Length == 2 && full[1] == ':') full += Path.DirectorySeparatorChar;
        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    /// <summary>The directory name for a (repo, plan) pair: a readable leaf so a human can find it
    /// in Explorer, plus eight hex of SHA-256 over the normalised key so two repos with the same
    /// leaf name never collide.</summary>
    public static string SlugFor(string repo, string? plan)
    {
        var key = KeyFor(repo, plan);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..8].ToLowerInvariant();
        var leaf = Sanitize(Path.GetFileName(
            NormalizeRepo(repo).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        if (leaf.Length == 0) leaf = "repo";
        var planPart = Sanitize(plan ?? "");
        return planPart.Length == 0 ? $"{leaf}-{hash}" : $"{leaf}-{planPart}-{hash}";
    }

    /// <summary>The catalogue key. Normalised repo, a NUL separator so no plan name can forge a
    /// different repo, then the plan name (case-folded — plan names are identifiers, not prose).</summary>
    public static string KeyFor(string repo, string? plan)
        => NormalizeRepo(repo) + "\0" + (plan ?? "").Trim().ToLowerInvariant();

    /// <summary>The derived database path — what a (repo, plan) resolves to when nothing overrides
    /// it. Pure: no I/O, no side effects.</summary>
    public static string DerivedRunDbPath(string root, string repo, string? plan)
        => Path.Combine(root, RunsDirName, SlugFor(repo, plan), RunDbFileName);

    /// <summary>
    /// Resolves the run database for a (repo, plan), importing a legacy <c>.conductor/run.db</c> on
    /// first sight and recording the pair in the catalogue.
    /// <para><b>Precedence, in order:</b> (1) <see cref="RunDbEnvVar"/>; (2) the repo-local
    /// <see cref="PointerFileName"/>; (3) derived from <paramref name="root"/> + slug. Only case (3)
    /// migrates or catalogues — an explicit override is taken at its word.</para>
    /// </summary>
    /// <param name="root">The state home root; defaults to <see cref="Root"/>.</param>
    /// <param name="allowMigration">False disables the legacy import (read-only callers, and the
    /// tests that assert the fast path does no I/O).</param>
    public static StateResolution Resolve(
        string repo, string? plan, string? root = null, bool allowMigration = true)
    {
        if (Environment.GetEnvironmentVariable(RunDbEnvVar) is { Length: > 0 } explicitDb)
            return new StateResolution(Path.GetFullPath(explicitDb), StateSource.EnvOverride, null);

        if (StatePointer.TryRead(PointerPathFor(repo)) is { Length: > 0 } pointed)
            return new StateResolution(pointed, StateSource.Pointer, null);

        var r = root ?? Root;
        var target = DerivedRunDbPath(r, repo, plan);
        // No `!File.Exists(target)` guard here: whether an existing target may be replaced is
        // StateMigration's call, not this one's (bug #33 — a target that is still the untouched copy
        // of a legacy file that has since moved on is refreshed, and one that has work of its own is
        // left alone with a warning). ImportLegacy's own first act is that same File.Exists.
        StateImport? import = null;
        if (allowMigration)
            import = StateMigration.ImportLegacy(LegacyDbPathFor(repo), target, r);

        StateCatalogue.Upsert(r, repo, plan, target, import);
        return new StateResolution(target, StateSource.Derived, import);
    }

    /// <summary>
    /// KS2.3: <see cref="Resolve"/>'s read-only twin — the same precedence, ZERO side effects. It
    /// never runs the legacy import and never writes the catalogue, so a preview (<c>journey</c>,
    /// the hub's pre-launch itinerary) can name the database <c>run</c> would open without
    /// registering a run that was never started. When the derived target does not exist yet but a
    /// legacy <c>.conductor/run.db</c> does, the LEGACY file is the answer: it is the byte-for-byte
    /// source <see cref="StateMigration.ImportLegacy"/> would copy on <c>run</c>'s first sight, so
    /// a resume peek over it reports exactly what <c>run</c> is about to do. (When both exist the
    /// target wins, as it does after any real resolution.)
    /// </summary>
    public static StateResolution Peek(string repo, string? plan, string? root = null)
    {
        if (Environment.GetEnvironmentVariable(RunDbEnvVar) is { Length: > 0 } explicitDb)
            return new StateResolution(Path.GetFullPath(explicitDb), StateSource.EnvOverride, null);

        if (StatePointer.TryRead(PointerPathFor(repo)) is { Length: > 0 } pointed)
            return new StateResolution(pointed, StateSource.Pointer, null);

        var target = DerivedRunDbPath(root ?? Root, repo, plan);
        var legacy = LegacyDbPathFor(repo);
        return new StateResolution(
            !File.Exists(target) && File.Exists(legacy) ? legacy : target, StateSource.Derived, null);
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Trim())
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : (c is '-' or '_' or '.' ? c : '-'));
        return sb.ToString().Trim('-', '.');
    }
}

/// <summary>Which rule produced a resolved database path. Reported by <c>doctor</c> so "why is it
/// reading THAT db" is answerable without reading source.</summary>
public enum StateSource
{
    /// <summary>Derived from the state home root and the (repo, plan) slug.</summary>
    Derived,
    /// <summary>Named outright by <see cref="StateHome.RunDbEnvVar"/>.</summary>
    EnvOverride,
    /// <summary>Named by the repo-local <c>.conductor/state-pointer.json</c>.</summary>
    Pointer,
}

/// <summary>The answer to "which run.db, and how did we get there".</summary>
/// <param name="RunDbPath">The absolute database path.</param>
/// <param name="Source">Which precedence rule won.</param>
/// <param name="Import">Non-null only when this resolution performed a legacy import.</param>
public sealed record StateResolution(string RunDbPath, StateSource Source, StateImport? Import);
