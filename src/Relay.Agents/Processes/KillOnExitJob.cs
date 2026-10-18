using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Relay.Agents.Processes;

/// <summary>
/// A Windows job object that kills every process in it once its last handle is closed. Only Relay
/// holds that handle, so the operating system closes it however Relay ends, a forced kill included,
/// and the agents go with it. Relay puts itself in the job, so every process it starts, and every
/// process those start, is in the job from its first instruction: there is no moment after a start
/// in which a process could start another outside it. Only a process started with an explicit request
/// to leave the job is outside it, as it would be if Relay had no job.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class KillOnExitJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitBreakawayOk = 0x800;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly Lock Gate = new();
    private static SafeFileHandle? _job;

    /// <summary>Puts Relay in the job, once; the processes it starts from then on are in it too.</summary>
    /// <remarks>A failure is not remembered, so the next agent tries again.</remarks>
    public static void Join()
    {
        lock (Gate)
            _job ??= Create();
    }

    private static SafeFileHandle Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        try
        {
            if (job.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            // Without breakaway allowed, a tool that starts its child outside its own job (some
            // launchers and installers do) would fail with access denied inside Relay.
            var limits = new JobObjectExtendedLimits { BasicLimits = { LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitBreakawayOk } };
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JobObjectExtendedLimits>()))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!AssignProcessToJobObject(job, GetCurrentProcess()))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimits
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
    private struct JobObjectExtendedLimits
    {
        public JobObjectBasicLimits BasicLimits;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobObjectExtendedLimits info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
