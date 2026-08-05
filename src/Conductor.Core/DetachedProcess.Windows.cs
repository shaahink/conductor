using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Conductor.Core;

/// <summary>SC5.2: the Win32 half of <see cref="DetachedProcess"/>. Split from the cross-platform
/// file so the interop bulk never pushes the policy above the architecture ratchet's line ceiling.
/// Every type here is private, so this file declares none by the ratchet's reckoning.</summary>
public static partial class DetachedProcess
{
    private const uint DETACHED_PROCESS = 0x00000008;
    private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const int ERROR_ACCESS_DENIED = 5;
    private const uint FILE_APPEND_DATA = 0x0004;
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint FILE_SHARE_ALL = 0x00000007; // read | write | delete
    private const uint OPEN_ALWAYS = 4;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = new(0x00020002);
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [SupportedOSPlatform("windows")]
    private static DetachSpawn StartWindows(string fileName, IReadOnlyList<string> args, string wd, string? logPath)
    {
        var commandLine = CommandLine(fileName, args);

        // The log handle is opened through CreateFileW rather than a FileStream for two reasons: the
        // child only inherits a handle that was created INHERITABLE (a SECURITY_ATTRIBUTES flag .NET
        // does not expose), and a raw handle is all STARTUPINFO can take anyway. It must outlive the
        // CreateProcess call and no longer — the child holds its own duplicate from then on.
        var log = INVALID_HANDLE_VALUE;
        var attrList = IntPtr.Zero;
        var handleSlot = IntPtr.Zero;
        try
        {
            var six = new STARTUPINFOEX();
            six.StartupInfo.cb = Marshal.SizeOf<STARTUPINFO>();
            var inheritHandles = false;
            var flags = DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP;
            if (logPath is not null)
            {
                var sa = new SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                    lpSecurityDescriptor = IntPtr.Zero,
                    bInheritHandle = true,
                };
                log = CreateFileW(logPath, FILE_APPEND_DATA | SYNCHRONIZE, FILE_SHARE_ALL,
                    ref sa, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
                // Losing the capture log costs diagnostics, not the run — start it either way.
                if (log != INVALID_HANDLE_VALUE && TryBuildInheritList(log, out attrList, out handleSlot))
                {
                    six.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
                    six.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
                    six.StartupInfo.hStdOutput = log;
                    six.StartupInfo.hStdError = log;
                    six.StartupInfo.hStdInput = IntPtr.Zero; // detached: there is nothing to read from
                    six.lpAttributeList = attrList;
                    flags |= EXTENDED_STARTUPINFO_PRESENT;
                    inheritHandles = true;
                }
            }

            var (ok, err, pi) = TryCreate(commandLine, inheritHandles, flags | CREATE_BREAKAWAY_FROM_JOB, wd, ref six);
            if (ok) return Finish(pi, brokeAway: true);

            // ERROR_ACCESS_DENIED here means "you are in a job object that does not permit breakaway"
            // — nothing was started, so retrying inside the job is safe and strictly better than
            // refusing to detach at all. Any other code is a real failure.
            if (err != ERROR_ACCESS_DENIED)
                return DetachSpawn.Failed($"CreateProcess failed: {new Win32Exception(err).Message} (win32 {err})");

            (ok, err, pi) = TryCreate(commandLine, inheritHandles, flags, wd, ref six);
            return ok
                ? Finish(pi, brokeAway: false)
                : DetachSpawn.Failed($"CreateProcess failed: {new Win32Exception(err).Message} (win32 {err})");
        }
        finally
        {
            if (attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
            if (handleSlot != IntPtr.Zero) Marshal.FreeHGlobal(handleSlot);
            if (log != INVALID_HANDLE_VALUE) CloseHandle(log);
        }
    }

    /// <summary>
    /// Restrict inheritance to exactly one handle — the capture log.
    ///
    /// <para>Found by the live rig, and it cost an hour: <c>bInheritHandles=TRUE</c> hands the child
    /// EVERY inheritable handle this process holds, not merely the ones named in STARTUPINFO. Pipe
    /// <c>conductor run --detach</c> anywhere — <c>| Out-Null</c>, a CI log, any caller that gives it
    /// a pipe for stdout — and the short-lived detach parent's inherited pipe handle is duplicated
    /// into an engine that runs for hours. The pipe never reaches EOF, so the reader blocks forever
    /// and the detach looks hung when it long since succeeded.</para>
    ///
    /// <para>PROC_THREAD_ATTRIBUTE_HANDLE_LIST is the only fix: with it, the child inherits the one
    /// handle in the list and nothing else. False if the list cannot be built — the caller then falls
    /// back to no inheritance at all, which loses the capture log but never hangs anyone.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool TryBuildInheritList(IntPtr handle, out IntPtr attrList, out IntPtr handleSlot)
    {
        attrList = IntPtr.Zero;
        handleSlot = IntPtr.Zero;
        var size = IntPtr.Zero;
        // First call always "fails" with ERROR_INSUFFICIENT_BUFFER; it exists to report the size.
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        if (size == IntPtr.Zero) return false;

        attrList = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref size))
        {
            Marshal.FreeHGlobal(attrList);
            attrList = IntPtr.Zero;
            return false;
        }
        // The array must stay alive until CreateProcess returns — the attribute list stores a
        // pointer to it, not a copy.
        handleSlot = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(handleSlot, handle);
        if (UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handleSlot,
                (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            return true;

        DeleteProcThreadAttributeList(attrList);
        Marshal.FreeHGlobal(attrList);
        Marshal.FreeHGlobal(handleSlot);
        attrList = IntPtr.Zero;
        handleSlot = IntPtr.Zero;
        return false;
    }

    [SupportedOSPlatform("windows")]
    private static (bool Ok, int Error, PROCESS_INFORMATION Pi) TryCreate(
        string commandLine, bool inheritHandles, uint flags, string wd, ref STARTUPINFOEX si)
    {
        // CreateProcessW may WRITE to the command-line buffer, so each attempt gets its own copy.
        var buffer = new StringBuilder(commandLine);
        var ok = CreateProcessW(null, buffer, IntPtr.Zero, IntPtr.Zero, inheritHandles, flags,
            IntPtr.Zero, string.IsNullOrEmpty(wd) ? null : wd, ref si, out var pi);
        return (ok, ok ? 0 : Marshal.GetLastWin32Error(), pi);
    }

    [SupportedOSPlatform("windows")]
    private static DetachSpawn Finish(PROCESS_INFORMATION pi, bool brokeAway)
    {
        // We are not waiting on this process — ever. Holding its handles would only keep a zombie
        // entry alive in our own table; the pid is the whole contract with the caller.
        if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
        if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
        return new DetachSpawn((int)pi.dwProcessId, brokeAway, null);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize,
        IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        ref SECURITY_ATTRIBUTES lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    /// <summary>The whole point of the raw handle: <c>bInheritHandle</c>. A handle the child cannot
    /// inherit is a capture log that stays empty.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    /// <summary>STARTUPINFO plus the attribute list that carries the inherit-handle whitelist.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }
}
