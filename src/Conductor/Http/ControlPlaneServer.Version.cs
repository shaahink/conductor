using Conductor.Core;
using Conductor.Core.Http;
using System.Net;

namespace Conductor.Http;

/// <summary>
/// SC8.1 — <c>GET /version</c>. The CLI verb answers for the binary you just typed; this answers for
/// the engine that is actually SERVING the run, which is the one the question is usually about. The
/// Face, a curl, and a future <c>conductor update</c> all need it from the outside, and a running
/// engine is precisely the case where you cannot ask its binary directly.
/// <para>Unauthenticated, like every other GET here: the build identity is not a secret, and gating
/// it behind the run token would make it useless for the "which engine is this?" question asked from
/// a shell that does not have the token yet.</para>
/// </summary>
public sealed partial class ControlPlaneServer
{
    private static async Task WriteVersionAsync(HttpListenerContext ctx)
    {
        await WriteJsonAsync(ctx, VersionReport.Current(),
            VersionJsonContext.Default.VersionReport).ConfigureAwait(false);
    }
}
