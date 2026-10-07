using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AnotherMarkdown.Translation
{
  // Own only the translator process and its children, never an existing CLI session.
  internal sealed class ProcessJob : IDisposable
  {
    private readonly SafeFileHandle handle;

    public ProcessJob()
    {
      handle = CreateJobObject(IntPtr.Zero, null);
      if (handle.IsInvalid) throw new Win32Exception();
      var info = new ExtendedLimitInformation();
      info.BasicLimitInformation.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
      if (!SetInformationJobObject(handle, 9, ref info, (uint)Marshal.SizeOf(info))) {
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
      }
    }

    public void Add(Process process)
    {
      if (!AssignProcessToJobObject(handle, process.Handle)) {
        var error = Marshal.GetLastWin32Error();
        // A very short-lived CLI may have exited before assignment.
        if (process.HasExited) return;
        try { process.Kill(); } catch (InvalidOperationException) { }
        throw new Win32Exception(error);
      }
    }

    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
      public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
      public uint LimitFlags;
      public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
      public uint ActiveProcessLimit;
      public UIntPtr Affinity;
      public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
      public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
      public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
      public BasicLimitInformation BasicLimitInformation;
      public IoCounters IoInfo;
      public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass,
      ref ExtendedLimitInformation info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
  }
}
