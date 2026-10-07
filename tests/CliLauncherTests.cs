using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AnotherMarkdown.Translation;
using Newtonsoft.Json.Linq;

internal static class CliLauncherTests
{
  private static int passed;
  private static readonly Encoding Utf8 = new UTF8Encoding(false);
  private static string executable;

  private static int Main(string[] args)
  {
    Console.InputEncoding = Utf8;
    Console.OutputEncoding = Utf8;
    executable = System.Reflection.Assembly.GetExecutingAssembly().Location;
    if (args.Length > 0 && args[0] == "echo") {
      Console.Write(new JObject { ["argv"] = new JArray(args.Skip(1)), ["stdin"] = Console.In.ReadToEnd() }.ToString(Newtonsoft.Json.Formatting.None));
      return 0;
    }
    if (args.Length > 0 && args[0] == "sleep") { Thread.Sleep(60000); return 0; }
    if (args.Length > 0 && args[0] == "tree") {
      var child = Process.Start(new ProcessStartInfo(executable, "sleep") { UseShellExecute = false, CreateNoWindow = true });
      var pendingPid = args[1] + ".tmp";
      File.WriteAllText(pendingPid, child.Id.ToString(), Utf8);
      File.Move(pendingPid, args[1]);
      Thread.Sleep(60000);
      return 0;
    }
    try {
      Run().GetAwaiter().GetResult();
      Console.WriteLine("PASS launcher: " + passed + " assertions");
      return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
  }

  private static void Check(bool condition, string label)
  {
    if (!condition) throw new Exception("FAIL " + label);
    passed++;
    Console.WriteLine("PASS " + label);
  }

  private static void Throws<T>(Action action, string label) where T : Exception
  {
    try { action(); }
    catch (T) { Check(true, label); return; }
    throw new Exception("FAIL expected " + typeof(T).Name + ": " + label);
  }

  private static async Task Run()
  {
    var tempBase = Directory.Exists(@"D:\Temp") ? @"D:\Temp\agent\markdown-ru" : Path.GetTempPath();
    var directory = Path.Combine(tempBase, "launcher-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      var nativeDirectory = Path.Combine(directory, "native");
      var scriptDirectory = Path.Combine(directory, "wrapper space (x86) & data \u0442\u0435\u0441\u0442");
      Directory.CreateDirectory(nativeDirectory);
      Directory.CreateDirectory(scriptDirectory);
      File.WriteAllText(Path.Combine(scriptDirectory, "agent.cmd"), "@echo off\r\n", Utf8);
      File.WriteAllText(Path.Combine(scriptDirectory, "cursor-agent.cmd"), "@echo off\r\n", Utf8);
      File.WriteAllText(Path.Combine(nativeDirectory, "cursor-agent.exe"), "", Utf8);
      File.WriteAllText(Path.Combine(scriptDirectory, "qwen.cmd"), "@echo off\r\n", Utf8);
      File.WriteAllText(Path.Combine(nativeDirectory, "qwen.cmd"), "@echo off\r\n", Utf8);
      File.WriteAllText(Path.Combine(scriptDirectory, "dsh.bat"), "@echo off\r\n", Utf8);
      var found = CliProfiles.DiscoverInstalled(new[] { scriptDirectory, nativeDirectory, scriptDirectory });
      Check(found.Count(i => i.ProviderId == "cursor") == 1 && found.Single(i => i.ProviderId == "cursor").Executable == Path.Combine(nativeDirectory, "cursor-agent.exe"), "one Cursor entry prefers native EXE across directories and aliases");
      Check(found.Count(i => i.ProviderId == "qwen") == 1 && found.Single(i => i.ProviderId == "qwen").Executable == Path.Combine(scriptDirectory, "qwen.cmd"), "one batch entry preserves PATH directory preference");
      Check(found.Single(i => i.ProviderId == "dsh").Executable.EndsWith(".bat"), "BAT fallback discovered");
      Check(CliProfiles.IsLauncherPath("cli.CMD") && CliProfiles.IsLauncherPath("cli.bat") && CliProfiles.IsLauncherPath("cli.EXE") && !CliProfiles.IsLauncherPath("cli.ps1"), "launcher extension helper");
      foreach (var extension in new[] { ".cmd", ".bat" }) {
        var launcher = Path.Combine(scriptDirectory, "echo-" + extension.Substring(1) + extension);
        File.WriteAllText(launcher, "@echo off\r\nsetlocal enabledelayedexpansion\r\n\"" + executable + "\" echo %*\r\n", Utf8);
        var options = CliProfiles.Defaults("custom", launcher);
        options.Validate();
        Check(CliTranslator.ResolveExecutable(launcher) == launcher && CliTranslator.ResolveExecutable(Path.ChangeExtension(launcher, null)) == launcher, extension + " direct and extensionless resolution");
        var values = new[] { "", "simple", "model with spaces", "\u041c\u043e\u0434\u0435\u043b\u044c", "amp & pipe | angle < > parentheses (x) brackets [x] semi; comma, star* question?", "trailing slash\\", "space and slash\\" };
        var arguments = string.Join(" ", values.Select(CliTranslator.QuoteArgument));
        var info = CliCommand.StartInfo(launcher, arguments, directory);
        Check(info.CreateNoWindow && !info.UseShellExecute && info.RedirectStandardInput && info.RedirectStandardOutput && info.RedirectStandardError && info.FileName == Path.Combine(Environment.SystemDirectory, "cmd.exe"), extension + " hidden redirected cmd host");
        var input = "# \u041f\u0440\u0438\u0432\u0435\u0442\nA \"quote\" & %PATH% ! ^ | < >\n";
        var result = await CliCommand.RunAsync(launcher, arguments, input, 10, CancellationToken.None, directory);
        Check(result.ExitCode == 0, extension + " forwarding exit code");
        var payload = JObject.Parse(result.StandardOutput);
        Check(payload["argv"].Values<string>().SequenceEqual(values), extension + " exact argv including metacharacters empty and trailing slash");
        Check((string)payload["stdin"] == input, extension + " exact UTF-8 stdin including quotes expansions and newlines");
        var marker = Path.Combine(directory, "injected.txt");
        var unsafeValues = new[] { "embedded \"quote\" & echo injected > " + marker, "%PATH%", "!PATH!", "caret^", "line\nbreak", "nul\0suffix" };
        foreach (var value in unsafeValues)
          Throws<ArgumentException>(() => CliCommand.StartInfo(launcher, CliTranslator.QuoteArgument(value), directory), extension + " rejects unsupported argument " + Array.IndexOf(unsafeValues, value));
        Check(!File.Exists(marker), extension + " rejected injection creates no file");
        var literalOperators = await CliCommand.RunAsync(launcher, "& echo injected > " + CliTranslator.QuoteArgument(marker), "", 10, CancellationToken.None, directory);
        Check(literalOperators.ExitCode == 0 && !File.Exists(marker) && JObject.Parse(literalOperators.StandardOutput)["argv"].Values<string>().Take(2).SequenceEqual(new[] { "&", "echo" }), extension + " raw shell operators become literal arguments");
        Throws<ArgumentException>(() => CliCommand.StartInfo(launcher, CliTranslator.QuoteArgument(new string('a', 8100)), directory), extension + " cmd command-length cap");
      }
      var forbidden = Path.Combine(directory, "forbidden.ps1");
      File.WriteAllText(forbidden, "", Utf8);
      Throws<ArgumentException>(() => CliTranslator.ResolveExecutable(forbidden), "PowerShell launcher remains unsupported");
      var treeLauncher = Path.Combine(scriptDirectory, "tree.cmd");
      File.WriteAllText(treeLauncher, "@echo off\r\n\"" + executable + "\" tree %*\r\n", Utf8);
      var pidFile = Path.Combine(directory, "child.pid");
      using (var cancellation = new CancellationTokenSource()) {
        var pending = CliCommand.RunAsync(treeLauncher, CliTranslator.QuoteArgument(pidFile), "", 10, cancellation.Token, directory);
        for (var attempt = 0; attempt < 100 && !File.Exists(pidFile); attempt++) await Task.Delay(25);
        Check(File.Exists(pidFile), "batch descendant started");
        var pid = int.Parse(File.ReadAllText(pidFile));
        cancellation.Cancel();
        try { await pending; throw new Exception("FAIL batch cancellation completed successfully"); }
        catch (OperationCanceledException) { Check(true, "batch cancellation propagates"); }
        await Task.Delay(100);
        var alive = false;
        try { using (var child = Process.GetProcessById(pid)) alive = !child.HasExited; }
        catch (ArgumentException) { }
        Check(!alive, "batch cancellation leaves no descendant");
      }
    }
    finally { Directory.Delete(directory, true); }
  }
}
