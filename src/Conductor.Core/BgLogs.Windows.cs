using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Conductor.Core;

/// <summary>SF0.3 (bug #12): the Win32 half of the detach, split out for the same reason
/// <c>DetachedProcess.Windows.cs</c> is — interop bulk must not push policy over the architecture
/// ratchet's line ceiling.</summary>
public static partial class BgLogs
{
    private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_ERROR_HANDLE = -12;
    private const uint HANDLE_FLAG_INHERIT = 0x00000001;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>
    /// Stop this process's own standard handles being inherited by anything it starts from now on.
    ///
    /// <para>Redirecting the child's streams is NOT enough on Windows, which is the part that costs
    /// an afternoon if you reason it out from the ProcessStartInfo alone (measured: with all three
    /// streams redirected, a piped <c>bg start</c> still held the pipe for the child's full 60
    /// seconds). <see cref="System.Diagnostics.Process.Start(System.Diagnostics.ProcessStartInfo)"/>
    /// always calls <c>CreateProcess</c> with <c>bInheritHandles=TRUE</c>, and that hands the child
    /// EVERY inheritable handle this process holds — not merely the ones named in STARTUPINFO. Our
    /// own stdout handle is inheritable whenever the caller gave us a pipe, so a detached grandchild
    /// ends up holding the caller's pipe and the pipe never reaches EOF.</para>
    ///
    /// <para>SC5.2 met the identical bug on <c>conductor run --detach</c> and solved it with
    /// <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>, which needs a raw <c>CreateProcessW</c>. The bg path
    /// deliberately keeps the platform shell (so a <c>.cmd</c>, a shell builtin and PATHEXT
    /// resolution all keep working, and W3.3's "the SHELL writes the log" invariant is untouched), so
    /// it clears the inherit flag at the source instead. Same outcome, no change to how children are
    /// launched.</para>
    ///
    /// <para>Both callers are processes that should never leak a console handle anyway: the <c>bg
    /// start</c> CLI exits milliseconds later, and <c>mcp-serve</c>'s stdout IS its JSON-RPC wire.
    /// Best-effort — a failure here costs a pipe that closes late, never the spawn.</para>
    /// </summary>
    public static void StopLeakingConsoleHandles()
    {
        if (!OperatingSystem.IsWindows()) return; // POSIX: the sh redirect replaces fd 1 before exec
        ClearInheritOnStandardHandles();
    }

    [SupportedOSPlatform("windows")]
    private static void ClearInheritOnStandardHandles()
    {
        foreach (var id in new[] { STD_INPUT_HANDLE, STD_OUTPUT_HANDLE, STD_ERROR_HANDLE })
        {
            var handle = GetStdHandle(id);
            if (handle == IntPtr.Zero || handle == InvalidHandleValue) continue;
            SetHandleInformation(handle, HANDLE_FLAG_INHERIT, 0);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);
}
