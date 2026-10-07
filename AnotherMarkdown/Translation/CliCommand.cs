using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
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
      if (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)) {
        if (Regex.IsMatch(executable + arguments, "[&|<>^%!\\r\\n]"))
          throw new ArgumentException("Для .cmd/.bat недопустимы метасимволы оболочки. Выберите .exe CLI или node.exe с путём к скрипту в аргументах.");
        info.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
        info.Arguments = "/d /s /v:off /c \"" + CliTranslator.QuoteArgument(executable) + " " + arguments + "\"";
      }
      else if (executable.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)) {
        var pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        info.FileName = File.Exists(pwsh) ? pwsh : Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        info.Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + CliTranslator.QuoteArgument(executable) + " " + arguments;
      }
      return info;
    }

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
