using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Conductor.Core.Courier;

/// <summary>DV4.3 / findings §6.5 — the per-install shared secret a run proves itself with.
///
/// <para>ADR-0005's argument does not stop applying because the port is new. A loopback listener
/// with no auth means any local process — a browser tab, a shell script, anything the owner ran once
/// — can push to the owner's chat AS the run, or read the notes coming back. The control plane
/// already carries this shape (a random token in a file the client reads); the difference is
/// lifetime: the control plane's token is per RUN and lives in that run's state dir, this one is per
/// INSTALL and lives in the state home, because the courier outlives every run and the client is a
/// run that has not started yet.</para>
///
/// <para><b>The file is the boundary, so the file is protected.</b> A secret in a world-readable
/// file is a secret that has been published to every process running as anybody on this machine.
/// The file is created empty, locked down, and only then written — the obvious order is the wrong
/// one, because writing first leaves a window in which the bytes are readable by all.</para></summary>
public static class CourierSecret
{
    /// <summary>The secret in force, creating and protecting one the first time it is asked for.
    /// Idempotent: an install has exactly one secret and a second caller gets the same string.</summary>
    public static string Resolve(string? stateHomeRoot = null)
    {
        if (Read(stateHomeRoot) is { Length: > 0 } existing) return existing;

        var path = CourierHome.SecretPathFor(stateHomeRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Protect(path);
            stream.Write(Encoding.UTF8.GetBytes(secret));
        }

        return secret;
    }

    /// <summary>The secret as written, or null when this install has none. Never throws: a run that
    /// cannot read the secret has no courier it can talk to, which is a refusal with a name, not an
    /// exception crossing a channel seam.</summary>
    public static string? Read(string? stateHomeRoot = null)
    {
        var path = CourierHome.SecretPathFor(stateHomeRoot);
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Constant-time comparison, the control plane's rule reused: a length-varying or
    /// early-exit compare on a loopback endpoint is a timing oracle a local process can actually
    /// win.</summary>
    public static bool Matches(string? given, string? expected)
    {
        if (given is not { Length: > 0 } || expected is not { Length: > 0 }) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));
    }

    /// <summary>Locks the file to this account alone — inheritance broken and one rule on Windows,
    /// mode 0600 elsewhere. Best-effort by contract: a filesystem that cannot express it (a network
    /// share, an exotic mount) must not stop the courier answering the phone, and
    /// <see cref="ProtectionComplaint"/> is what says so out loud.</summary>
    public static void Protect(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) ProtectWindows(path);
            else ProtectUnix(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or PlatformNotSupportedException or InvalidOperationException)
        {
            // Reported by ProtectionComplaint, not thrown: see the summary.
        }
    }

    /// <summary>Why this install's secret is readable by somebody it should not be, or null. What a
    /// test and <c>courier status</c> both assert on — "we called Protect" is a claim, and the whole
    /// point of §6.5 is that the file, not the call, is the boundary.</summary>
    public static string? ProtectionComplaint(string? stateHomeRoot = null)
    {
        var path = CourierHome.SecretPathFor(stateHomeRoot);
        if (!File.Exists(path)) return null;

        try
        {
            return OperatingSystem.IsWindows() ? WindowsComplaint(path) : UnixComplaint(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or PlatformNotSupportedException or InvalidOperationException)
        {
            return $"the courier secret at {path} could not be inspected ({ex.Message}); "
                 + "treat it as readable by anything running as you.";
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void ProtectUnix(string path) =>
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ProtectWindows(string path)
    {
        var me = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("this account has no SID");
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.SetOwner(me);
        acl.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? WindowsComplaint(string path)
    {
        var acl = new FileInfo(path).GetAccessControl();
        if (!acl.AreAccessRulesProtected)
            return $"the courier secret at {path} still inherits its parent's permissions, so "
                 + "whoever can read the state home can read it.";

        var me = WindowsIdentity.GetCurrent().User?.Value;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (string.Equals(rule.IdentityReference.Value, me, StringComparison.Ordinal)) continue;
            return $"the courier secret at {path} grants access to {rule.IdentityReference.Value}, "
                 + "which is not this account.";
        }

        return null;
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static string? UnixComplaint(string path)
    {
        var mode = File.GetUnixFileMode(path);
        const UnixFileMode Others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                  | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        return (mode & Others) == UnixFileMode.None
            ? null
            : $"the courier secret at {path} is mode {mode}; it has to be readable by this user only.";
    }
}
