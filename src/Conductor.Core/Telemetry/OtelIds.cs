using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Conductor.Core.Telemetry;

/// <summary>
/// KS7.3 — trace and span ids DERIVED from the run rather than drawn at random.
/// </summary>
/// <remarks>
/// An exporter that reads a finished log can be pointed at the same run twice — by an operator
/// re-running the verb, by a retry after a collector refused a batch. With random ids that produces two
/// unrelated traces and a viewer showing the run's history twice; with derived ids the second export is
/// idempotent, and a run exported to two different backends is the same trace id in both.
/// <para>SHA-256 truncated: not a security boundary, just a stable spread. Collision inside one run's
/// span keys would need a 64-bit accident.</para>
/// </remarks>
internal static class OtelIds
{
    /// <summary>16 bytes of hex — the trace, one per run.</summary>
    public static string Trace(string runId) => Hex(Digest("conductor.trace:" + runId), 16);

    /// <summary>8 bytes of hex — one span. <paramref name="key"/> must be unique WITHIN the run.</summary>
    public static string Span(string runId, string key) => Hex(Digest("conductor.span:" + runId + "|" + key), 8);

    private static byte[] Digest(string s) => SHA256.HashData(Encoding.UTF8.GetBytes(s));

    private static string Hex(byte[] bytes, int take)
    {
        var sb = new StringBuilder(take * 2);
        for (var i = 0; i < take; i++) sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        // An all-zero id is "no span" on the wire. Astronomically unlikely; still not left to chance.
        var hex = sb.ToString();
        return hex.All(c => c == '0') ? new string('1', take * 2) : hex;
    }
}
