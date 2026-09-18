using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SonettoHere.Launcher.Native;

/// <summary>
/// 进程作业对象。使用 KILL_ON_JOB_CLOSE：启动器一旦退出（含被强杀/崩溃），
/// 归属该 job 的后端、前端及其子孙进程都不会残留成孤儿。
/// 正常关闭时启动器会先优雅停服，job 里已经没有活着的进程。
/// </summary>
internal sealed class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private IntPtr _handle;

    public JobObject(LogBus? log = null)
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
        {
            log?.Write($"[启动器] 创建 Job Object 失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            return;
        }

        var info = new JobObjectExtendedLimitInformationNative
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInformationNative>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                log?.Write($"[启动器] 设置 Job Object 限制失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public bool IsValid => _handle != IntPtr.Zero;

    public void Assign(IntPtr processHandle, LogBus? log = null)
    {
        if (_handle == IntPtr.Zero || processHandle == IntPtr.Zero)
        {
            return;
        }

        if (!AssignProcessToJobObject(_handle, processHandle))
        {
            log?.Write($"[启动器] 将子进程加入 Job Object 失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }
    }

    /// <summary>整树强杀（仅作最后兜底，正常流程走优雅关闭）。</summary>
    public void Terminate()
    {
        if (_handle != IntPtr.Zero)
        {
            TerminateJobObject(_handle, 1);
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
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
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationNative
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
