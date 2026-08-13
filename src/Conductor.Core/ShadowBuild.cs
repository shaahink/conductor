using System.Globalization;

namespace Conductor.Core;

/// <summary>
/// KS0.3, bug #16 — a gate must never try to rebuild the engine that is running it.
///
/// <para>Conductor drives its own repository, and a developer engine runs from that repository's own
/// build output. The gate battery then runs <c>dotnet build</c> over the same solution, MSBuild tries
/// to overwrite <c>conductor.exe</c> while it is executing, and the gate fails with
/// <c>The file is locked by: conductor (12345)</c>. That message is worse than the failure: an agent
/// reads it as a stale orphan holding a lock and goes looking for a process to kill — and the process
/// it finds is the run supervising it. SF0.3 fixed the agent's half by naming <c>CONDUCTOR_PID</c>;
/// this is the engine's half.</para>
///
/// <para>The fix is to move the build, not to explain the crash: when the running image sits inside
/// the tree a gate builds, the gate is redirected to a shadow artifacts path outside that tree, so the
/// build proves the code compiles without ever touching the image it is running from. A command that
/// cannot be redirected safely — a shell chain, a non-dotnet build, one that already chose its own
/// output — is left exactly as written and the operator is told what is about to happen instead.</para>
///
/// <para>Pure and injectable (the running image and the shadow root are parameters, not ambient), so
/// the rule is unit-testable without a self-hosting engine.</para>
/// </summary>
public static class ShadowBuild
{
    /// <param name="Command">What the gate should actually run — unchanged when
    /// <paramref name="Rewritten"/> is false.</param>
    /// <param name="Why">One line for the gate log. Always worth printing: either the redirect or the
    /// warning that the lock error, if it comes, is this engine and not an orphan.</param>
    /// <param name="Rewritten">The command was redirected to the shadow path.</param>
    public sealed record Redirect(string Command, string Why, bool Rewritten);

    /// <summary>Build verbs that write assemblies into a project's output — the ones that can collide
    /// with a running image. <c>dotnet run</c> is deliberately absent: a gate that runs something is
    /// not a build, and redirecting it would change what it runs.</summary>
    private static readonly string[] BuildVerbs = ["build", "test", "publish", "pack", "msbuild"];

    /// <summary>Ways a command has already chosen where its output goes. Redirecting on top of one of
    /// these would silently change what the gate was asked to do.</summary>
    private static readonly string[] OutputAlreadyChosen =
        ["--artifacts-path", "--output", "-o ", "baseoutputpath", "artifactspath", "outputpath"];

    /// <summary>Anything that makes the command more than one program.</summary>
    private static readonly char[] ShellPunctuation = ['&', '|', ';', '>', '<'];

    /// <summary>Null when there is nothing to say: the usual case, where the engine was installed
    /// somewhere else entirely and the gate can build whatever it likes.</summary>
    public static Redirect? For(string? command, string treeRoot, string? runningImage, string shadowRoot)
    {
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(runningImage)) return null;
        if (string.IsNullOrWhiteSpace(treeRoot) || !IsUnder(runningImage, treeRoot)) return null;

        var trimmed = command.Trim();
        if (!CanRedirect(trimmed))
            return new Redirect(command, Rewritten: false, Why:
                $"WARNING: this engine is running from {runningImage}, inside the tree this gate " +
                "builds, and the command cannot be redirected automatically. If it fails with " +
                "\"locked by: conductor\", that is THIS run holding its own image - not a stale " +
                "orphan to kill. Add --artifacts-path to the gate, or run the engine from an " +
                "installed copy.");

        return new Redirect(
            $"{trimmed} --artifacts-path \"{shadowRoot}\"",
            $"this engine is running from {runningImage}, inside the tree this gate builds - " +
            $"building to {shadowRoot} instead, so the gate cannot fail overwriting its own running " +
            "image (bug #16)",
            Rewritten: true);
    }

    /// <summary>Where a tree's shadow build lives: outside the tree, so it can never be an input to
    /// the build it holds the output of, and stable per tree, so the build stays incremental.</summary>
    public static string RootFor(string treeRoot)
    {
        var key = Path.GetFullPath(treeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                      .ToLowerInvariant();
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        var name = Path.GetFileName(key);
        if (string.IsNullOrEmpty(name)) name = "tree";
        return Path.Combine(Path.GetTempPath(), "conductor-gate-build",
                            string.Create(CultureInfo.InvariantCulture, $"{name}-{Convert.ToHexString(hash)[..8].ToLowerInvariant()}"));
    }

    private static bool CanRedirect(string command)
    {
        if (command.IndexOfAny(ShellPunctuation) >= 0) return false;

        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        var tool = Path.GetFileNameWithoutExtension(parts[0]);
        var verb = tool.Equals("msbuild", StringComparison.OrdinalIgnoreCase) ? "msbuild" : parts[1];
        if (!tool.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            && !tool.Equals("msbuild", StringComparison.OrdinalIgnoreCase)) return false;
        if (!BuildVerbs.Contains(verb, StringComparer.OrdinalIgnoreCase)) return false;

        var lower = command.ToLowerInvariant();
        return !OutputAlreadyChosen.Any(o => lower.Contains(o, StringComparison.Ordinal));
    }

    /// <summary>Path containment, resolved and case-insensitively — a false positive costs one
    /// redirected build, a false negative costs the failure this class exists to prevent.</summary>
    private static bool IsUnder(string child, string parent)
    {
        try
        {
            var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
            return Path.GetFullPath(child).StartsWith(p, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}
