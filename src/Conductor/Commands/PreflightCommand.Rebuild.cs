using System.Globalization;

using Conductor.Core;
using Conductor.Models;

namespace Conductor.Commands;

/// <summary>
/// KS3.4 — the leg nobody ever ran by hand, and the one the field log calls a defect that burned
/// three sessions: <em>is the run about to use stale engine code?</em>
///
/// <para>It has one shape and two ways of arriving at it. A source tree feeding this engine has files
/// that are NEWER than the binary answering here — because the operator fixed something under
/// <c>src/</c> and never rebuilt, or because the fix was rebuilt into a developer image while the
/// <c>conductor</c> that will be typed at the terminal is the installed copy from last week. Either
/// way the run executes code that does not contain the fix, every surface reports the version the
/// binary was stamped with, and nothing anywhere says otherwise.</para>
///
/// <para>Silent on every ordinary repo, by construction: a plan that drives some other project has no
/// engine sources to be newer than anything, and this leg says so rather than inventing a worry.</para>
/// </summary>
public sealed partial class PreflightCommand
{
    /// <summary>The engine binary as facts, so the rule is a function of its arguments and a test can
    /// fabricate a stale one without a stale build.</summary>
    /// <param name="Path">The file that is executing (or would be).</param>
    /// <param name="WriteUtc">Its last-write time — the honest "when did this image appear", which a
    /// copied or published binary carries and a build stamp does not always.</param>
    /// <param name="BuildUtc">The compile-time stamp, used when the file cannot be stat'd.</param>
    /// <param name="CommitSha">The commit it was built from, or <c>unknown</c>.</param>
    /// <param name="Dirty">The tree carried uncommitted changes at build time.</param>
    internal sealed record EngineImage(string Path, DateTimeOffset? WriteUtc, DateTimeOffset? BuildUtc, string CommitSha, bool Dirty)
    {
        internal static EngineImage Running()
        {
            var path = BuildInfo.BinaryPath;
            DateTimeOffset? write = null;
            try
            {
                if (File.Exists(path)) write = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* stamp is enough */ }
            return new EngineImage(path, write, BuildInfo.Current.BuildDate, BuildInfo.Current.CommitSha, BuildInfo.Current.Dirty);
        }

        /// <summary>When this image came into being. The file time first: a published binary copied
        /// onto a machine is exactly as old as the copy, and its build stamp lies about that.</summary>
        internal DateTimeOffset? Stamp => WriteUtc ?? BuildUtc;
    }

    /// <summary>The engine's own project file, relative to the root of its repository. The marker for
    /// "this directory is a tree that builds conductor" — named once, here.</summary>
    internal static readonly string[] EngineProjectRelPath = ["src", "Conductor", "Conductor.csproj"];

    /// <summary>The solution file at the root of the engine's repository.</summary>
    internal const string EngineSolutionFile = "Conductor.slnx";

    /// <summary>Source extensions that change what the engine DOES. A markdown file under
    /// <c>src/</c> does not make a binary stale.</summary>
    private static readonly string[] SourceGlobs = ["*.cs", "*.csproj", "*.slnx"];

    /// <summary>The <c>conductor</c> a hand-typed launch would spawn, or null when nothing is on
    /// PATH.</summary>
    internal static string? PathCopyOfConductor() => DoctorCommand.ResolveOnPath("conductor");

    internal static Leg RebuildLeg(PlanConfig plan, EngineImage image, string? pathBinary)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(image);

        var detail = new List<string>();
        if (image.Dirty)
            detail.Add($"this engine was built from a dirty tree ({image.CommitSha}+uncommitted), so its commit does not identify it");
        if (pathBinary is { Length: > 0 } && !SamePath(pathBinary, image.Path))
            detail.Add($"`conductor` on PATH is {pathBinary}, which is NOT the engine answering here ({image.Path}) — " +
                       "a launch typed as `conductor run` runs that one instead");

        var trees = EngineSourceTrees(plan, image);
        if (trees.Count == 0)
            return new Leg(RebuildLegName, detail.Count > 0 ? "warn" : "ok",
                $"{BuildInfo.Current.Full} — no conductor source tree feeds this plan, so nothing here can make the engine stale",
                detail);

        if (image.Stamp is not { } stamp)
            return new Leg(RebuildLegName, "warn",
                $"{BuildInfo.Current.Full} — this binary carries no build date and could not be stat'd, so staleness is unknown",
                detail);

        var stale = new List<string>();
        foreach (var tree in trees)
        {
            if (NewestSource(tree) is not { } newest) continue;
            if (newest.WrittenUtc <= stamp) continue;
            stale.Add($"in {tree}: {Rel(tree, newest.Path)} was written " +
                      $"{Staleness.Age(newest.WrittenUtc - stamp)} after the engine image");
        }

        if (stale.Count > 0)
        {
            detail.AddRange(stale);
            detail.Add($"rebuild before launching — `dotnet build {EngineSolutionFile}` for a developer image, " +
                       "or reinstall the engine — or the run executes code that does not contain the change");
            return new Leg(RebuildLegName, "fail",
                $"{BuildInfo.Current.Full} at {image.Path} ({Iso(stamp)}) is OLDER than the sources that build it",
                detail);
        }

        return new Leg(RebuildLegName, detail.Count > 0 ? "warn" : "ok",
            $"{BuildInfo.Current.Full} at {image.Path} ({Iso(stamp)}) is at least as new as every source in " +
            $"{trees.Count} tree(s) that build it",
            detail);
    }

    /// <summary>The trees whose sources feed the engine that would run this plan: the repository the
    /// running image sits inside (a developer build), and the plan's own repo when that repo is a
    /// conductor checkout (the self-hosting case, and the one where an installed engine on PATH is a
    /// week behind the fix that was just written).</summary>
    internal static List<string> EngineSourceTrees(PlanConfig plan, EngineImage image)
    {
        var trees = new List<string>();
        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            var full = Path.GetFullPath(dir);
            if (!trees.Exists(t => SamePath(t, full))) trees.Add(full);
        }

        Add(EnclosingEngineTree(Path.GetDirectoryName(image.Path)));
        if (LooksLikeEngineTree(plan.Repo)) Add(plan.Repo);
        return trees;
    }

    /// <summary>Walks up from a directory to the first one that both carries the engine solution and
    /// its project file. Null when the image lives somewhere with no sources beside it — an installed
    /// or published engine, which is the common and healthy case.</summary>
    private static string? EnclosingEngineTree(string? start)
    {
        var dir = string.IsNullOrWhiteSpace(start) ? null : new DirectoryInfo(start);
        for (var i = 0; dir is not null && i < 12; i++, dir = dir.Parent)
            if (LooksLikeEngineTree(dir.FullName)) return dir.FullName;
        return null;
    }

    private static bool LooksLikeEngineTree(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        try
        {
            return File.Exists(Path.Combine(dir, EngineSolutionFile))
                && File.Exists(Path.Combine([dir, .. EngineProjectRelPath]));
        }
        catch (ArgumentException) { return false; }
    }

    /// <summary>The most recently written source under <c>&lt;tree&gt;/src</c>, skipping build output.
    /// Null for a tree with no sources at all.</summary>
    private static (string Path, DateTimeOffset WrittenUtc)? NewestSource(string tree)
    {
        var src = Path.Combine(tree, "src");
        if (!Directory.Exists(src)) return null;
        (string Path, DateTimeOffset WrittenUtc)? newest = null;
        try
        {
            foreach (var glob in SourceGlobs)
            foreach (var file in Directory.EnumerateFiles(src, glob, SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file)) continue;
                var written = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                if (newest is null || written > newest.Value.WrittenUtc) newest = (file, written);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* partial answer is an answer */ }
        return newest;
    }

    private static bool IsBuildOutput(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string a, string b)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), comparison);
        }
        catch (ArgumentException) { return false; }
    }

    private static string Rel(string root, string path)
    {
        try { return Path.GetRelativePath(root, path).Replace('\\', '/'); }
        catch (ArgumentException) { return path; }
    }

    private static string Iso(DateTimeOffset when)
        => when.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
