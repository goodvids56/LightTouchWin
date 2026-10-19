// Starts a device's process with its standard output and error going straight to a log file: the emulator writes its
// diagnostics with the C runtime's stderr, which a GUI process otherwise doesn't have, and the file keeps them however
// long the device outlives the library window.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LightTouch.Native;

internal static partial class ChildProcess
{
    private const int StartfUseStdHandles = 0x100;
    private const uint CreateNoWindow = 0x08000000, CreateUnicodeEnvironment = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcess(string? application, char[] commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment,
        string? currentDirectory, ref StartupInfo startup, out ProcessInformation info);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateFile(string name, uint access, uint share, ref SecurityAttributes security,
        uint disposition, uint attributes, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateJobObject(IntPtr attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr job, int infoClass, ref JobLimits info, int length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    /// A job that kills its processes when this process ends however it ends: the device's usbmuxd never outlives it.
    private static readonly Lazy<IntPtr> CompanionJob = new(() =>
    {
        const int ExtendedLimitInformation = 9;
        const uint KillOnJobClose = 0x2000;
        var job = CreateJobObject(IntPtr.Zero, null);
        var limits = new JobLimits { LimitFlags = KillOnJobClose };
        SetInformationJobObject(job, ExtendedLimitInformation, ref limits, Marshal.SizeOf<JobLimits>());
        return job;
    });

    /// `arguments` quoted as the C runtime parses them. `companion`: the process ends when this one does.
    public static Process Start(string executable, IEnumerable<string> arguments, string log, bool companion = false)
    {
        const uint GenericWrite = 0x40000000, ShareRead = 1, ShareWrite = 2, CreateAlways = 2, Normal = 0x80;
        var security = new SecurityAttributes { nLength = Marshal.SizeOf<SecurityAttributes>(), bInheritHandle = 1 };
        var file = CreateFile(log, GenericWrite, ShareRead | ShareWrite, ref security, CreateAlways, Normal, IntPtr.Zero);
        if (file == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Can't write {log}");
        try
        {
            var startup = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                dwFlags = StartfUseStdHandles,
                hStdOutput = file,
                hStdError = file,
            };
            var line = new StringBuilder(Quote(executable));
            foreach (var a in arguments) line.Append(' ').Append(Quote(a));
            var command = (line + "\0").ToCharArray();
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true, CreateNoWindow | CreateUnicodeEnvironment,
                    IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out var info))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            if (companion) AssignProcessToJobObject(CompanionJob.Value, info.hProcess);
            CloseHandle(info.hThread);
            try { return Process.GetProcessById(info.dwProcessId); }
            finally { CloseHandle(info.hProcess); }
        }
        finally
        {
            CloseHandle(file);
        }
    }

    /// CommandLineToArgvW's rules: backslashes double only before a quote.
    private static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0) return arg;
        var o = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }
            o.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            o.Append(c);
        }
        return o.Append('\\', slashes * 2).Append('"').ToString();
    }
}
