using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Domain.AI.Sandbox;

namespace Infrastructure.AI.Sandbox;

/// <summary>
/// Manages a Windows Job Object for process resource enforcement via P/Invoke.
/// Disposing closes the Job Object handle, triggering KILL_ON_JOB_CLOSE
/// for all assigned processes.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsJobObjectManager : IDisposable
{
    private IntPtr _jobHandle;
    private bool _disposed;

    public WindowsJobObjectManager()
    {
        _jobHandle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (_jobHandle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>
    /// Configures resource limits on this Job Object.
    /// </summary>
    public void SetLimits(ResourceLimits limits)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();

        info.BasicLimitInformation.LimitFlags =
            NativeMethods.JOB_OBJECT_LIMIT_PROCESS_MEMORY |
            NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE |
            NativeMethods.JOB_OBJECT_LIMIT_ACTIVE_PROCESS;

        info.ProcessMemoryLimit = new UIntPtr((ulong)limits.MemoryLimitBytes);
        info.BasicLimitInformation.ActiveProcessLimit = (uint)limits.MaxSubprocesses;

        if (limits.CpuTimeSeconds > 0)
        {
            info.BasicLimitInformation.LimitFlags |= NativeMethods.JOB_OBJECT_LIMIT_PROCESS_TIME;
            info.BasicLimitInformation.PerProcessUserTimeLimit =
                (long)(limits.CpuTimeSeconds * 10_000_000);
        }

        var size = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!NativeMethods.SetInformationJobObject(
                    _jobHandle,
                    NativeMethods.JobObjectInfoType.ExtendedLimitInformation,
                    ptr, (uint)size))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// Assigns a process to this Job Object.
    /// </summary>
    public void AssignProcess(Process process)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!NativeMethods.AssignProcessToJobObject(_jobHandle, process.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    /// <summary>
    /// Queries CPU time and peak memory from the Job Object accounting information.
    /// </summary>
    public ResourceUsage QueryUsage()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var accounting = QueryAccountingInfo();
        var peakMemory = QueryPeakMemory();

        var totalCpuTicks = accounting.TotalUserTime + accounting.TotalKernelTime;
        var cpuSeconds = totalCpuTicks / 10_000_000.0;

        return new ResourceUsage
        {
            MemoryBytes = peakMemory,
            CpuTimeSeconds = cpuSeconds,
            SubprocessCount = (int)accounting.TotalProcesses
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_jobHandle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_jobHandle);
            _jobHandle = IntPtr.Zero;
        }
    }

    private NativeMethods.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION QueryAccountingInfo()
    {
        var size = (uint)Marshal.SizeOf<NativeMethods.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>();
        var ptr = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!NativeMethods.QueryInformationJobObject(
                    _jobHandle,
                    NativeMethods.JobObjectInfoType.BasicAccountingInformation,
                    ptr, size, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return Marshal.PtrToStructure<NativeMethods.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private long QueryPeakMemory()
    {
        var size = (uint)Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!NativeMethods.QueryInformationJobObject(
                    _jobHandle,
                    NativeMethods.JobObjectInfoType.ExtendedLimitInformation,
                    ptr, size, out _))
            {
                return 0;
            }

            var extInfo = Marshal.PtrToStructure<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(ptr);
            return (long)(ulong)extInfo.PeakJobMemoryUsed;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    internal static class NativeMethods
    {
        internal const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x00000100;
        internal const uint JOB_OBJECT_LIMIT_PROCESS_TIME = 0x00000002;
        internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        internal const uint JOB_OBJECT_LIMIT_ACTIVE_PROCESS = 0x00000008;

        internal enum JobObjectInfoType
        {
            BasicAccountingInformation = 1,
            ExtendedLimitInformation = 9,
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
        {
            public long TotalUserTime;
            public long TotalKernelTime;
            public long ThisPeriodTotalUserTime;
            public long ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount;
            public uint TotalProcesses;
            public uint ActiveProcesses;
            public uint TotalTerminatedProcesses;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            IntPtr hJob, JobObjectInfoType infoType,
            IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObject(
            IntPtr hJob, JobObjectInfoType infoType,
            IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength,
            out uint lpReturnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr hObject);
    }
}
