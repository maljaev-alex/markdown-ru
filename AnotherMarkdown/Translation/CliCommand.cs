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
    public string StandardError;
  }

  internal static class CliCommand
  {
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public static ProcessStartInfo StartInfo(string executable, string arguments, string workingDirectory)
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
      if (CliProfiles.Identify(executable) == "opencode") {
        info.EnvironmentVariables["OPENCODE_PERMISSION"] = "{\"*\":\"deny\"}";
        info.EnvironmentVariables["OPENCODE_DISABLE_AUTOUPDATE"] = "true";
        info.EnvironmentVariables["OPENCODE_DISABLE_LSP_DOWNLOAD"] = "true";
        info.EnvironmentVariables["OPENCODE_DISABLE_DEFAULT_PLUGINS"] = "true";
      }
      if (CliProfiles.Identify(executable) == "kimi") {
        info.EnvironmentVariables["KIMI_CODE_NO_AUTO_UPDATE"] = "1";
        info.EnvironmentVariables["KIMI_DISABLE_TELEMETRY"] = "1";
      }
      if (CliProfiles.Identify(executable) == "copilot") info.EnvironmentVariables["COPILOT_AUTO_UPDATE"] = "false";
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
      int timeoutSeconds, CancellationToken cancellation, string workingDirectory = null) =>
      Task.Run(() => RunCoreAsync(executable, arguments, input, timeoutSeconds, cancellation, workingDirectory), cancellation);

    private static async Task<CliCommandResult> RunCoreAsync(string executable, string arguments, string input,
      int timeoutSeconds, CancellationToken cancellation, string workingDirectory = null)
    {
      var ownsDirectory = workingDirectory == null;
      var directory = workingDirectory ?? Path.Combine(Path.GetTempPath(), "AnotherMarkdown", "probe-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(directory);
      try {
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        using (var job = new ProcessJob())
        using (var process = new Process { StartInfo = StartInfo(executable, arguments, directory) }) {
          timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
          try {
            cancellation.ThrowIfCancellationRequested();
            process.Start(); job.Add(process);
            using (timeout.Token.Register(() => {
              job.Dispose();
              try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (Win32Exception) { }
            })) {
              var stdout = ReadAsync(process.StandardOutput);
              var stderr = ReadAsync(process.StandardError);
              Exception inputError = null;
              try {
                var bytes = Utf8.GetBytes(input ?? "");
                await process.StandardInput.BaseStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                process.StandardInput.Close();
              }
              catch (IOException error) { inputError = error; }
              await Task.WhenAll(stdout, stderr, Task.Run(() => process.WaitForExit())).ConfigureAwait(false);
              timeout.Token.ThrowIfCancellationRequested();
              if (process.ExitCode == 0 && inputError != null) throw new IOException("CLI закрыл stdin до получения документа.", inputError);
              return new CliCommandResult { ExitCode = process.ExitCode, StandardOutput = await stdout.ConfigureAwait(false), StandardError = await stderr.ConfigureAwait(false) };
            }
          }
          catch (Exception) when (timeout.IsCancellationRequested) {
            cancellation.ThrowIfCancellationRequested();
            throw new TimeoutException("CLI не ответил за " + timeoutSeconds + " секунд.");
          }
        }
      }
      finally {
        if (ownsDirectory) {
          try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
      }
    }

    private static async Task<string> ReadAsync(StreamReader reader)
    {
      var result = new StringBuilder(); var buffer = new char[4096]; var overflow = false; int count;
      while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0) {
        if (result.Length + count > 8000000) overflow = true;
        if (result.Length < 8000000) result.Append(buffer, 0, Math.Min(count, 8000000 - result.Length));
      }
      if (overflow) throw new InvalidOperationException("Ответ CLI превысил ограничение 8 млн символов.");
      return result.ToString();
    }
  }
}
