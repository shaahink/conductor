namespace Conductor.Core.Http;

/// <summary>
/// Where a run advertises its control plane. The file is the handshake every out-of-process client uses:
/// the Face, <c>conductor face</c>, the detached-run watcher, and <see cref="Conductor.Core.Fleet"/>'s
/// scan of OTHER repos' runs.
/// </summary>
/// <remarks>
/// K2.1: this was a static on <c>ControlPlaneServer</c>, which is HTTP hosting and therefore lives in the
/// CLI assembly now. The fleet scan is core code and must still find the file, so the CONVENTION (a path
/// under the state dir) is core and the SERVER that writes it is not. Splitting them here is what let the
/// hosting move out without core reaching back up for it.
/// </remarks>
public static class ControlPlaneDiscovery
{
    /// <summary>The discovery file for a run whose state dir is <paramref name="stateDir"/>.</summary>
    public static string PathFor(string stateDir) => Path.Combine(stateDir, "control-plane.json");
}
