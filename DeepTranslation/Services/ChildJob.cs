using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeepTranslation.Services;

/// <summary>
/// 자식 엔진 프로세스를 Job Object에 묶어, 앱이 크래시하거나 강제 종료돼도 OS가 자식을 함께 정리하게 한다
/// (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE — 앱 프로세스가 사라지면 Job 핸들이 닫히며 자식이 죽는다).
/// </summary>
internal static class ChildJob
{
    private static readonly IntPtr Handle = Create();

    public static void Attach(Process proc)
    {
        if (Handle == IntPtr.Zero) return;
        try { AssignProcessToJobObject(Handle, proc.Handle); } catch { } // 실패해도 기존 Stop() 경로는 그대로 동작
    }

    private static IntPtr Create()
    {
        try
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE }
            };
            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, p, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, p, (uint)size))
                    return IntPtr.Zero;
            }
            finally { Marshal.FreeHGlobal(p); }
            return job;
        }
        catch { return IntPtr.Zero; }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount,
                     ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attrs, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
