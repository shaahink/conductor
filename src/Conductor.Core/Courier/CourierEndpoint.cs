using System.Globalization;

namespace Conductor.Core.Courier;

/// <summary>DV4.3 / findings §6.5 — where the courier answers, and on what terms.
///
/// <para><b>Its own named port, never a scan.</b> The control plane scans forward from a preferred
/// port because two runs on one machine are ordinary and neither of them is "the" run. The courier
/// is the opposite: there is exactly ONE per machine by construction — it owns the bot token, and
/// Telegram allows one consumer per token — so a second courier is not a case to accommodate, it is
/// a misconfiguration to refuse by name. A scan would paper over it, and worse: a run that scanned
/// for a courier would attach to whatever answered on the next port up, which is the shape of
/// "any local process can push to the owner's chat as the run" that ADR-0005 exists to prevent.</para>
///
/// <para><b>Loopback only.</b> The prefix is a literal <see cref="Loopback"/>, not <c>+</c> or
/// <c>*</c>: on Windows a wildcard prefix needs an ACL reservation or admin rights, so a wildcard
/// here would also be the difference between a daemon that starts as the logged-on user (DV4.2's
/// whole point) and one that needs elevation.</para>
///
/// <para>The port is a constant with an env override for rigs, and the RUNNING courier records the
/// port it actually bound in its presence record — so a run reads the port from the same file it
/// reads the protocol version from, rather than assuming a rig's override.</para></summary>
public static class CourierEndpoint
{
    /// <summary>The courier's port. Chosen below the Windows dynamic range (49152+) so the OS never
    /// hands it to something else, and clear of the control plane's scan window (4317-4336).</summary>
    public const int DefaultPort = 47137;

    /// <summary>Points a rig at a port of its own. Trap 3's discipline — a second conductor run may
    /// share this machine, so nothing under test may bind the port a real courier would.</summary>
    public const string PortEnvVar = "CONDUCTOR_COURIER_PORT";

    /// <summary>The shared secret's header. Named like the control plane's <c>X-Conductor-Token</c>
    /// and deliberately NOT the same name: they are different secrets with different lifetimes (one
    /// per run, one per install), and a client that muddles them must fail closed, not by accident.</summary>
    public const string AuthHeader = "X-Conductor-Courier";

    /// <summary>The only address the courier ever binds or is dialled on.</summary>
    public const string Loopback = "127.0.0.1";

    /// <summary>The hello: who is running, which protocol, which engine. <see cref="CourierPresence"/>
    /// on the wire — the record DV4.2 already writes, served rather than re-modelled.</summary>
    public const string HelloPath = "/hello";

    /// <summary>A run handing a push to the daemon. POST, and it carries the secret.</summary>
    public const string PushPath = "/push";

    /// <summary>The port in force here: <see cref="PortEnvVar"/> when it names a usable port, else
    /// <see cref="DefaultPort"/>. A junk override is IGNORED rather than fatal — the courier is the
    /// process that must keep answering the phone.</summary>
    public static int Port =>
        int.TryParse(Environment.GetEnvironmentVariable(PortEnvVar), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536
            ? p
            : DefaultPort;

    /// <summary>The base URL of a courier on <paramref name="port"/>.</summary>
    public static string BaseUrl(int port) =>
        "http://" + Loopback + ":" + port.ToString(CultureInfo.InvariantCulture);

    /// <summary>The listener prefix for <paramref name="port"/>. Trailing slash: the hosting API
    /// rejects a prefix without one, and the failure is a runtime throw, not a compile error.</summary>
    public static string PrefixFor(int port) => BaseUrl(port) + "/";

    /// <summary>Why a run will not dial the courier that is running, or null. Two reasons and both
    /// name the fix: no courier at all, and a courier that never opened its socket (a DV4.2-era
    /// daemon, which is exactly the skew <see cref="CourierProtocol"/> was built to catch).</summary>
    /// <param name="courier">The live presence record, or null when none is running.</param>
    public static string? Unreachable(CourierPresence? courier)
    {
        if (courier is null)
            return "no courier is running on this machine. Start one: " + CourierProtocol.RestartVerb;

        if (courier.Port is not > 0)
        {
            var name = string.IsNullOrWhiteSpace(courier.TaskName) ? CourierTask.DefaultName : courier.TaskName;
            return $"the courier \"{name}\" (pid {courier.Pid.ToString(CultureInfo.InvariantCulture)}) "
                 + "is running without a loopback listener, so a run has no way to reach it. "
                 + "Restart it: " + CourierProtocol.RestartVerb;
        }

        return null;
    }
}
