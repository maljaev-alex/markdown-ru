using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AnotherMarkdown.Translation
{
  internal sealed class CliCommandResult
  {
    public int ExitCode;
    public string StandardOutput;
    public byte[] StandardOutputBytes;
    public string StandardError;
  }

  internal static class CliCommand
  {
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public static ProcessStartInfo StartInfo(string executable, string arguments, string workingDirectory, string providerId = null)
    {
      executable = CliTranslator.ResolveExecutable(executable);
      var info = new ProcessStartInfo(executable, arguments) {
        UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory,
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8
      };
      info.EnvironmentVariables.Remove("CODEX_THREAD_ID");
      info.EnvironmentVariables.Remove("CODEX_INTERNAL_ORIGINATOR_OVERRIDE");
      info.EnvironmentVariables["NO_COLOR"] = "1";
      info.EnvironmentVariables["TERM"] = "dumb";
      var selectedProvider = providerId == null ? CliProfiles.Identify(executable) : providerId.Trim().ToLowerInvariant();
      if (selectedProvider == "opencode") {
        info.EnvironmentVariables["OPENCODE_PERMISSION"] = "{\"*\":\"deny\"}";
        info.EnvironmentVariables["OPENCODE_DISABLE_AUTOUPDATE"] = "true";
        info.EnvironmentVariables["OPENCODE_DISABLE_LSP_DOWNLOAD"] = "true";
        info.EnvironmentVariables["OPENCODE_DISABLE_DEFAULT_PLUGINS"] = "true";
      }
      if (selectedProvider == "kimi") {
        info.EnvironmentVariables["KIMI_CODE_NO_AUTO_UPDATE"] = "1";
        info.EnvironmentVariables["KIMI_DISABLE_TELEMETRY"] = "1";
      }
      if (selectedProvider == "copilot") info.EnvironmentVariables["COPILOT_AUTO_UPDATE"] = "false";
      if (selectedProvider == "curl-api") {
        info.EnvironmentVariables.Remove("SSLKEYLOGFILE");
        // Windows curl can write localized ANSI diagnostics. They are discarded;
        // stdout is read as raw response bytes and validated by the API parser.
        info.StandardErrorEncoding = new UTF8Encoding(false, false);
      }
      if (CliProfiles.IsBatchPath(executable)) {
        // Batch wrappers reparse %* and may enable delayed expansion themselves.
        // Requote each native argument, and fail before launch for values whose
        // expansion or embedded quotes cannot be preserved through arbitrary wrappers.
        ValidateBatchValue(executable);
        if ((arguments ?? "").IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
          throw new ArgumentException("Аргументы .cmd/.bat не должны содержать нулевой символ или перенос строки. Выберите нативный .exe CLI или передачу документа через stdin.");
        var command = new StringBuilder(CliTranslator.QuoteArgument(executable));
        foreach (var argument in SplitArguments(arguments ?? "")) {
          ValidateBatchValue(argument);
          command.Append(' ').Append(CliTranslator.QuoteArgument(argument));
        }
        info.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        info.Arguments = "/d /s /v:off /c \"" + command + "\"";
        if (info.Arguments.Length + info.FileName.Length > 8000)
          throw new ArgumentException("Запрос превышает размер командной строки .cmd/.bat. Выберите нативный .exe CLI или CLI с передачей документа через stdin.");
      }
      else if (executable.Length + (arguments ?? "").Length > 30000)
        throw new ArgumentException("Запрос превышает размер командной строки Windows. Для этого документа выберите CLI с передачей запроса через stdin, например Codex.");
      return info;
    }

    private static void ValidateBatchValue(string value)
    {
      if (value.IndexOfAny(new[] { '\0', '\r', '\n', '"', '%', '!', '^' }) >= 0)
        throw new ArgumentException("Аргумент .cmd/.bat содержит кавычки, перенос строки или символы %, !, ^, которые нельзя безопасно передать через этот launcher. Выберите нативный .exe CLI или передачу документа через stdin.");
    }

    private static string[] SplitArguments(string arguments)
    {
      // Prefix a dummy executable: CommandLineToArgvW treats argv[0] differently.
      int count;
      var pointer = CommandLineToArgvW("launcher.exe " + arguments, out count);
      if (pointer == IntPtr.Zero) throw new Win32Exception();
      try {
        var result = new string[Math.Max(0, count - 1)];
        for (var index = 1; index < count; index++)
          result[index - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size));
        return result;
      }
      finally { LocalFree(pointer); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static Task<CliCommandResult> RunAsync(string executable, string arguments, string input,
      int timeoutSeconds, CancellationToken cancellation, string workingDirectory = null, string providerId = null,
      int maximumOutputCharacters = 8000000, bool detectOutputEncoding = true, int maximumStandardOutputBytes = 0) =>
      Task.Run(() => RunCoreAsync(executable, arguments, input, timeoutSeconds, cancellation, workingDirectory, providerId, maximumOutputCharacters, detectOutputEncoding, maximumStandardOutputBytes), cancellation);

    private static async Task<CliCommandResult> RunCoreAsync(string executable, string arguments, string input,
      int timeoutSeconds, CancellationToken cancellation, string workingDirectory, string providerId, int maximumOutputCharacters, bool detectOutputEncoding, int maximumStandardOutputBytes)
    {
      if (maximumOutputCharacters < 1 || maximumOutputCharacters > 32001000) throw new ArgumentOutOfRangeException(nameof(maximumOutputCharacters));
      if (maximumStandardOutputBytes < 0 || maximumStandardOutputBytes > 32001000) throw new ArgumentOutOfRangeException(nameof(maximumStandardOutputBytes));
      var ownsDirectory = workingDirectory == null;
      var directory = workingDirectory ?? Path.Combine(Path.GetTempPath(), "AnotherMarkdown", "probe-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(directory);
      try {
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        using (var job = new ProcessJob()) {
          timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
          cancellation.ThrowIfCancellationRequested();
          using (var process = CliProcess.Start(StartInfo(executable, arguments, directory, providerId), job, detectOutputEncoding: detectOutputEncoding)) {
            try {
              using (timeout.Token.Register(() => StopOwnedProcess(process, job))) {
                var stdout = maximumStandardOutputBytes == 0
                  ? ReadAsync(process.StandardOutput, timeout.Token, () => StopOwnedProcess(process, job), maximumOutputCharacters) : null;
                var stdoutBytes = maximumStandardOutputBytes == 0 ? null
                  : ReadBytesAsync(process.StandardOutput.BaseStream, timeout.Token, () => StopOwnedProcess(process, job), maximumStandardOutputBytes);
                var stderr = ReadAsync(process.StandardError, timeout.Token, () => StopOwnedProcess(process, job), maximumOutputCharacters);
                Exception inputError = null;
                try {
                  var bytes = Utf8.GetBytes(input ?? "");
                  await WithCancellationAsync(process.StandardInput.BaseStream.WriteAsync(bytes, 0, bytes.Length), timeout.Token).ConfigureAwait(false);
                  process.StandardInput.Close();
                }
                catch (IOException error) { inputError = error; }
                await WithCancellationAsync(Task.WhenAll((Task)stdout ?? stdoutBytes, stderr, Task.Run(() => process.WaitForExit())), timeout.Token).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                if (process.ExitCode == 0 && inputError != null) throw new IOException("CLI закрыл stdin до получения документа.", inputError);
                return new CliCommandResult { ExitCode = process.ExitCode, StandardOutput = stdout == null ? null : await stdout.ConfigureAwait(false),
                  StandardOutputBytes = stdoutBytes == null ? null : await stdoutBytes.ConfigureAwait(false), StandardError = await stderr.ConfigureAwait(false) };
              }
            }
            catch (Exception) when (timeout.IsCancellationRequested) {
              cancellation.ThrowIfCancellationRequested();
              throw new TimeoutException("CLI не ответил за " + timeoutSeconds + " секунд.");
            }
            finally { StopOwnedProcess(process, job); }
          }
        }
      }
      finally {
        if (ownsDirectory) {
          try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
      }
    }

    private static void StopOwnedProcess(CliProcess process, ProcessJob job)
    {
      job.Dispose();
      try { if (!process.HasExited) process.Kill(); }
      catch (InvalidOperationException) { } catch (Win32Exception) { }
    }

    private static async Task<string> ReadAsync(StreamReader reader, CancellationToken cancellation, Action abort, int maximumOutputCharacters)
    {
      try {
        var result = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await WithCancellationAsync(reader.ReadAsync(buffer, 0, buffer.Length), cancellation).ConfigureAwait(false)) != 0) {
          if (result.Length + count > maximumOutputCharacters) throw new InvalidOperationException(maximumOutputCharacters == 8000000
            ? "Ответ CLI превысил ограничение 8 млн символов." : "Ответ CLI превысил допустимый размер.");
          result.Append(buffer, 0, count);
        }
        return result.ToString();
      }
      catch { abort(); throw; }
    }

    private static async Task<byte[]> ReadBytesAsync(Stream stream, CancellationToken cancellation, Action abort, int maximumBytes)
    {
      try {
        using (var result = new MemoryStream()) {
          var buffer = new byte[8192]; int count;
          while ((count = await WithCancellationAsync(stream.ReadAsync(buffer, 0, buffer.Length, cancellation), cancellation).ConfigureAwait(false)) != 0) {
            if (result.Length + count > maximumBytes) throw new InvalidOperationException("Ответ CLI превысил допустимый размер.");
            result.Write(buffer, 0, count);
          }
          return result.ToArray();
        }
      }
      catch { abort(); throw; }
    }

    private static async Task WithCancellationAsync(Task task, CancellationToken cancellation)
    {
      _ = task.ContinueWith(faulted => { var ignored = faulted.Exception; }, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
      cancellation.ThrowIfCancellationRequested();
      var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
      using (cancellation.Register(() => canceled.TrySetResult(true))) {
        if (await Task.WhenAny(task, canceled.Task).ConfigureAwait(false) != task) cancellation.ThrowIfCancellationRequested();
        await task.ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
      }
    }

    private static async Task<T> WithCancellationAsync<T>(Task<T> task, CancellationToken cancellation)
    {
      await WithCancellationAsync((Task)task, cancellation).ConfigureAwait(false);
      return await task.ConfigureAwait(false);
    }
  }
}
