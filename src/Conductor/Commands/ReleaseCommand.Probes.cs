using System.Globalization;
using System.Text.Json;

using Conductor.Core;
using Conductor.Core.Courier;
using Conductor.Core.History;
using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Github;
using Conductor.Core.Release;
using Conductor.Core.Store;
using Conductor.Core.Update;
using Conductor.Models;

namespace Conductor.Commands;

/// <summary>
/// CH4.1 — the impure half. Everything here runs a real command, reads a real file or asks the real
/// scheduler; nothing here decides anything. The verdicts live in
/// <see cref="Conductor.Core.Release.ReleasePreflight"/> so a test can seed a red fact and prove the
/// exit code moves without a git repository, a courier or a store.
/// </summary>
public sealed partial class ReleaseCommand
{
    /// <summary>What the engine on PATH says it is. <paramref name="Binary"/> is the file that
    /// ANSWERED - not the PATH entry, which on this machine is a scoop shim no process ever executes.
    /// </summary>
    internal sealed record EngineStampProbe(string? Sha, string? Version, bool Dirty, string? Binary);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The migrations directory, relative to the repo root — the one path whose contents
    /// decide whether trap 18 is live this era.</summary>
    private const string MigrationsDir = "src/Conductor.Core/Store/Migrations";

    // ---- 1. merge --------------------------------------------------------------------------

    /// <summary>Runbook section 1, as counts. <c>rev-list --left-right --count base...branch</c>
    /// prints LEFT then RIGHT: left is what base has and branch does not (behind), right is the
    /// other way (ahead). A fast-forward is exactly "left is zero".</summary>
    internal static MergeFacts ProbeMerge(string repo, string? baseBranch, string? branch)
    {
        var baseRef = string.IsNullOrWhiteSpace(baseBranch) ? "master" : baseBranch.Trim();
        var head = string.IsNullOrWhiteSpace(branch) ? Git.Branch(repo) : branch.Trim();

        var baseExists = Exists(repo, baseRef);
        var headExists = Exists(repo, head);
        if (!baseExists || !headExists)
            return new MergeFacts(baseRef, head, baseExists, headExists, 0, 0, 0, 0, false, false);

        var (behind, ahead) = Counts(repo, $"{baseRef}...{head}");
        var remoteBase = $"origin/{baseRef}";
        var hasRemote = Exists(repo, remoteBase);
        var baseBehindRemote = hasRemote ? Counts(repo, $"{baseRef}...{remoteBase}").Right : 0;
        var branchBehindRemote = hasRemote ? Counts(repo, $"{remoteBase}...{head}").Left : 0;
        var dirty = Git.Exec(repo, "status", "--porcelain").Output.Trim().Length > 0;

        return new MergeFacts(baseRef, head, true, true, ahead, behind,
            baseBehindRemote, branchBehindRemote, hasRemote, dirty);
    }

    private static bool Exists(string repo, string rev)
        => Git.Exec(repo, "rev-parse", "--verify", "--quiet", rev + "^{commit}").ExitCode == 0;

    private static (int Left, int Right) Counts(string repo, string range)
    {
        var r = Git.Exec(repo, "rev-list", "--left-right", "--count", range);
        if (r.ExitCode != 0) return (0, 0);
        var parts = r.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r2)
            ? (l, r2)
            : (0, 0);
    }

    // ---- 2. changelog ----------------------------------------------------------------------

    /// <summary>Runbook section 2. The verdict is <c>tools/changelog-section.sh</c>'s own exit code,
    /// run for real — not a regex over the file that agrees with itself. That script is what
    /// <c>release.yml</c> runs as the first job of a tag build and its stdout IS the release body, so
    /// asking it here asks the same question the tag will ask.</summary>
    internal static ChangelogFacts ProbeChangelog(string repo, string? version)
    {
        var path = Path.Combine(repo, "CHANGELOG.md");
        var exists = File.Exists(path);
        var headings = exists
            ? File.ReadLines(path).Where(l => l.StartsWith("## [", StringComparison.Ordinal)).Take(12).ToList()
            : [];

        if (!exists || string.IsNullOrWhiteSpace(version))
            return new ChangelogFacts(version, exists, headings, ScriptRan: false, 0, [], "");

        var script = Path.Combine(repo, "tools", "changelog-section.sh");
        if (!File.Exists(script))
            return new ChangelogFacts(version, true, headings, ScriptRan: false, 0, [],
                $"{script} does not exist - release.yml runs it on every tag build");

        // MEASURED at CH4.1: on Windows `sh` is Git's, in a directory that is NOT on the Windows PATH,
        // so starting it by bare name fails — and ProcessRunner reports a failure to START as exit -1
        // with the reason on STDOUT. Read naively, that renders as "the script says there is no
        // section", which is a verdict about the CHANGELOG derived from a shell that never ran. That
        // substitution — could-not-measure printing as measured — is the exact family of failure this
        // whole era exists to remove, so the shell is resolved explicitly and a missing one is said.
        var shell = ResolveShell();
        if (shell is null)
            return new ChangelogFacts(version, true, headings, ScriptRan: false, 0, [],
                "no POSIX shell was found (sh, bash, or Git's usr/bin/sh) - the section could not be " +
                "measured, which is NOT the same as the section being absent");

        try
        {
            var r = ProcessRunner.Run(shell, ["tools/changelog-section.sh", version.Trim()], repo, ProbeTimeout);
            if (r.TimedOut || (r.ExitCode == -1 && r.Output.StartsWith("failed to start", StringComparison.Ordinal)))
                return new ChangelogFacts(version, true, headings, ScriptRan: false, 0, [],
                    r.TimedOut ? $"{shell} did not finish within {ProbeTimeout.TotalSeconds:0}s" : r.Output.Trim());

            var body = r.Output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            return new ChangelogFacts(version, true, headings, ScriptRan: true, r.ExitCode, body, r.StdErr.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new ChangelogFacts(version, true, headings, ScriptRan: false, 0, [], ex.Message);
        }
    }

    /// <summary>A POSIX shell that can run <c>tools/changelog-section.sh</c>. PATH first, then Git's
    /// bundled one — <c>&lt;git&gt;/cmd/git.exe</c> and <c>&lt;git&gt;/bin/git.exe</c> both sit one
    /// directory below the install root that carries <c>usr/bin/sh.exe</c>, and that is where a
    /// Windows machine's only <c>sh</c> normally is.</summary>
    private static string? ResolveShell()
    {
        foreach (var name in new[] { "sh", "bash" })
            if (DoctorCommand.ResolveOnPath(name) is { Length: > 0 } found)
                return found;

        if (DoctorCommand.ResolveOnPath("git") is not { Length: > 0 } git) return null;
        var root = Path.GetDirectoryName(Path.GetDirectoryName(git));
        if (root is not { Length: > 0 }) return null;
        foreach (var candidate in new[] { Path.Combine(root, "usr", "bin", "sh.exe"), Path.Combine(root, "bin", "sh.exe") })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    // ---- 3. processes ----------------------------------------------------------------------

    /// <summary>Runbook section 3. The subject is the INSTALLED binary — the file
    /// <c>tools/install.ps1</c> would overwrite — not this process's image, which under
    /// <c>dotnet run</c> is a build output nobody executes twice. Same detector
    /// <c>conductor update</c> refuses on, so the two verbs cannot disagree.</summary>
    internal static ProcessFacts ProbeProcesses(PlanConfig plan, string planPath, EngineStampProbe installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        // MEASURED at CH4.1: `conductor` on PATH here is C:\Users\shahi\scoop\shims\conductor.CMD, a
        // shim. UpdateSafety's process-image half compares MainModule to the path it is handed, and no
        // process ever executes a .CMD - so handing it the shim makes that whole detector silently
        // blind, leaving only the engine lock. `version --json` reports WHICH FILE ANSWERED, which is
        // the real exe and the file the reinstall overwrites.
        var target = installed.Binary is { Length: > 0 } b ? b
            : DoctorCommand.ResolveOnPath("conductor") ?? BuildInfo.BinaryPath;
        var live = LiveEngines();
        var blockers = UpdateSafety.Blockers(target, StateDirsFor(plan, planPath));

        var pid = Environment.GetEnvironmentVariable("CONDUCTOR_PID");
        int? conductorPid = int.TryParse(pid, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p2) ? p2 : null;
        return new ProcessFacts(blockers, live, conductorPid);
    }

    /// <summary>Every conductor image alive on this machine, by NAME rather than by matching the
    /// installed path. DV7.3 listed pids and command lines by hand precisely because the path match
    /// is not enough: <c>MainModule</c> is refused for a process owned by another user or running
    /// elevated, and a run started from a different install prefix — the other repository's, on the
    /// other account (trap 3) — would be missed by an equality test and still be broken by the swap.
    /// Best effort on the path, never on the existence.</summary>
    private static IReadOnlyList<LiveEngine> LiveEngines()
    {
        var found = new List<LiveEngine>();
        foreach (var name in new[] { "conductor", "conductor-face" })
        {
            System.Diagnostics.Process[] candidates;
            try { candidates = System.Diagnostics.Process.GetProcessesByName(name); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            {
                continue;
            }
            foreach (var p in candidates)
            {
                try
                {
                    if (p.Id == Environment.ProcessId) continue;
                    string path;
                    try { path = p.MainModule?.FileName ?? $"{name} (path unreadable)"; }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                    {
                        path = $"{name} (path unreadable - another user, or elevated)";
                    }
                    found.Add(new LiveEngine(p.Id, path));
                }
                finally { p.Dispose(); }
            }
        }
        return [.. found.OrderBy(e => e.Pid)];
    }

    /// <summary>The state directories a live-run lock could be in, DEDUPED. The same
    /// <c>.conductor</c> reached through the current directory and through the plan's <c>repo</c>
    /// differs only in the casing of the drive, and reporting one live run twice reads as two.</summary>
    private static IEnumerable<string> StateDirsFor(PlanConfig plan, string planPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in Candidates())
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string full;
            try { full = Path.GetFullPath(Path.Combine(root, StateHome.ScratchDirName)); }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException) { continue; }
            if (seen.Add(full)) yield return full;
        }

        IEnumerable<string> Candidates()
        {
            yield return Directory.GetCurrentDirectory();
            if (Directory.Exists(plan.Repo)) yield return plan.Repo;
            var beside = Path.GetDirectoryName(Path.GetFullPath(planPath));
            if (beside is { Length: > 0 }) yield return beside;
        }
    }

    // ---- 4. migration ----------------------------------------------------------------------

    /// <summary>Runbook section 3's second half — trap 18. Two independent readings: which migration
    /// FILES landed since the commit the installed engine was built from, and what schema the live
    /// store is actually at. The first is the one that predicts the damage, because the store is
    /// migrated forward by whichever binary opens it for write first.</summary>
    internal static MigrationFacts ProbeMigration(string repo, string? storePath, EngineStampProbe installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        var tree = MigrationRunner.CurrentVersion;
        var (sha, version, dirty, _) = installed;

        var since = new List<string>();
        if (sha is { Length: > 0 })
        {
            var r = Git.Exec(repo, "log", "--format=", "--name-only", $"{sha}..HEAD", "--", MigrationsDir);
            if (r.ExitCode == 0)
                since = [.. r.Output.Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(l => l, StringComparer.Ordinal)];
        }

        long? schema = null;
        if (storePath is { Length: > 0 } && RunArchive.TryOpen(storePath) is { } archive)
        {
            try
            {
                var rows = archive.Query("SELECT version FROM schema_version LIMIT 1");
                if (rows.Count > 0 && rows[0].TryGetValue("version", out var v) && v is not null)
                    schema = Convert.ToInt64(v, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidCastException or FormatException)
            {
                // an unreadable schema row is reported as "unreadable", not as a crash
            }
        }

        return new MigrationFacts(tree, sha, version, dirty, since, schema, storePath);
    }

    /// <summary>Ask the engine on PATH what it is. Through <c>version --json</c> rather than through
    /// this process's own <c>BuildInfo</c>: under <c>dotnet run</c> those are two different binaries,
    /// and the one the reinstall replaces is the one on PATH.</summary>
    internal static EngineStampProbe InstalledStamp(string repo)
    {
        var exe = DoctorCommand.ResolveOnPath("conductor");
        if (exe is null or { Length: 0 }) return new EngineStampProbe(null, null, false, null);
        try
        {
            var r = ProcessRunner.Run(exe, ["version", "--json"], repo, ProbeTimeout);
            if (r.ExitCode != 0 || r.Output.Trim().Length == 0) return new EngineStampProbe(null, null, false, null);
            using var doc = JsonDocument.Parse(r.Output);
            var dirty = false;
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (string.Equals(prop.Name, "dirty", StringComparison.OrdinalIgnoreCase))
                    dirty = prop.Value.ValueKind == JsonValueKind.True;
            return new EngineStampProbe(
                Str(doc.RootElement, "commit"), Str(doc.RootElement, "version"), dirty, Str(doc.RootElement, "binary"));
        }
        catch (Exception ex) when (ex is JsonException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new EngineStampProbe(null, null, false, null);
        }
    }

    /// <summary>Case-insensitive property read: the version report is serialised through a source
    /// generated context, and which casing it emits is not this verb's business to depend on.</summary>
    private static string? Str(JsonElement root, string name)
    {
        foreach (var prop in root.EnumerateObject())
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                return prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
        return null;
    }

    // ---- 5. courier ------------------------------------------------------------------------

    /// <summary>Runbook section 4, and the one probe with a rule attached: <b>it may not dial
    /// Telegram</b>. Telegram allows one <c>getUpdates</c> consumer per token and the real courier
    /// owns it, so a preflight that "checked the token works" would starve the live daemon and cost
    /// the owner every notification the run sends. The scheduler, the presence file and the settings
    /// file answer everything this line needs.</summary>
    internal static async Task<CourierFacts> ProbeCourierAsync(PlanConfig plan)
    {
        var settings = CourierSettings.Load();
        var token = Environment.GetEnvironmentVariable(TelegramCourierSource.TokenEnvVar)?.Trim();
        var scope = PersistedScope(TelegramCourierSource.TokenEnvVar);

        CourierTaskState state;
        try
        {
            state = await new CourierTask().StateAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            state = new CourierTaskState(CourierTask.DefaultName, Registered: false, SchedulerState: ex.Message, Running: null);
        }

        var allowed = settings.Projects.Any(p => SamePath(p.Repo, plan.Repo));
        return new CourierFacts(
            TokenSet: token is { Length: > 0 },
            PersistedScope: scope,
            TaskRegistered: state.Registered,
            SchedulerState: state.SchedulerState,
            Running: state.Running is not null,
            Pid: state.Running?.Pid,
            Chats: settings.Chats.Count,
            Projects: settings.Projects.Count,
            RepoAllowed: allowed);
    }

    /// <summary>Where a logon-triggered Scheduled Task would find the token. A task inherits
    /// PERSISTED user and machine variables, never whatever a shell happened to export — which is why
    /// DV7.3 measured this by hand and found it was the one thing that could have gone wrong. Only
    /// Windows has the distinction; elsewhere the process environment is the whole story.</summary>
    private static string? PersistedScope(string name)
    {
        if (!OperatingSystem.IsWindows())
            return Environment.GetEnvironmentVariable(name) is { Length: > 0 } ? "process" : null;
        if (Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine) is { Length: > 0 })
            return "Machine";
        return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) is { Length: > 0 } ? "User" : null;
    }

    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

    // ---- 6. backfill -----------------------------------------------------------------------

    /// <summary>Runbook section 5. "Owed a record" is one join: a run that has finished, and zero
    /// rows in <c>github_map</c> for the destination. Read-only throughout — <see cref="RunArchive"/>
    /// opens <c>Mode=ReadOnly</c> and creates no WAL sidecars, so asking cannot migrate the store
    /// that the driving engine is holding (trap 18).</summary>
    internal static BackfillFacts ProbeBackfill(PlanConfig plan, string? repoOverride, string? storePath)
    {
        var destination = !string.IsNullOrWhiteSpace(repoOverride)
            ? repoOverride.Trim()
            : plan.Github is { Enabled: true } ? GithubIdentity.Resolve(plan) : null;

        if (storePath is null or { Length: 0 } || RunArchive.TryOpen(storePath) is not { } archive)
            return new BackfillFacts(destination, [], null);

        // KS1.6 — `runs.status` is a snapshot column, not a fact: an engine that was killed never got
        // to correct its row, and four runs on this machine say `running` for ever. Reconciled once
        // per store (liveness is a property of the store, not of a row) and the row's own claim is
        // kept beside it, because "the row says running" and "a run is running" are different facts.
        var storeLooksLive = RunLiveness.StoreLooksLive(storePath, plan.Repo);

        // KS1.6 again, structurally: the run list comes through the archive's own sanctioned door
        // (ArchivedRun) rather than through SQL this file writes against `runs`. A join here would
        // have been one statement shorter and would have put a fifth reader of a snapshot column in
        // the repo. The mirror count is a separate read because `github_map` is not a view of
        // anything - it is the local record of what this engine actually created.
        var mirrored = MirrorCounts(archive);

        var runs = archive.Runs()
            .Where(r => r.RunId.Length > 0)
            .Select(r => new MirroredRun(
                r.RunId,
                r.PlanName,
                RunLiveness.Reconcile(r.Status, storeLooksLive),
                mirrored.TryGetValue(r.RunId, out var n) ? n : 0,
                InFlight: RunLiveness.IsStillGoing(r.Status, storeLooksLive))
            { StoredStatus = r.Status })
            .ToList();

        return new BackfillFacts(destination, runs, null);
    }

    /// <summary>How many issues this engine has recorded creating, per run. A store older than schema
    /// v14 has no <c>github_map</c> at all — that is "nothing is recorded", not an error, and
    /// certainly not a reason to take the whole preflight down.</summary>
    private static Dictionary<string, int> MirrorCounts(RunArchive archive)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            foreach (var row in archive.Query("SELECT run_id, COUNT(*) AS n FROM github_map GROUP BY run_id"))
            {
                var id = row.TryGetValue("run_id", out var i) ? i?.ToString() ?? "" : "";
                if (id.Length == 0) continue;
                counts[id] = row.TryGetValue("n", out var n) && n is not null
                    ? (int)Convert.ToInt64(n, CultureInfo.InvariantCulture)
                    : 0;
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // no github_map on this schema
        }
        return counts;
    }

    /// <summary>Where this plan's run.db lives, WITHOUT creating or migrating anything —
    /// <c>StateHome.Peek</c> is the zero-side-effect twin of <c>Resolve</c>.</summary>
    private static string? PeekStore(PlanConfig plan, string planPath)
    {
        try
        {
            var path = StateHome.Peek(plan.Repo, plan.Name).RunDbPath;
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _ = planPath;
            return null;
        }
    }
}
