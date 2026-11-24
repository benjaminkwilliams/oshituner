using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace oshituner;

internal static partial class PipePeer
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(SafeProcessHandle process, int informationClass,
        out ProcessInformation information, int length, out int returned);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint ExitStatus;
        public nint PebAddress;
        public nint Affinity;
        public nint Priority;
        public nuint ProcessId;
        public nuint ParentProcessId;
    }

    internal static bool WasStartedBy(string expectedExecutable)
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            using var parent = OpenParent(current);
            if (parent is null) return false;
            var executable = parent.MainModule?.FileName;
            if (string.Equals(executable, expectedExecutable, StringComparison.OrdinalIgnoreCase)) return true;
            // Older Chromium builds launch native helpers through the system command interpreter.
            if (!string.Equals(executable, Path.Combine(Environment.SystemDirectory, "cmd.exe"), StringComparison.OrdinalIgnoreCase)
                && !string.Equals(executable, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "cmd.exe"),
                    StringComparison.OrdinalIgnoreCase))
                return false;
            using var ancestor = OpenParent(parent);
            return string.Equals(ancestor?.MainModule?.FileName, expectedExecutable, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or Win32Exception or UnauthorizedAccessException or OverflowException or IOException)
        {
            return false;
        }
    }

    private static Process? OpenParent(Process child)
    {
        if (NtQueryInformationProcess(child.SafeHandle, 0, out var information, Marshal.SizeOf<ProcessInformation>(), out _) != 0)
            return null;
        var parent = Process.GetProcessById(checked((int)information.ParentProcessId));
        try
        {
            if (parent.StartTime <= child.StartTime) return parent;
        }
        catch
        {
            parent.Dispose();
            throw;
        }
        parent.Dispose();
        return null;
    }

    internal static uint? VerifiedServerId(NamedPipeClientStream pipe, string expectedExecutable) =>
        GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var id) && IsExecutable(id, expectedExecutable) ? id : null;

    internal static bool IsServer(NamedPipeClientStream pipe, string expectedExecutable) =>
        VerifiedServerId(pipe, expectedExecutable).HasValue;

    internal static bool IsClient(NamedPipeServerStream pipe, string expectedExecutable) =>
        GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var id) && IsExecutable(id, expectedExecutable);

    internal static bool IsExecutable(uint processId, string expectedExecutable)
    {
        try
        {
            using var peer = Process.GetProcessById(checked((int)processId));
            return string.Equals(peer.MainModule?.FileName, expectedExecutable, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or Win32Exception or UnauthorizedAccessException or OverflowException or IOException)
        {
            return false;
        }
    }
}
