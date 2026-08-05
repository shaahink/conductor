namespace Conductor.Core;

/// <summary>
/// SF6.3 — the analyzer is answered once, not once per console handler.
///
/// <para>MA0045 flags <see cref="CancellationTokenSource.Cancel()"/> and points at
/// <c>CancelAsync()</c>. That advice cannot be taken here: every caller is a SYNCHRONOUS delegate the
/// runtime hands us — <see cref="Console.CancelKeyPress"/>, the Win32 console control handler — and a
/// fire-and-forget <c>CancelAsync()</c> inside one of those is not merely equivalent, it is worse: the
/// process can be torn down before the cancellation has propagated, which is exactly the mid-session
/// data loss the Ctrl+C path exists to prevent.</para>
///
/// <para>So the rule is wrong in this one situation, and this is the one place that says so. It used to
/// be said in three (twice in <c>RunCommand</c>, once in <c>McpServeCommand</c>), one of them carrying
/// the justification "CancelAsync doesn't exist on CancellationTokenSource" — which stopped being true
/// at .NET 8 and would have sent the next reader looking for a fix that was already available and still
/// wrong to use.</para>
/// </summary>
internal static class SyncCancellation
{
    /// <summary>Requests cancellation from a synchronous OS callback.</summary>
    internal static void RequestStop(CancellationTokenSource cts)
    {
        ArgumentNullException.ThrowIfNull(cts);
#pragma warning disable MA0045 // see the type comment: the caller is an OS-supplied sync delegate and cannot await
        cts.Cancel();
#pragma warning restore MA0045
    }
}
