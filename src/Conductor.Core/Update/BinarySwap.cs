using System.Diagnostics;

namespace Conductor.Core.Update;

/// <summary>What one file replacement did. <c>Retired</c> is the old file's parked path when it
/// could not be deleted — a running Windows image can be renamed but not removed.</summary>
/// <param name="Ok">False means the destination is untouched (or was restored).</param>
/// <param name="Detail">One sentence naming what happened, including the rollback if there was one.</param>
/// <param name="Retired">Path the previous binary was parked at and still occupies, or null.</param>
public sealed record SwapResult(bool Ok, string Detail, string? Retired);

/// <summary>
/// SC8.3 — the rename dance, which is the only way to replace a binary that is currently executing.
///
/// <para>Windows will not let you delete or overwrite the image file of a running process, but it
/// WILL let you rename it: the mapped section keeps pointing at the same inode under its new name.
/// So the old engine steps aside to <c>conductor.exe.old</c>, the new one takes the vacated name, and
/// the old file is deleted afterwards — which fails, harmlessly, if it is still the running process.
/// It is then swept on the next update. POSIX needs none of this, but runs the same path: one code
/// path that works everywhere beats two that differ where nobody tests.</para>
///
/// <para><b>Rollback is the point.</b> Between the rename and the copy, the destination name does not
/// exist — a crash there would leave the operator with no <c>conductor</c> at all. Every failure
/// after the rename moves the old file back before returning.</para>
/// </summary>
public static class BinarySwap
{
    /// <summary>Suffix the outgoing binary is parked under.</summary>
    public const string RetiredSuffix = ".old";

    /// <summary>Replaces <paramref name="destination"/> with <paramref name="replacement"/>, keeping
    /// the outgoing file recoverable until the new one is in place.</summary>
    public static SwapResult Replace(string destination, string replacement)
    {
        if (!File.Exists(replacement))
            return new SwapResult(false, $"nothing to install: {replacement} is not there", null);

        // Nothing to step aside for — a face that was never installed beside the engine, say.
        if (!File.Exists(destination))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(replacement, destination, overwrite: false);
                ArchiveUnpacker.MakeExecutable(destination);
                return new SwapResult(true, $"installed {Path.GetFileName(destination)} (was not present)", null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new SwapResult(false, $"could not write {destination}: {ex.Message}", null);
            }
        }

        var retired = ChooseRetiredPath(destination);
        try { File.Move(destination, retired); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SwapResult(false,
                $"could not move {Path.GetFileName(destination)} aside: {ex.Message} " +
                "(the install directory may need elevation, or another process holds it)", null);
        }

        try
        {
            File.Copy(replacement, destination, overwrite: false);
            ArchiveUnpacker.MakeExecutable(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var rolledBack = TryRestore(retired, destination);
            return new SwapResult(false,
                $"could not install {Path.GetFileName(destination)}: {ex.Message} — " +
                (rolledBack
                    ? "the previous binary was put back, nothing changed"
                    : $"AND THE ROLLBACK FAILED; the previous binary is at {retired}, move it back by hand"),
                rolledBack ? null : retired);
        }

        return TryDelete(retired)
            ? new SwapResult(true, $"replaced {Path.GetFileName(destination)}", null)
            : new SwapResult(true,
                $"replaced {Path.GetFileName(destination)}; the previous one is parked at " +
                $"{Path.GetFileName(retired)} (it is still running, or locked) and is swept on the next update",
                retired);
    }

    /// <summary>Deletes the parked binaries a previous update could not remove. Returns how many went.</summary>
    public static int SweepRetired(string directory)
    {
        var swept = 0;
        try
        {
            // Enumerated wholesale and filtered in code, NEVER by glob: Windows still matches file
            // patterns with DOS 8.3 semantics, where a trailing `.*` behaves nothing like it reads —
            // `x.old.*` matches `x.old` and misses `x.old.4123`. A substring test has no such folklore.
            foreach (var f in Directory.EnumerateFiles(directory))
            {
                if (!Path.GetFileName(f).Contains(RetiredSuffix, StringComparison.Ordinal)) continue;
                if (TryDelete(f)) swept++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return swept;
    }

    /// <summary>Asks a candidate binary what it is, by running it. This is the verification that
    /// actually matters: a checksum proves the bytes arrived intact, and only executing the thing
    /// proves the bytes are a conductor of the version the release claims. A generous timeout,
    /// because a self-contained single-file build unpacks its native libraries on first run.</summary>
    public static (bool Ok, string Output) AskVersion(string exePath, TimeSpan timeout)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(exePath, "version --short")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? Directory.GetCurrentDirectory(),
            });
            if (proc is null) return (false, "could not start it");

            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { proc.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                return (false, $"it did not answer `version --short` within {timeout.TotalSeconds:0}s");
            }
            var output = stdout.GetAwaiter().GetResult().Trim();
            var error = stderr.GetAwaiter().GetResult().Trim();
            return proc.ExitCode == 0 && output.Length > 0
                ? (true, output)
                : (false, $"exit {proc.ExitCode}: {(output.Length > 0 ? output : error)}".Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary><c>x.exe.old</c>, or <c>x.exe.old.&lt;pid&gt;</c> when a previous update left one behind
    /// that is still locked. Never reuses an occupied name — that is how a swap loses the rollback.</summary>
    private static string ChooseRetiredPath(string destination)
    {
        var candidate = destination + RetiredSuffix;
        if (!File.Exists(candidate) || TryDelete(candidate)) return candidate;
        return destination + RetiredSuffix + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return !File.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool TryRestore(string retired, string destination)
    {
        try
        {
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(retired, destination);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
