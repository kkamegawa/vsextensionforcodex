using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Codex.VisualStudio.Extension;

/// <summary>
/// Wraps a Win32 job object configured with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>.
/// Any process assigned to the job - and every child/grandchild process it spawns,
/// since they are implicitly associated with the same job - is terminated as soon
/// as the job handle is closed. This guarantees that the Codex worker, the codex
/// app-server, and any cmd.exe processes it launches are torn down together, even
/// if Visual Studio (and therefore this extension process) is force-closed.
/// </summary>
public sealed partial class ProcessJobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private SafeJobObjectHandle? handle;

    private ProcessJobObject(SafeJobObjectHandle handle)
    {
        this.handle = handle;
    }

    /// <summary>
    /// Creates a new job object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> set.
    /// Returns <see langword="null"/> if the job object could not be created or configured;
    /// callers should treat this as best-effort and continue without it.
    /// </summary>
    public static ProcessJobObject? CreateKillOnCloseJob()
    {
        SafeJobObjectHandle jobHandle = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (jobHandle.IsInvalid)
        {
            return null;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };

        int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr infoPtr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, infoPtr, false);
            if (!NativeMethods.SetInformationJobObject(jobHandle, JobObjectExtendedLimitInformation, infoPtr, (uint)length))
            {
                jobHandle.Dispose();
                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(infoPtr);
        }

        return new ProcessJobObject(jobHandle);
    }

    /// <summary>
    /// Associates <paramref name="process"/> (and, implicitly, every future child process
    /// it creates) with this job object. Returns <see langword="false"/> on failure, which
    /// callers should treat as best-effort.
    /// </summary>
    public bool Assign(Process process)
    {
        if (handle is null)
        {
            return false;
        }

        return NativeMethods.AssignProcessToJobObject(handle, process.SafeHandle);
    }

    public void Dispose()
    {
        handle?.Dispose();
        handle = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private sealed class SafeJobObjectHandle : SafeHandle
    {
        public SafeJobObjectHandle()
            : base(IntPtr.Zero, ownsHandle: true)
        {
        }

        public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);

        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private static partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial SafeJobObjectHandle CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetInformationJobObject(SafeJobObjectHandle hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool AssignProcessToJobObject(SafeJobObjectHandle hJob, SafeHandle hProcess);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CloseHandle(IntPtr hObject);
    }
}
