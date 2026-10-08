using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    if (args.Length > 0 && args[0] == "raw-bytes") {
      using (var data = new MemoryStream()) {
        Console.OpenStandardInput().CopyTo(data);
        Console.Write(Convert.ToBase64String(data.ToArray()));
      }
      return 0;
    }
    if (args.Length > 0 && args[0] == "localized-stderr") {
      Console.Write("{\"ok\":true}");
      using (var error = Console.OpenStandardError()) {
        var localizedBytes = new byte[] { 0xD2, 0xE5, 0xF1, 0xF2 };
        error.Write(localizedBytes, 0, localizedBytes.Length);
      }
      return 35;
    }
    if (args.Length > 0 && args[0] == "environment") {
      Console.Write(new JObject {
        ["opencode"] = Environment.GetEnvironmentVariable("OPENCODE_PERMISSION"),
        ["opencodeUpdate"] = Environment.GetEnvironmentVariable("OPENCODE_DISABLE_AUTOUPDATE"),
        ["opencodeLsp"] = Environment.GetEnvironmentVariable("OPENCODE_DISABLE_LSP_DOWNLOAD"),
        ["opencodePlugins"] = Environment.GetEnvironmentVariable("OPENCODE_DISABLE_DEFAULT_PLUGINS"),
        ["kimiUpdate"] = Environment.GetEnvironmentVariable("KIMI_CODE_NO_AUTO_UPDATE"),
        ["kimiTelemetry"] = Environment.GetEnvironmentVariable("KIMI_DISABLE_TELEMETRY"),
        ["copilotUpdate"] = Environment.GetEnvironmentVariable("COPILOT_AUTO_UPDATE")
      }.ToString(Newtonsoft.Json.Formatting.None));
      return 0;
    }
    if (args.Length > 0 && args[0] == "signal-handle") { SetEvent(new IntPtr(long.Parse(args[1]))); return 0; }
    if (args.Length > 0 && (args[0] == "spawn-exit" || args[0] == "spawn-wait")) {
      using (var child = Process.Start(new ProcessStartInfo(executable, "sleep") { UseShellExecute = false, CreateNoWindow = true })) {
        var record = new JObject { ["parent"] = Process.GetCurrentProcess().Id, ["child"] = child.Id };
        File.WriteAllText(args[1] + ".tmp", record.ToString(Newtonsoft.Json.Formatting.None), Utf8);
        File.Move(args[1] + ".tmp", args[1]);
      }
      if (args[0] == "spawn-wait") Thread.Sleep(60000);
      return 0;
    }
    if (args.Length > 0 && args[0] == "flood") { Console.Write(new string('x', 8000001)); Console.Out.Flush(); Thread.Sleep(60000); return 0; }
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
      var dshLauncher = Path.Combine(scriptDirectory, "dsh.cmd");
      File.WriteAllText(dshLauncher, "@echo off\r\n\"" + executable + "\" echo %*\r\n", Utf8);
      var dshOptions = CliProfiles.Defaults("dsh", dshLauncher);
      var previousTemp = Environment.GetEnvironmentVariable("TEMP");
      try {
        Environment.SetEnvironmentVariable("TEMP", directory);
        var document = "# Heading\n\"quoted\" & %PATH% ! ^\n" + new string('a', 12000);
        var answer = JObject.Parse(await new CliTranslator().TranslateAsync(document, dshOptions, CancellationToken.None));
        Check(answer["argv"].Values<string>().SequenceEqual(new[] { "--profile", "headless" }), "DeepSeek batch profile keeps the document out of argv");
        Check(((string)answer["stdin"]).Contains(document), "DeepSeek batch receives long multiline Markdown and metacharacters verbatim over stdin");
      }
      finally { Environment.SetEnvironmentVariable("TEMP", previousTemp); }
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
      await NativeTransport(directory, scriptDirectory, nativeDirectory);
    }
    finally { Directory.Delete(directory, true); }
  }

  private static async Task NativeTransport(string directory, string scripts, string nativeDirectory)
  {
    var wrapper = Path.Combine(scripts, "transport.cmd");
    File.WriteAllText(wrapper, "@echo off\r\n\"" + executable + "\" %*\r\n", Utf8);
    var hostEncoding = Console.InputEncoding;
    var document = "# \u041f\u0440\u0438\u0432\u0435\u0442 \ud83c\udf0d\nquotes \" & %PATH% ! ^ | < >\n";
    try {
      foreach (var encoding in new Encoding[] { new UTF8Encoding(true), new UnicodeEncoding(false, true) }) {
        Console.InputEncoding = encoding;
        foreach (var launcher in new[] { executable, wrapper }) {
          var result = await CliCommand.RunAsync(launcher, "raw-bytes", document, 5, CancellationToken.None, directory);
          Check(result.ExitCode == 0 && Convert.FromBase64String(result.StandardOutput).SequenceEqual(Utf8.GetBytes(document)), "native/batch stdin is exact UTF-8 without host UTF-8/UTF-16 BOM");
          Check(Console.InputEncoding.CodePage == encoding.CodePage && Console.InputEncoding.GetPreamble().SequenceEqual(encoding.GetPreamble()), "native transport preserves host Console.InputEncoding");
        }
      }
    }
    finally { Console.InputEncoding = hostEncoding; }

    var curlInfo = CliCommand.StartInfo(executable, "localized-stderr", directory, "curl-api");
    Check(curlInfo.StandardOutputEncoding.DecoderFallback is DecoderExceptionFallback
      && curlInfo.StandardErrorEncoding.DecoderFallback is DecoderReplacementFallback,
      "curl keeps strict UTF-8 response decoding and tolerates discarded localized diagnostics");
    var localizedResult = await CliCommand.RunAsync(executable, "localized-stderr", "", 5, CancellationToken.None,
      directory, "curl-api", detectOutputEncoding: false);
    Check(localizedResult.ExitCode == 35 && (bool)JObject.Parse(localizedResult.StandardOutput)["ok"] && localizedResult.StandardError.Length > 0,
      "localized cp1251 curl stderr preserves valid UTF-8 stdout and native TLS failure code without displaying diagnostics");
    var ordinaryInfo = CliCommand.StartInfo(executable, "localized-stderr", directory, "custom");
    Check(ordinaryInfo.StandardOutputEncoding.DecoderFallback is DecoderExceptionFallback
      && ordinaryInfo.StandardErrorEncoding.DecoderFallback is DecoderExceptionFallback,
      "ordinary CLI providers retain strict UTF-8 stdout and stderr defaults");
    Exception localizedFailure = null;
    try { await CliCommand.RunAsync(executable, "localized-stderr", "", 5, CancellationToken.None, directory, "custom"); }
    catch (Exception error) { localizedFailure = error; }
    Check(localizedFailure is DecoderFallbackException, "ordinary CLI providers still reject malformed UTF-8 stderr");

    var renamed = Path.Combine(nativeDirectory, "oc.exe");
    File.Copy(executable, renamed);
    File.Copy(typeof(JObject).Assembly.Location, Path.Combine(nativeDirectory, "Newtonsoft.Json.dll"));
    foreach (var provider in new[] { "opencode", "kimi", "copilot" }) {
      var info = CliCommand.StartInfo(renamed, "environment", directory, provider);
      var result = await CliCommand.RunAsync(renamed, "environment", "", 5, CancellationToken.None, directory, provider);
      var payload = JObject.Parse(result.StandardOutput);
      if (provider == "opencode") Check(info.EnvironmentVariables["OPENCODE_PERMISSION"] == "{\"*\":\"deny\"}" && (string)payload["opencode"] == "{\"*\":\"deny\"}" && (string)payload["opencodeUpdate"] == "true" && (string)payload["opencodeLsp"] == "true" && (string)payload["opencodePlugins"] == "true", "selected OpenCode provider retains deny-all environment for renamed oc.exe");
      if (provider == "kimi") Check((string)payload["kimiUpdate"] == "1" && (string)payload["kimiTelemetry"] == "1", "selected Kimi provider retains no-update environment for renamed executable");
      if (provider == "copilot") Check((string)payload["copilotUpdate"] == "false", "selected Copilot provider retains no-update environment for renamed executable");
    }
    using (var unrelated = new EventWaitHandle(false, EventResetMode.ManualReset)) {
      var handle = unrelated.SafeWaitHandle.DangerousGetHandle();
      if (!SetHandleInformation(handle, 1, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
      await CliCommand.RunAsync(executable, "signal-handle " + handle.ToInt64(), "", 5, CancellationToken.None, directory);
      Check(!unrelated.WaitOne(0), "explicit handle list prevents inheriting an unrelated inheritable host event");
    }
    var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(index => CliCommand.RunAsync(index % 2 == 0 ? executable : wrapper,
      "raw-bytes", document + index, 5, CancellationToken.None, directory)));
    Check(concurrent.Select((result, index) => result.ExitCode == 0 && Convert.FromBase64String(result.StandardOutput).SequenceEqual(Utf8.GetBytes(document + index))).All(value => value),
      "concurrent native and batch pipes keep each UTF-8 document independent and reach EOF");
    await LegacyRaceWitness(directory);
    await SuspendedJobOwnership(directory);
    foreach (var launcher in new[] { executable, wrapper }) {
      foreach (var cancel in new[] { false, true }) {
        var recordFile = Path.Combine(directory, "immediate-" + Guid.NewGuid().ToString("N") + ".json");
        using (var cancellation = new CancellationTokenSource()) {
          var timer = Stopwatch.StartNew();
          var pending = CliCommand.RunAsync(launcher, "spawn-exit " + CliTranslator.QuoteArgument(recordFile), new string('x', 1000000), cancel ? 10 : 1, cancellation.Token, directory);
          await WaitForFile(recordFile);
          var record = JObject.Parse(File.ReadAllText(recordFile));
          if (cancel) cancellation.Cancel();
          try { await pending; throw new Exception("FAIL immediate shim should be canceled or timed out"); }
          catch (OperationCanceledException) when (cancel) { Check(true, "immediate native/batch shim cancellation propagates"); }
          catch (TimeoutException) when (!cancel) { Check(true, "immediate native/batch shim timeout propagates"); }
          Check(timer.Elapsed < TimeSpan.FromSeconds(5), "blocked stdin and inherited output pipes cannot delay cancellation/timeout");
          await WaitForDead((int)record["parent"]); await WaitForDead((int)record["child"]);
          Check(!Alive((int)record["parent"]) && !Alive((int)record["child"]), "immediate shim cancellation/timeout kills both owned parent and descendant");
        }
      }
    }
    var floodTimer = Stopwatch.StartNew();
    try { await CliCommand.RunAsync(executable, "flood", "", 10, CancellationToken.None, directory); throw new Exception("FAIL output cap should fail"); }
    catch (InvalidOperationException error) { Check(error.Message.Contains("8"), "stdout cap fails with the bounded-output diagnostic"); }
    Check(floodTimer.Elapsed < TimeSpan.FromSeconds(5), "stdout overflow terminates owned process rather than waiting for the full timeout");
  }

  private static bool Alive(int pid)
  {
    try { using (var process = Process.GetProcessById(pid)) return !process.HasExited; }
    catch (ArgumentException) { return false; }
  }

  private static async Task WaitForFile(string path)
  {
    for (var attempt = 0; attempt < 200 && !File.Exists(path); attempt++) await Task.Delay(10);
    Check(File.Exists(path), "owned process fixture published its atomic PID record");
  }

  private static async Task WaitForDead(int pid)
  {
    var timer = Stopwatch.StartNew();
    while (Alive(pid) && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
  }

  private static async Task LegacyRaceWitness(string directory)
  {
    var path = Path.Combine(directory, "legacy-race.json");
    Process child = null;
    using (var job = new ProcessJob())
    using (var parent = new Process { StartInfo = CliCommand.StartInfo(executable, "spawn-exit " + CliTranslator.QuoteArgument(path), directory) }) {
      try {
        parent.Start();
        // Deliberately let the immediate shim create its child and exit before
        // assignment, making the old sequencing race deterministic.
        await WaitForFile(path);
        parent.WaitForExit();
        var record = JObject.Parse(File.ReadAllText(path));
        child = Process.GetProcessById((int)record["child"]);
        Check(!job.Contains(child), "legacy Process.Start-before-job ordering deterministically lets the child escape");
        try { job.Add(parent); } catch (Win32Exception) { }
        job.Dispose();
        Check(!child.HasExited, "closing the late-assigned job cannot kill the already escaped owned child");
      }
      finally {
        // Only this test's own recorded child is terminated, never user sessions.
        if (child != null) { if (!child.HasExited) child.Kill(); child.WaitForExit(); child.Dispose(); }
      }
    }
  }

  private static async Task SuspendedJobOwnership(string directory)
  {
    var path = Path.Combine(directory, "suspended-race.json");
    using (var job = new ProcessJob())
    using (var parent = CliProcess.Start(CliCommand.StartInfo(executable, "spawn-exit " + CliTranslator.QuoteArgument(path), directory), job, pid => {
      Thread.Sleep(100);
      Check(!File.Exists(path) && Alive(pid), "CreateProcess suspended cannot run an immediate shim before assignment");
    })) {
      await WaitForFile(path);
      var record = JObject.Parse(File.ReadAllText(path));
      using (var child = Process.GetProcessById((int)record["child"])) {
        Check(job.Contains(child), "immediate child inherits the specific owned job before its parent can exit");
        job.Dispose();
        // PID lookup can disappear before the pinned process handles signal.
        var timer = Stopwatch.StartNew();
        while ((!parent.HasExited || !child.HasExited) && timer.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        Check(parent.HasExited && child.HasExited, "closing the assigned job removes immediate parent and child");
      }
    }
    var failedPid = 0;
    using (var closedJob = new ProcessJob()) {
      try {
        using (CliProcess.Start(CliCommand.StartInfo(executable, "spawn-exit " + CliTranslator.QuoteArgument(path + ".failed"), directory), closedJob, pid => { failedPid = pid; closedJob.Dispose(); })) { }
        throw new Exception("FAIL assignment to closed job should fail");
      }
      catch (ObjectDisposedException) { Check(true, "failed job assignment refuses to resume the suspended process"); }
      await WaitForDead(failedPid);
      Check(!Alive(failedPid) && !File.Exists(path + ".failed"), "failed assignment terminates the unstarted owned process without creating a child");
    }
  }

  [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
  [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetEvent(IntPtr handle);
}
