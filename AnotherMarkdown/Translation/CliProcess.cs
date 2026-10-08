using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace AnotherMarkdown.Translation
{
  // Create suspended, join our kill-on-close job, then allow the CLI to execute.
  // No System.Diagnostics.Process stdin writer is created, so the host console
  // encoding cannot emit a preamble into the independent UTF-8 pipe.
  internal sealed class CliProcess : IDisposable
  {
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private SafeFileHandle processHandle;
    private ProcessJob job;
    private int disposed;
    public int Id { get; private set; }
    public StreamWriter StandardInput { get; private set; }
    public StreamReader StandardOutput { get; private set; }
    public StreamReader StandardError { get; private set; }

    public bool HasExited
    {
      get {
        var result = WaitForSingleObject(processHandle, 0);
        if (result == 0xFFFFFFFF) throw new Win32Exception(Marshal.GetLastWin32Error());
        return result == 0;
      }
    }

    public int ExitCode
    {
      get {
        if (!HasExited) throw new InvalidOperationException("CLI ещё работает.");
        uint exitCode;
        if (!GetExitCodeProcess(processHandle, out exitCode)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return unchecked((int)exitCode);
      }
    }

    public static CliProcess Start(ProcessStartInfo info, ProcessJob job, Action<int> beforeAssignment = null, bool detectOutputEncoding = true)
    {
      if (info == null || job == null) throw new ArgumentNullException();
      if (info.UseShellExecute || !info.RedirectStandardInput || !info.RedirectStandardOutput || !info.RedirectStandardError || info.UserName.Length != 0)
        throw new ArgumentException("CLI требует скрытый запуск с отдельными stdin/stdout/stderr.");
      if (string.IsNullOrEmpty(info.FileName) || info.FileName.IndexOf('\0') >= 0 || (info.Arguments ?? "").IndexOf('\0') >= 0 || (info.WorkingDirectory ?? "").IndexOf('\0') >= 0)
        throw new ArgumentException("Команда CLI содержит недопустимое имя или нулевой символ.");
      var commandLine = new StringBuilder(CliTranslator.QuoteArgument(info.FileName) + " " + info.Arguments);
      if (commandLine.Length >= 32767) throw new ArgumentException("Команда CLI превышает ограничение Windows.");
      SafeFileHandle childInput = null, input = null, output = null, childOutput = null, error = null, childError = null;
      IntPtr attributes = IntPtr.Zero, inheritedHandles = IntPtr.Zero, environment = IntPtr.Zero;
      var attributesInitialized = false;
      var native = new ProcessInformation();
      CliProcess process = null;
      try {
        var security = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), InheritHandle = 1 };
        if (!CreatePipe(out childInput, out input, ref security, 0) ||
            !CreatePipe(out output, out childOutput, ref security, 0) ||
            !CreatePipe(out error, out childError, ref security, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        foreach (var parent in new[] { input, output, error })
          if (!SetHandleInformation(parent, 1, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());

        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        attributes = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
        attributesInitialized = true;
        inheritedHandles = Marshal.AllocHGlobal(IntPtr.Size * 3);
        Marshal.WriteIntPtr(inheritedHandles, 0, childInput.DangerousGetHandle());
        Marshal.WriteIntPtr(inheritedHandles, IntPtr.Size, childOutput.DangerousGetHandle());
        Marshal.WriteIntPtr(inheritedHandles, IntPtr.Size * 2, childError.DangerousGetHandle());
        if (!UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x20002), inheritedHandles, new IntPtr(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
          throw new Win32Exception(Marshal.GetLastWin32Error());
        var startup = new StartupInfoEx {
          StartupInfo = new StartupInfo {
            Size = Marshal.SizeOf(typeof(StartupInfoEx)), Flags = 0x101, ShowWindow = 0,
            StandardInput = childInput.DangerousGetHandle(), StandardOutput = childOutput.DangerousGetHandle(), StandardError = childError.DangerousGetHandle()
          }, AttributeList = attributes
        };
        var variables = new StringBuilder();
        foreach (var key in info.EnvironmentVariables.Keys.Cast<string>().OrderBy(key => key, StringComparer.OrdinalIgnoreCase)) {
          var value = info.EnvironmentVariables[key];
          if (value == null) continue;
          if (key.IndexOf('\0') >= 0 || value.IndexOf('\0') >= 0) throw new ArgumentException("Окружение CLI содержит нулевой символ.");
          variables.Append(key).Append('=').Append(value).Append('\0');
        }
        variables.Append('\0');
        environment = Marshal.StringToHGlobalUni(variables.ToString());
        // CREATE_SUSPENDED | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT |
        // EXTENDED_STARTUPINFO_PRESENT. Only the three listed handles inherit.
        if (!CreateProcess(info.FileName, commandLine, IntPtr.Zero, IntPtr.Zero, true, 0x08080404, environment,
            string.IsNullOrEmpty(info.WorkingDirectory) ? null : info.WorkingDirectory, ref startup, out native))
          throw new Win32Exception(Marshal.GetLastWin32Error());
        process = new CliProcess { processHandle = new SafeFileHandle(native.Process, true), Id = native.ProcessId, job = job };
        native.Process = IntPtr.Zero;
        childInput.Dispose(); childInput = null;
        childOutput.Dispose(); childOutput = null;
        childError.Dispose(); childError = null;
        // Optional test seam verifies that an immediate shim cannot execute in
        // the old Process.Start -> AssignProcessToJobObject race interval.
        beforeAssignment?.Invoke(process.Id);
        job.Add(process.processHandle.DangerousGetHandle());
        process.StandardInput = new StreamWriter(new FileStream(input, FileAccess.Write, 4096, false), Utf8, 4096);
        input = null;
        process.StandardOutput = new StreamReader(new FileStream(output, FileAccess.Read, 4096, false), info.StandardOutputEncoding ?? Utf8, detectOutputEncoding, 4096);
        output = null;
        process.StandardError = new StreamReader(new FileStream(error, FileAccess.Read, 4096, false), info.StandardErrorEncoding ?? Utf8, detectOutputEncoding, 4096);
        error = null;
        if (ResumeThread(native.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        return process;
      }
      catch {
        // Assignment/resume/pipe setup failures never run the suspended child.
        if (process != null) process.Dispose();
        else if (native.Process != IntPtr.Zero) TerminateUnwrappedProcess(native.Process, 1);
        throw;
      }
      finally {
        if (native.Thread != IntPtr.Zero) CloseHandle(native.Thread);
        if (native.Process != IntPtr.Zero) CloseHandle(native.Process);
        childInput?.Dispose(); childOutput?.Dispose(); childError?.Dispose();
        input?.Dispose(); output?.Dispose(); error?.Dispose();
        if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
        if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
        if (inheritedHandles != IntPtr.Zero) Marshal.FreeHGlobal(inheritedHandles);
        if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
      }
    }

    public void Kill()
    {
      if (processHandle == null || processHandle.IsClosed || processHandle.IsInvalid) return;
      if (TerminateProcess(processHandle, 1)) return;
      var error = Marshal.GetLastWin32Error();
      if (HasExited) return;
      // Closing a kill-on-close job begins asynchronous termination. Windows
      // can return ACCESS_DENIED for a second terminate during that interval.
      if (error == 5 && WaitForSingleObject(processHandle, 500) == 0) return;
      throw new Win32Exception(error);
    }

    public void WaitForExit()
    {
      if (WaitForSingleObject(processHandle, uint.MaxValue) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
      if (Interlocked.Exchange(ref disposed, 1) != 0) return;
      // Cleanup must not replace a completed RPC, cancellation, or the original
      // startup failure. Release each independent stream even if another pipe
      // was closed by the exiting CLI while StreamWriter flushes on disposal.
      try { Kill(); } catch (Win32Exception) { } catch (InvalidOperationException) { }
      try { job?.Dispose(); }
      finally {
        try { DisposePipe(StandardInput); }
        finally {
          try { DisposePipe(StandardOutput); }
          finally {
            try { DisposePipe(StandardError); }
            finally { processHandle?.Dispose(); }
          }
        }
      }
    }

    private static void DisposePipe(IDisposable pipe)
    {
      try { pipe?.Dispose(); } catch (IOException) { } catch (InvalidOperationException) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr SecurityDescriptor; public int InheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
      public int Size; public string Reserved, Desktop, Title;
      public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
      public short ShowWindow, ReservedSize; public IntPtr Reserved2, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
    [DllImport("kernel32.dll", EntryPoint = "TerminateProcess", SetLastError = true)] private static extern bool TerminateUnwrappedProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
  }
}
