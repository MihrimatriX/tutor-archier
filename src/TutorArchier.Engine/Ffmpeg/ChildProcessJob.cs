using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TutorArchier.Engine.Ffmpeg;

/// <summary>
/// Ties ffmpeg processes to this app's lifetime with a Windows job object (KILL_ON_JOB_CLOSE).
/// Without it a crash or a Task Manager kill leaves encodes running — a looping benchmark or a
/// multi-hour x265 job would keep the CPU/GPU busy with nobody to stop it.
/// The job handle is intentionally never closed: Windows closes it when the process exits,
/// which is exactly when the children must die.
/// </summary>
internal static class ChildProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly Lazy<nint> Job = new(CreateJob);

    /// <summary>Adds the process to the job. No-op off Windows; failures are reported, never thrown.</summary>
    public static bool TryTrack(Process process, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindows())
            return false;

        var job = Job.Value;
        if (job == 0)
        {
            error = "job object unavailable";
            return false;
        }

        try
        {
            if (AssignProcessToJobObject(job, process.Handle))
                return true;
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // The process already exited.
            error = ex.Message;
        }
        return false;
    }

    internal static bool IsTracked(Process process) =>
        OperatingSystem.IsWindows()
        && Job.Value != 0
        && IsProcessInJob(process.Handle, Job.Value, out var inJob)
        && inJob;

    private static nint CreateJob()
    {
        if (!OperatingSystem.IsWindows())
            return 0;

        var job = CreateJobObject(0, null);
        if (job == 0)
            return 0;

        var info = new JobObjectExtendedLimitInformationData
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformationData>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, pointer, fDeleteOld: false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, pointer, (uint)size))
            {
                CloseHandle(job);
                return 0;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
        return job;
    }

#pragma warning disable SYSLIB1054 // DllImport keeps this file free of unsafe code / source-generated marshalling.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(nint process, nint job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
#pragma warning restore SYSLIB1054

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
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
    private struct JobObjectExtendedLimitInformationData
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
