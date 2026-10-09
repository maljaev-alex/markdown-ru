using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AnotherMarkdown.Translation;
using Newtonsoft.Json;

internal static class CliConnectionStoreTests
{
  private static int checks;
  private static void Check(bool condition, string label)
  {
    if (!condition) throw new Exception("FAIL: " + label);
    checks++; Console.WriteLine("PASS " + label);
  }
  private static CliConnectionSettings Profile(string provider, string executable, string model)
  {
    return new CliConnectionSettings {
      ProviderId = provider, Executable = executable, Model = model, ReasoningEffort = "high",
      UseDefaultModel = false, UseManualModel = true, UseCustomArguments = true,
      Arguments = "\"first argument\" --model {model} --last \"quoted\\path\"\r\nnext line",
      OutputFormat = "json-stream", TimeoutSeconds = 222, ParallelRequests = 6,
      MinimumChunkCharacters = 500, ShowButtons = false
    };
  }
  private static void Equal(CliConnectionSettings actual, CliConnectionSettings expected, string label)
  {
    Check(actual.ProviderId == expected.ProviderId && actual.Executable == expected.Executable
      && actual.Model == expected.Model && actual.ReasoningEffort == expected.ReasoningEffort
      && actual.UseDefaultModel == expected.UseDefaultModel && actual.UseManualModel == expected.UseManualModel
      && actual.UseCustomArguments == expected.UseCustomArguments && actual.Arguments == expected.Arguments
      && actual.OutputFormat == expected.OutputFormat && actual.TimeoutSeconds == expected.TimeoutSeconds
      && actual.ParallelRequests == expected.ParallelRequests && actual.MinimumChunkCharacters == expected.MinimumChunkCharacters
      && actual.ShowButtons == expected.ShowButtons, label);
  }
  private static void ReadFailure(string path, string contents, string label)
  {
    File.WriteAllText(path, contents, new UTF8Encoding(false)); var bytes = File.ReadAllBytes(path);
    var failed = false;
    try { CliConnectionStore.Load(path); }
    catch (InvalidDataException error) { failed = true; Check(!error.Message.Contains("PRIVATE-SENTINEL") && error.InnerException == null, label + " has a safe diagnostic"); }
    Check(failed && File.ReadAllBytes(path).SequenceEqual(bytes), label + " leaves original bytes intact");
  }
  private static void SaveFailure(string path, CliConnectionSettings value, string label)
  {
    var before = File.ReadAllBytes(path); var failed = false;
    try { CliConnectionStore.Save(path, new[] { value }); }
    catch (IOException error) { failed = true; Check(!error.Message.Contains("PRIVATE-SENTINEL") && error.InnerException == null, label + " has a safe diagnostic"); }
    Check(failed && File.ReadAllBytes(path).SequenceEqual(before), label + " preserves the previous file");
  }
  private sealed class PoisonConverter : JsonConverter
  {
    public override bool CanConvert(Type type) => type == typeof(CliConnectionSettings);
    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) { throw new Exception("Global JSON settings must not run."); }
    public override object ReadJson(JsonReader reader, Type type, object existing, JsonSerializer serializer) { throw new Exception("Global JSON settings must not run."); }
  }
  private static int Main()
  {
    var temporaryRoot = Directory.Exists(@"D:\Temp") ? @"D:\Temp\agent\markdown-ru" : Path.Combine(Path.GetTempPath(), "AnotherMarkdown-tests");
    var directory = Path.Combine(temporaryRoot, "cli-connection-store-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      var path = Path.Combine(directory, "connections.cli.json");
      Check(CliConnectionStore.Load(path).Count == 0, "missing store is an empty catalog");
      Check(CliConnectionStore.Load(Path.Combine(directory, "missing", "catalog.json")).Count == 0, "missing parent is an empty catalog");
      var first = Profile("codex", @"D:\Tools\Codex\codex.exe", "model-a");
      var second = Profile("cursor", first.Executable, "model-b");
      var third = Profile("codex", @"D:\Tools\Other\codex.exe", "model-c");
      Check(CliConnectionSettings.SameIdentity(" CODEX ", @"d:/tools/codex/./codex.exe", "codex", first.Executable), "identity matches Windows casing and rooted dot segments without resolving files");
      Check(CliConnectionSettings.SameIdentity("", @"D:\tools\old\..\codex.exe", "", @"d:\TOOLS\codex.exe"), "empty-provider identity supports pure path matching");
      Check(CliConnectionSettings.SameIdentity("codex", @"\\SERVER\share\old\..\codex.exe", "CODEX", @"\\server\SHARE\codex.exe"), "UNC identities normalize within the share root");
      Check(!CliConnectionSettings.SameIdentity("codex", first.Executable, "cursor", first.Executable), "provider separates an identical executable path");
      Check(!CliConnectionSettings.SameIdentity("codex", first.Executable, "codex", third.Executable), "different executable paths retain independent settings");
      Check(!CliConnectionSettings.SameIdentity("codex", "codex.exe", "codex", @"D:\Tools\Codex\codex.exe"), "a PATH name is not guessed to equal an absolute path");
      Check(CliConnectionSettings.SameIdentity("custom", @".\bin\codex.exe", "custom", Path.Combine(Environment.CurrentDirectory, "bin", "codex.exe")), "a relative directory path matches its lexical absolute browse selection");
      Check(!CliConnectionSettings.SameIdentity("custom", @"D:bin\codex.exe", "custom", @"D:\bin\codex.exe"), "an ambiguous drive-relative path is not guessed to be rooted");
      Check(CliConnectionSettings.SameIdentity("custom", @"%LOCALAPPDATA%\bin\codex.exe", "custom", Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "bin", "codex.exe")),
        "path environment aliases match browsing the same executable without changing its adapter");
      var list = new List<CliConnectionSettings> { first.Copy(), second.Copy(), third.Copy(), first.Copy() };
      var replacement = first.Copy(); replacement.Model = "updated"; replacement.Executable = @"d:/tools/codex/codex.exe";
      CliConnectionSettings.Upsert(list, replacement);
      Check(list.Count == 3 && list[0].Model == "updated" && list[1].Model == "model-b" && list[2].Model == "model-c", "upsert replaces and deduplicates only the matching provider/path");
      replacement.Model = "after-upsert";
      Check(list[0].Model == "updated", "upsert owns a copy of the supplied draft");
      var options = new TranslationOptions {
        ConnectionMode = "api", SelectedApiConnectionId = "selected-api", ApiConfigurationError = "api-warning", CliConfigurationError = "cli-warning",
        ApiConnections = new List<ApiConnection> { new ApiConnection { Id = "selected-api", ApiKey = "synthetic-API-secret-not-in-CLI-store" } },
        CliConnections = list
      };
      var apiList = options.ApiConnections; var cliList = options.CliConnections;
      first.ApplyTo(options); Equal(CliConnectionSettings.Capture(options), first, "capture and apply preserve all fourteen CLI settings");
      Check(options.ConnectionMode == "api" && options.SelectedApiConnectionId == "selected-api"
        && ReferenceEquals(options.ApiConnections, apiList) && ReferenceEquals(options.CliConnections, cliList)
        && options.ApiConfigurationError == "api-warning" && options.CliConfigurationError == "cli-warning", "applying a CLI profile does not alter mode, API connections, lists or load errors");
      var copied = options.Copy(); copied.CliConnections[0].Model = "copy-only"; copied.CliConnections.Add(first.Copy()); copied.ApiConnections[0].ApiKey = "different-synthetic-key";
      Check(options.CliConnections.Count == 3 && options.CliConnections[0].Model == "updated" && options.ApiConnections[0].ApiKey == "synthetic-API-secret-not-in-CLI-store", "TranslationOptions.Copy isolates CLI entries, lists and existing API entries");
      CliConnectionStore.Save(path, new[] { first, second, third });
      var loaded = CliConnectionStore.Load(path);
      Check(loaded.Count == 3, "three independent provider/path settings survive a save and restart");
      Equal(loaded[0], first, "first profile round-trips all CLI switches, model, effort and quoted multiline arguments");
      Equal(loaded[1], second, "another provider retains its own complete draft");
      Equal(loaded[2], third, "another path for the same provider retains its own complete draft");
      Check(!File.ReadAllText(path).Contains("synthetic-API-secret") && !File.ReadAllText(path).Contains("ApiKey"), "CLI serialization cannot copy API credentials from TranslationOptions");
      loaded[0].Arguments = "changed after load";
      Equal(CliConnectionStore.Load(path)[0], first, "returned profiles are detached from the saved file");
      var prior = File.ReadAllBytes(path); second.Arguments = "\u041f\u0443\u0442\u044c \u6d4b\u8bd5 \"first\" \"last\""; second.MinimumChunkCharacters = 0;
      CliConnectionStore.Save(path, new[] { first, second, third });
      Check(File.ReadAllBytes(path + ".bak").SequenceEqual(prior), "atomic replacement backs up the immediately previous complete store");
      Equal(CliConnectionStore.Load(path)[1], second, "Unicode arguments and disabled minimum chunk size round-trip losslessly");
      prior = File.ReadAllBytes(path); second.TimeoutSeconds = 3600; second.ParallelRequests = 8; second.MinimumChunkCharacters = 1000000;
      CliConnectionStore.Save(path, new[] { first, second, third });
      Check(File.ReadAllBytes(path + ".bak").SequenceEqual(prior), "a second replacement refreshes the rolling backup without deleting the destination first");
      Equal(CliConnectionStore.Load(path)[1], second, "maximum supported timing, concurrency and chunk values round-trip");
      Check(!Directory.GetFiles(directory, "*.tmp-*").Any(), "successful saves leave no temporary files");
      var oldDefaults = JsonConvert.DefaultSettings;
      try {
        JsonConvert.DefaultSettings = () => new JsonSerializerSettings { Converters = { new PoisonConverter() }, TypeNameHandling = TypeNameHandling.All };
        CliConnectionStore.Save(path, new[] { first }); Equal(CliConnectionStore.Load(path)[0], first, "store ignores unrelated global JSON converters and type-name policies");
      } finally { JsonConvert.DefaultSettings = oldDefaults; }
      CliConnectionStore.Save(path, new[] { first, replacement });
      Check(CliConnectionStore.Load(path).Count == 1 && CliConnectionStore.Load(path)[0].Model == "after-upsert", "duplicate identities are saved once with the most recent draft");
      var incomplete = new CliConnectionSettings { ProviderId = "custom", Executable = "", ShowButtons = false };
      CliConnectionStore.Save(path, new[] { incomplete }); Equal(CliConnectionStore.Load(path)[0], incomplete, "an incomplete CLI draft can persist while translation buttons are disabled");
      CliConnectionStore.Save(path, new CliConnectionSettings[0]); Check(CliConnectionStore.Load(path).Count == 0, "an explicitly empty valid catalog is saved as an empty list");
      ReadFailure(path, "{ PRIVATE-SENTINEL malformed", "malformed JSON");
      var corrupt = File.ReadAllBytes(path); var backup = File.ReadAllBytes(path + ".bak"); SaveFailure(path, first, "saving over a corrupt catalog");
      Check(File.ReadAllBytes(path + ".bak").SequenceEqual(backup), "refused corrupt-store save also preserves the previous backup");
      var failedOptions = new TranslationOptions(); failedOptions.LoadCliConnections(path);
      Check(failedOptions.CliConnections.Count == 0 && failedOptions.CliConfigurationError != null && !failedOptions.CliConfigurationError.Contains("PRIVATE-SENTINEL"), "failed load exposes a safe error separately from the empty draft list");
      Check(failedOptions.Copy().CliConfigurationError == failedOptions.CliConfigurationError, "draft copies retain the corrupt-store warning");
      File.WriteAllText(path, "[]"); failedOptions.LoadCliConnections(path);
      Check(failedOptions.CliConfigurationError == null, "a successful explicit reload clears the load error");
      ReadFailure(path, "[{\"ProviderId\":\"codex\",\"ProviderId\":\"cursor\"}]", "duplicate object properties");
      ReadFailure(path, "[] []", "extra JSON after the catalog");
      ReadFailure(path, "[null]", "null catalog entries");
      ReadFailure(path, "[{\"ApiKey\":\"PRIVATE-SENTINEL\"}]", "unrecognized credential fields");
      ReadFailure(path, "[{\"ParallelRequests\":9}]", "out-of-range concurrency");
      ReadFailure(path, "[{\"MinimumChunkCharacters\":-1}]", "out-of-range minimum chunk size");
      ReadFailure(path, "[{\"Executable\":\"bad\\npath\"}]", "newline executable injection");
      ReadFailure(path, "[" + string.Join(",", Enumerable.Repeat("{}", CliConnectionStore.MaximumConnections + 1)) + "]", "excess catalog entries");
      using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write)) stream.SetLength(CliConnectionStore.MaximumFileBytes + 1L);
      var oversizeFailed = false; try { CliConnectionStore.Load(path); } catch (InvalidDataException) { oversizeFailed = true; }
      Check(oversizeFailed && new FileInfo(path).Length == CliConnectionStore.MaximumFileBytes + 1L, "an oversized file is rejected before parsing and remains intact");
      File.Delete(path); CliConnectionStore.Save(path, new[] { first });
      foreach (var invalid in new[] {
        new CliConnectionSettings { TimeoutSeconds = 9 }, new CliConnectionSettings { ParallelRequests = 0 },
        new CliConnectionSettings { MinimumChunkCharacters = 1000001 }, new CliConnectionSettings { Arguments = new string('a', 32766) },
        new CliConnectionSettings { Arguments = "before\0after" }, new CliConnectionSettings { Model = "before\rafter" }
      }) SaveFailure(path, invalid, "invalid draft " + checks);
      var beforeLock = File.ReadAllBytes(path); var lockFailed = false;
      using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) {
        try { CliConnectionStore.Save(path, new[] { second }); } catch (IOException) { lockFailed = true; }
      }
      Check(lockFailed && File.ReadAllBytes(path).SequenceEqual(beforeLock), "a locked store cannot be replaced or silently reset");
      File.SetAttributes(path, FileAttributes.ReadOnly);
      try { SaveFailure(path, second, "read-only destination"); } finally { File.SetAttributes(path, FileAttributes.Normal); }
      Check(!Directory.GetFiles(directory, "*.tmp-*").Any(), "failed replacements remove only their own temporary files");
      Console.WriteLine("PASS CLI connection store: " + checks + " assertions"); return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    finally {
      var resolved = Path.GetFullPath(directory); var allowed = Path.GetFullPath(temporaryRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
      if (resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
  }
}
