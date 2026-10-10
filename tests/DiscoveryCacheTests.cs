using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AnotherMarkdown.Translation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class DiscoveryCacheTests
{
  private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
  private static readonly string Executable = Process.GetCurrentProcess().MainModule.FileName;
  private static string scratch, scratchRoot;
  private static int checks;
  private static int Main(string[] args)
  {
    Console.InputEncoding = Utf8; Console.OutputEncoding = Utf8;
    if (args.Length > 0 && args[0] == "app-server") return FakeServer();
    if (args.Length > 0 && args[0] == "exec") return FakeExec(args);
    scratchRoot = Path.GetFullPath(Directory.Exists(@"D:\Temp") ? @"D:\Temp\agent\markdown-ru" : Path.Combine(Path.GetTempPath(), "AnotherMarkdown-tests"));
    scratch = Path.Combine(scratchRoot, "discovery-cache-tests-" + Guid.NewGuid().ToString("N"));
    var oldTemp = Environment.GetEnvironmentVariable("TEMP"); var oldTmp = Environment.GetEnvironmentVariable("TMP");
    var oldLog = Environment.GetEnvironmentVariable("SETTINGS_DISCOVERY_FIXTURE_LOG"); var oldMcp = Environment.GetEnvironmentVariable("SETTINGS_DISCOVERY_FIXTURE_MCP");
    try {
      Directory.CreateDirectory(scratch); Environment.SetEnvironmentVariable("TEMP", scratch); Environment.SetEnvironmentVariable("TMP", scratch);
      Run().GetAwaiter().GetResult(); Console.WriteLine("PASS discovery cache: " + checks + " assertions"); return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    finally {
      Environment.SetEnvironmentVariable("TEMP", oldTemp); Environment.SetEnvironmentVariable("TMP", oldTmp);
      Environment.SetEnvironmentVariable("SETTINGS_DISCOVERY_FIXTURE_LOG", oldLog); Environment.SetEnvironmentVariable("SETTINGS_DISCOVERY_FIXTURE_MCP", oldMcp);
      var resolved = Path.GetFullPath(scratch);
      if (Directory.Exists(resolved) && resolved.StartsWith(scratchRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Directory.Delete(resolved, true);
    }
  }
  private static async Task Run()
  {
    await OffCallingThread(); await LifetimeAndRefresh(); await PersistentModelCache(); await EmptyResults(); await DeepCopies();
    await JoinedInstallations(); await OneSubscriberCancels(); await AllSubscribersCancel(); await ReplacementFlight();
    await FailedRefresh(); await CanceledRefresh(); await BoundedEntries(); await EnvironmentKeys(); await RawDiscoveryStaysFresh();
  }
  private static void Check(bool condition, string label)
  { if (!condition) throw new Exception("FAIL: " + label); checks++; Console.WriteLine("PASS " + label); }
  private static async Task<Exception> Failure(Func<Task> action)
  { try { await action(); } catch (Exception error) { return error; } throw new Exception("Expected a discovery failure."); }
  private static async Task<T> Within<T>(Task<T> task, int milliseconds = 10000)
  { if (await Task.WhenAny(task, Task.Delay(milliseconds)) != task) throw new TimeoutException("Discovery fixture did not complete."); return await task; }
  private static List<CliInstallation> Installations(string id = "cursor") => new List<CliInstallation> {
    new CliInstallation { ProviderId = id, Executable = @"D:\fixture\" + id + ".exe" }
  };
  private static CliModelCatalog Models(string id = "fixture") => new CliModelCatalog {
    DefaultModelId = id, ConfiguredReasoningEffort = "high", Note = "fixture note", McpConfigurationRead = true,
    McpServerNames = new List<string> { "fixture-mcp" }, Models = new List<CliModel> {
      new CliModel { Id = id, Name = "Fixture model", IsDefault = true, BaseModelId = "fixture-base", BaseModelName = "Fixture base", DefaultReasoningEffort = "high", FastModelId = id + "-fast", FastOnly = true,
        ReasoningEfforts = new List<CliReasoningEffort> { new CliReasoningEffort { Id = "high", Description = "fixture effort", ModelId = id, ModelIds = new List<string> { id, id + "-alias" } } } }
    }
  };
  private sealed class Clock
  {
    private long ticks = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
    public DateTime Now() => new DateTime(Interlocked.Read(ref ticks), DateTimeKind.Utc);
    public void Advance(TimeSpan amount) => Interlocked.Add(ref ticks, amount.Ticks);
  }
  private sealed class Gate<T> where T : class
  {
    public readonly TaskCompletionSource<T> Result;
    public readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource<bool> Canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool ignoreCancellation;
    public CancellationToken Token;
    public Task<T> Running;
    public Gate(bool ignoreCancellation = false, bool asynchronousCompletion = true)
    { this.ignoreCancellation = ignoreCancellation; Result = new TaskCompletionSource<T>(asynchronousCompletion ? TaskCreationOptions.RunContinuationsAsynchronously : TaskCreationOptions.None); }
    public Task<T> Start(CancellationToken token) { Token = token; return Running = Run(token); }
    private async Task<T> Run(CancellationToken token)
    {
      using (token.Register(() => Canceled.TrySetResult(true))) {
        Started.TrySetResult(true);
        if (!ignoreCancellation && await Task.WhenAny(Result.Task, Canceled.Task).ConfigureAwait(false) == Canceled.Task) token.ThrowIfCancellationRequested();
        return await Result.Task.ConfigureAwait(false);
      }
    }
  }
  private sealed class NoPostContext : SynchronizationContext
  { public int Posts; public override void Post(SendOrPostCallback callback, object state) { Interlocked.Increment(ref Posts); throw new Exception("A cache continuation returned to the UI context."); } }

  private static async Task OffCallingThread()
  {
    var caller = 0; var worker = 0; var context = new NoPostContext();
    var cache = new SettingsDiscoveryCache(installationLoader: token => { worker = Thread.CurrentThread.ManagedThreadId; return Task.FromResult(Installations()); });
    var handed = new TaskCompletionSource<Task<List<CliInstallation>>>(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() => {
      try { caller = Thread.CurrentThread.ManagedThreadId; SynchronizationContext.SetSynchronizationContext(context); handed.TrySetResult(cache.LoadInstallationsAsync(false, CancellationToken.None)); }
      catch (Exception error) { handed.TrySetException(error); }
    }) { IsBackground = true };
    thread.Start(); var pending = await Within(handed.Task); var result = await Within(pending);
    Check(result.Count == 1 && worker != caller && context.Posts == 0, "a synchronous installation scanner and continuations run outside the caller UI thread");
  }
  private static async Task LifetimeAndRefresh()
  {
    var clock = new Clock(); var calls = 0;
    var cache = new SettingsDiscoveryCache(installationLoader: token => { Interlocked.Increment(ref calls); return Task.FromResult(Installations()); }, utcNow: clock.Now);
    Check(!cache.TryInstallations(out var absent, out var missingFresh) && absent == null && !missingFresh, "an untouched installation cache has no invented result");
    await cache.LoadInstallationsAsync(false, CancellationToken.None);
    Check(cache.TryInstallations(out var first, out var fresh) && fresh && first.Count == 1 && calls == 1, "first installation discovery creates a fresh cached snapshot");
    clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1)); await cache.LoadInstallationsAsync(false, CancellationToken.None);
    Check(calls == 1 && cache.TryInstallations(out first, out fresh) && fresh, "default TTL remains fresh immediately before five minutes");
    clock.Advance(TimeSpan.FromTicks(1));
    Check(cache.TryInstallations(out first, out fresh) && !fresh && first.Count == 1, "expired discovery remains available as stale UI data at the exact TTL");
    await cache.LoadInstallationsAsync(false, CancellationToken.None); await cache.LoadInstallationsAsync(true, CancellationToken.None);
    Check(calls == 3 && cache.TryInstallations(out first, out fresh) && fresh, "expiration reloads and explicit refresh bypasses even fresh completed data");
    using (var canceled = new CancellationTokenSource()) {
      canceled.Cancel(); var error = await Failure(() => cache.LoadInstallationsAsync(false, canceled.Token));
      Check(error is OperationCanceledException && calls == 3, "an already canceled consumer cannot receive a cached success or start discovery");
    }
  }
  private static async Task PersistentModelCache()
  {
    var clock = new Clock(); var path = Path.Combine(scratch, "models.json");
    var cliCalls = 0; var apiCalls = 0;
    Func<string, string, CancellationToken, Task<CliModelCatalog>> cli = (provider, executable, token) => {
      Interlocked.Increment(ref cliCalls); return Task.FromResult(Models("cli-" + cliCalls));
    };
    Func<ApiConnection, int, CancellationToken, Task<CliModelCatalog>> api = (connection, timeout, token) => {
      Interlocked.Increment(ref apiCalls); return Task.FromResult(Models("api-" + apiCalls));
    };
    var profile = new ApiConnection { Endpoint = "https://example.test/v1", ApiKey = "cache-secret-fixture", Model = "first" };
    var first = new SettingsDiscoveryCache(modelLoader: cli, apiModelLoader: api, utcNow: clock.Now);
    first.ConfigureModelPersistence(path);
    await first.LoadModelsAsync("cursor", "fixture.exe", false, CancellationToken.None);
    await first.LoadApiModelsAsync(profile, 30, false, CancellationToken.None);
    using (var canceled = new CancellationTokenSource()) {
      canceled.Cancel();
      Check(await Failure(() => first.LoadApiModelsAsync(profile, 30, false, canceled.Token)) is OperationCanceledException,
        "a canceled API consumer cannot receive a cached catalog success");
    }
    await Within(Task.Run(async () => {
      for (var i = 0; i < 500; i++) {
        if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8).Contains("api-1") && File.ReadAllText(path, Encoding.UTF8).Contains("cli-1")) return true;
        await Task.Delay(10);
      }
      throw new TimeoutException("Persistent model cache was not written.");
    }));
    var bytes = File.ReadAllText(path, Encoding.UTF8);
    Check(!bytes.Contains(profile.ApiKey) && !bytes.Contains(profile.Endpoint), "persistent metadata contains no API key or endpoint");
    var next = new SettingsDiscoveryCache(modelLoader: cli, apiModelLoader: api, utcNow: clock.Now);
    next.ConfigureModelPersistence(path);
    await next.PersistenceReady;
    Check(next.TryModelsInMemory("cursor", "fixture.exe", out var readyCli, out var readyFresh) && readyFresh
      && next.TryApiModelsInMemory(profile, out var readyApi, out readyFresh) && readyFresh,
      "plugin startup loads persisted CLI and API catalogs into the non-I/O path before opening Settings");
    profile.Model = "second";
    Check(next.TryModels("cursor", "fixture.exe", out var cliCatalog, out var fresh) && fresh && cliCatalog.DefaultModelId == "cli-1"
      && next.TryApiModels(profile, 30, out var apiCatalog, out fresh) && fresh && apiCatalog.DefaultModelId == "api-1",
      "CLI and API model metadata survive restart and API model selection without a request");
    clock.Advance(TimeSpan.FromHours(23));
    await next.LoadModelsAsync("cursor", "fixture.exe", false, CancellationToken.None);
    await next.LoadApiModelsAsync(profile, 30, false, CancellationToken.None);
    Check(cliCalls == 1 && apiCalls == 1, "both model catalogs remain fresh inside the twenty-four-hour window");
    await next.LoadApiModelsAsync(profile, 30, true, CancellationToken.None);
    Check(apiCalls == 2, "explicit API Refresh bypasses the fresh model cache");
    clock.Advance(TimeSpan.FromHours(1));
    Check(first.TryModels("cursor", "fixture.exe", out var staleCli, out fresh) && !fresh && staleCli.DefaultModelId == "cli-1",
      "expired disk metadata cannot erase the stale in-memory model catalog shown during refresh");
    var afterExpiry = new SettingsDiscoveryCache(modelLoader: cli, apiModelLoader: api, utcNow: clock.Now);
    afterExpiry.ConfigureModelPersistence(path);
    await afterExpiry.LoadModelsAsync("cursor", "fixture.exe", false, CancellationToken.None);
    Check(cliCalls == 2, "persisted CLI metadata expires exactly twenty-four hours after its original fetch");
  }
  private static async Task EmptyResults()
  {
    var installs = 0; var models = 0;
    var cache = new SettingsDiscoveryCache(token => { Interlocked.Increment(ref installs); return Task.FromResult(new List<CliInstallation>()); },
      (provider, executable, token) => { Interlocked.Increment(ref models); return Task.FromResult(new CliModelCatalog { Note = "honest empty catalog" }); });
    await cache.LoadInstallationsAsync(false, CancellationToken.None); await cache.LoadInstallationsAsync(false, CancellationToken.None);
    await cache.LoadModelsAsync("custom", "missing-fixture.exe", false, CancellationToken.None); await cache.LoadModelsAsync("custom", "missing-fixture.exe", false, CancellationToken.None);
    Check(installs == 1 && cache.TryInstallations(out var empty, out var fresh) && fresh && empty.Count == 0, "a successful empty installation list is valid cached data");
    Check(models == 1 && cache.TryModels("custom", "missing-fixture.exe", out var catalog, out fresh) && fresh && catalog.Models.Count == 0 && catalog.Note == "honest empty catalog", "an honest empty model catalog does not trigger repeated discovery");
  }
  private static async Task DeepCopies()
  {
    var originalInstallations = Installations(); var originalModels = Models();
    var cache = new SettingsDiscoveryCache(token => Task.FromResult(originalInstallations), (p, e, t) => Task.FromResult(originalModels));
    var loadedInstallations = await cache.LoadInstallationsAsync(false, CancellationToken.None);
    var loaded = await cache.LoadModelsAsync("cursor", "fixture.exe", false, CancellationToken.None);
    originalInstallations[0].Executable = "mutated-source.exe"; originalModels.Models[0].Name = "mutated-source";
    originalModels.Models[0].ReasoningEfforts[0].ModelIds.Clear(); originalModels.McpServerNames.Clear();
    loadedInstallations[0].ProviderId = "mutated-consumer"; loaded.Models[0].Id = "mutated-consumer";
    loaded.Models[0].ReasoningEfforts[0].Description = "mutated-consumer"; loaded.McpServerNames.Add("mutated-consumer");
    Check(cache.TryInstallations(out var installations, out var fresh) && fresh && installations[0].ProviderId == "cursor" && installations[0].Executable == @"D:\fixture\cursor.exe", "installation objects are cloned independently on put and get");
    Check(cache.TryModels("cursor", "fixture.exe", out var copy, out fresh) && fresh && copy.Models[0].Id == "fixture" && copy.Models[0].Name == "Fixture model"
      && copy.Models[0].ReasoningEfforts[0].Description == "fixture effort" && copy.Models[0].ReasoningEfforts[0].ModelIds.SequenceEqual(new[] { "fixture", "fixture-alias" })
      && copy.McpServerNames.SequenceEqual(new[] { "fixture-mcp" }), "model effort aliases and MCP names are deep copied from source and every consumer");
    Check(copy.DefaultModelId == "fixture" && copy.ConfiguredReasoningEffort == "high" && copy.Note == "fixture note" && copy.McpConfigurationRead
      && copy.Models[0].IsDefault && copy.Models[0].BaseModelId == "fixture-base" && copy.Models[0].BaseModelName == "Fixture base" && copy.Models[0].DefaultReasoningEffort == "high" && copy.Models[0].FastModelId == "fixture-fast" && copy.Models[0].FastOnly, "deep copies retain discovery and grouping metadata including a Fast-only capability");
    copy.Models.Clear(); installations.Clear();
    Check((await cache.LoadModelsAsync("cursor", "fixture.exe", false, CancellationToken.None)).Models.Count == 1
      && (await cache.LoadInstallationsAsync(false, CancellationToken.None)).Count == 1, "mutating a cached lookup never poisons a later load");
  }
  private static async Task JoinedInstallations()
  {
    var work = new Gate<List<CliInstallation>>(); var calls = 0;
    var cache = new SettingsDiscoveryCache(token => { Interlocked.Increment(ref calls); return work.Start(token); });
    var first = cache.LoadInstallationsAsync(false, CancellationToken.None); await Within(work.Started.Task);
    var second = cache.LoadInstallationsAsync(true, CancellationToken.None); work.Result.TrySetResult(Installations());
    var values = await Within(Task.WhenAll(first, second)); values[0][0].Executable = "consumer-only.exe";
    Check(calls == 1 && values[1][0].Executable != "consumer-only.exe", "installation refresh joins an active flight and subscribers receive independent snapshots");
  }
  private static async Task OneSubscriberCancels()
  {
    var work = new Gate<CliModelCatalog>(); var calls = 0;
    var cache = new SettingsDiscoveryCache(modelLoader: (p, e, t) => { Interlocked.Increment(ref calls); return work.Start(t); });
    using (var cancel = new CancellationTokenSource()) {
      var first = cache.LoadModelsAsync("cursor", "fixture.exe", false, cancel.Token); await Within(work.Started.Task);
      var second = cache.LoadModelsAsync("cursor", "fixture.exe", true, CancellationToken.None);
      cancel.Cancel(); var error = await Within(Failure(async () => { await first; }));
      Check(error is OperationCanceledException && !work.Token.IsCancellationRequested && !second.IsCompleted, "closing one form cancels only its wait while a peer keeps the model flight alive");
      work.Result.TrySetResult(Models()); var result = await Within(second);
      Check(calls == 1 && result.Models.Count == 1 && cache.TryModels("cursor", "fixture.exe", out var cached, out var fresh) && fresh, "the remaining peer completes and caches the shared successful result");
    }
  }
  private static async Task AllSubscribersCancel()
  {
    var old = new Gate<CliModelCatalog>(); var replacement = new Gate<CliModelCatalog>(); var calls = 0;
    var cache = new SettingsDiscoveryCache(modelLoader: (p, e, t) => Interlocked.Increment(ref calls) == 1 ? old.Start(t) : replacement.Start(t));
    using (var firstCancel = new CancellationTokenSource())
    using (var secondCancel = new CancellationTokenSource()) {
      var first = cache.LoadModelsAsync("cursor", "fixture.exe", false, firstCancel.Token); await Within(old.Started.Task);
      var second = cache.LoadModelsAsync("cursor", "fixture.exe", false, secondCancel.Token);
      firstCancel.Cancel(); await Within(Failure(async () => { await first; }));
      Check(!old.Token.IsCancellationRequested, "shared discovery stays alive until the final subscriber detaches");
      secondCancel.Cancel(); var error = await Within(Failure(async () => { await second; })); await Within(old.Canceled.Task);
      Check(error is OperationCanceledException && old.Token.IsCancellationRequested && !cache.TryModels("cursor", "fixture.exe", out var missing, out var fresh), "the final canceled consumer cancels owned discovery and never caches a canceled result");
      var next = cache.LoadModelsAsync("cursor", "fixture.exe", false, CancellationToken.None); await Within(replacement.Started.Task);
      replacement.Result.TrySetResult(Models("replacement"));
      Check(calls == 2 && (await Within(next)).DefaultModelId == "replacement", "a new consumer does not join a canceled flight");
    }
  }
  private static async Task ReplacementFlight()
  {
    var old = new Gate<CliModelCatalog>(true, false); var current = new Gate<CliModelCatalog>(false, false); var calls = 0;
    var cache = new SettingsDiscoveryCache(modelLoader: (p, e, t) => {
      var number = Interlocked.Increment(ref calls); if (number > 2) throw new Exception("Obsolete finally removed the replacement flight."); return number == 1 ? old.Start(t) : current.Start(t);
    });
    using (var cancel = new CancellationTokenSource()) {
      var first = cache.LoadModelsAsync("cursor", "fixture.exe", false, cancel.Token); await Within(old.Started.Task);
      cancel.Cancel(); await Within(Failure(async () => { await first; }));
      var replacement = cache.LoadModelsAsync("cursor", "fixture.exe", false, CancellationToken.None); await Within(current.Started.Task);
      // Inline completion makes the obsolete loader's continuation run before
      // the next join, while the replacement remains held at its own gate.
      old.Result.TrySetResult(Models("obsolete")); await Within(old.Running);
      var peer = cache.LoadModelsAsync("cursor", "fixture.exe", true, CancellationToken.None);
      Check(calls == 2 && !peer.IsCompleted && !cache.TryModels("cursor", "fixture.exe", out var catalog, out var fresh), "an abandoned successful loader neither poisons the cache nor removes its replacement flight");
      current.Result.TrySetResult(Models("current")); var values = await Within(Task.WhenAll(replacement, peer));
      Check(calls == 2 && values.All(v => v.DefaultModelId == "current"), "replacement and later refresh share the current owned flight");
    }
  }
  private static async Task FailedRefresh()
  {
    var clock = new Clock(); var calls = 0;
    var cache = new SettingsDiscoveryCache(modelLoader: (p, e, t) => {
      var number = Interlocked.Increment(ref calls); if (number == 2) throw new InvalidOperationException("EXPECTED_REFRESH_FAILURE"); return Task.FromResult(Models("version-" + number));
    }, utcNow: clock.Now, lifetime: TimeSpan.FromMinutes(1));
    await cache.LoadModelsAsync("cursor", "fixture.exe", false, CancellationToken.None); clock.Advance(TimeSpan.FromMinutes(2));
    var error = await Failure(() => cache.LoadModelsAsync("cursor", "fixture.exe", true, CancellationToken.None));
    Check(error is InvalidOperationException && cache.TryModels("cursor", "fixture.exe", out var stale, out var fresh) && !fresh && stale.DefaultModelId == "version-1", "failed refresh preserves the old stale snapshot without renewing its TTL");
    Check((await cache.LoadModelsAsync("cursor", "fixture.exe", false, CancellationToken.None)).DefaultModelId == "version-3" && calls == 3, "a faulted model flight cannot poison the next retry");
    var nullCache = new SettingsDiscoveryCache(modelLoader: (p, e, t) => Task.FromResult<CliModelCatalog>(null));
    Check(await Failure(() => nullCache.LoadModelsAsync("cursor", "fixture.exe", false, CancellationToken.None)) is InvalidOperationException
      && !nullCache.TryModels("cursor", "fixture.exe", out stale, out fresh), "a missing loader result is a fault rather than an invented empty cached success");
  }
  private static async Task CanceledRefresh()
  {
    var clock = new Clock(); var work = new Gate<CliModelCatalog>(true, false); var calls = 0;
    var cache = new SettingsDiscoveryCache(modelLoader: (p, e, t) => Interlocked.Increment(ref calls) == 1 ? Task.FromResult(Models("old")) : work.Start(t), utcNow: clock.Now, lifetime: TimeSpan.FromSeconds(1));
    await cache.LoadModelsAsync("cursor", "fixture.exe", false, CancellationToken.None); clock.Advance(TimeSpan.FromSeconds(2));
    using (var cancel = new CancellationTokenSource()) {
      var pending = cache.LoadModelsAsync("cursor", "fixture.exe", true, cancel.Token); await Within(work.Started.Task);
      cancel.Cancel(); await Within(Failure(async () => { await pending; })); work.Result.TrySetResult(Models("late-canceled")); await Within(work.Running);
      Check(cache.TryModels("cursor", "fixture.exe", out var stale, out var fresh) && !fresh && stale.DefaultModelId == "old", "even a late successful abandoned refresh preserves the old stale data");
    }
  }
  private static async Task BoundedEntries()
  {
    var cache = new SettingsDiscoveryCache(token => Task.FromResult(Installations()), (p, e, t) => Task.FromResult(Models(e)));
    for (var i = 0; i < 32; i++) await cache.LoadModelsAsync("cursor", "fixture-" + i + ".exe", false, CancellationToken.None);
    Check(cache.TryModels("cursor", "fixture-0.exe", out var catalog, out var fresh) && fresh, "the completed cache can retain thirty-two entries");
    await cache.LoadModelsAsync("cursor", "fixture-32.exe", false, CancellationToken.None);
    Check(cache.TryModels("cursor", "fixture-0.exe", out catalog, out fresh) && !cache.TryModels("cursor", "fixture-1.exe", out catalog, out fresh)
      && cache.TryModels("cursor", "fixture-32.exe", out catalog, out fresh), "the thirty-third model evicts the least recently used entry rather than a recently read one");
    await cache.LoadInstallationsAsync(false, CancellationToken.None);
    Check(!cache.TryModels("cursor", "fixture-2.exe", out catalog, out fresh) && cache.TryInstallations(out var installations, out fresh), "installation and model snapshots share the total thirty-two-entry bound");
  }
  private static async Task EnvironmentKeys()
  {
    var oldPath = Environment.GetEnvironmentVariable("PATH"); var oldHome = Environment.GetEnvironmentVariable("CODEX_HOME");
    var oldHost = Environment.GetEnvironmentVariable("OLLAMA_HOST"); var oldDirectory = Environment.CurrentDirectory;
    var first = Path.Combine(scratch, "first-profile"); var second = Path.Combine(scratch, "second-profile"); var calls = 0;
    var cache = new SettingsDiscoveryCache(token => Task.FromResult(Installations()), (p, e, t) => { Interlocked.Increment(ref calls); return Task.FromResult(Models()); });
    try {
      Environment.SetEnvironmentVariable("PATH", first + ";" + second); Environment.SetEnvironmentVariable("CODEX_HOME", first);
      var key = SettingsDiscoveryCache.ModelKey("cursor", "fixture.exe");
      Check(key == SettingsDiscoveryCache.ModelKey(" CURSOR ", "FIXTURE.EXE") && key != SettingsDiscoveryCache.ModelKey("codex", "fixture.exe"), "model keys normalize Windows executable casing but isolate providers");
      Check(SettingsDiscoveryCache.ModelKey("cursor", "%CODEX_HOME%\\agent.exe") == SettingsDiscoveryCache.ModelKey("cursor", Path.Combine(first, "agent.exe")), "model key expands only known nonsecret profile path placeholders without resolving files");
      await cache.LoadModelsAsync("cursor", "fixture.exe", false, CancellationToken.None); await cache.LoadInstallationsAsync(false, CancellationToken.None);
      Environment.SetEnvironmentVariable("PATH", second + ";" + first);
      Check(key != SettingsDiscoveryCache.ModelKey("cursor", "fixture.exe") && !cache.TryModels("cursor", "fixture.exe", out var catalog, out var fresh)
        && !cache.TryInstallations(out var installations, out fresh), "ordered PATH changes invalidate both installation and model lookup contexts");
      await cache.LoadModelsAsync("cursor", "fixture.exe", false, CancellationToken.None); var changedPath = SettingsDiscoveryCache.ModelKey("cursor", "fixture.exe");
      Environment.SetEnvironmentVariable("CODEX_HOME", second);
      Check(changedPath != SettingsDiscoveryCache.ModelKey("cursor", "fixture.exe") && !cache.TryModels("cursor", "fixture.exe", out catalog, out fresh), "a changed native CLI profile home cannot reuse another profile catalog");
      var profileKey = SettingsDiscoveryCache.ModelKey("cursor", "fixture.exe"); Directory.CreateDirectory(second); Environment.CurrentDirectory = second;
      Check(profileKey != SettingsDiscoveryCache.ModelKey("cursor", "fixture.exe") && SettingsDiscoveryCache.ModelKey("cursor", "missing\\relative.exe") == SettingsDiscoveryCache.ModelKey("cursor", Path.Combine(second, "missing", "relative.exe")), "relative executable lookup context includes the current directory through pure path operations");
      Environment.CurrentDirectory = oldDirectory; Environment.SetEnvironmentVariable("PATH", first + ";" + second); Environment.SetEnvironmentVariable("CODEX_HOME", first);
      Check(cache.TryModels("cursor", "fixture.exe", out catalog, out fresh) && fresh && calls == 2, "restoring the exact environment retrieves its own prior snapshot without cross-profile contamination");
      Environment.SetEnvironmentVariable("OLLAMA_HOST", "http://127.0.0.1:1"); var hostKey = SettingsDiscoveryCache.ModelKey("ollama", "fixture.exe");
      Environment.SetEnvironmentVariable("OLLAMA_HOST", "http://127.0.0.1:2");
      Check(hostKey != SettingsDiscoveryCache.ModelKey("ollama", "fixture.exe"), "a changed Ollama server cannot reuse another server's installed model catalog");
    }
    finally { Environment.CurrentDirectory = oldDirectory; Environment.SetEnvironmentVariable("PATH", oldPath); Environment.SetEnvironmentVariable("CODEX_HOME", oldHome); Environment.SetEnvironmentVariable("OLLAMA_HOST", oldHost); }
  }
  private static void Log(JObject value)
  {
    var directory = Environment.GetEnvironmentVariable("SETTINGS_DISCOVERY_FIXTURE_LOG");
    if (!string.IsNullOrEmpty(directory)) File.WriteAllText(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"), value.ToString(Formatting.None), Utf8);
  }
  private static int FakeServer()
  {
    Log(new JObject { ["kind"] = "server" }); string line;
    while ((line = Console.ReadLine()) != null) {
      if (string.IsNullOrWhiteSpace(line.TrimStart('\uFEFF'))) continue;
      var request = JObject.Parse(line); if (request["id"] == null) continue; var method = (string)request["method"]; var result = new JObject();
      if (method == "model/list") result = new JObject { ["data"] = new JArray(new JObject { ["model"] = "fixture", ["displayName"] = "Fixture", ["isDefault"] = true }) };
      if (method == "config/read") result = new JObject { ["config"] = new JObject { ["model"] = "fixture", ["mcp_servers"] = new JObject { [Environment.GetEnvironmentVariable("SETTINGS_DISCOVERY_FIXTURE_MCP")] = new JObject { ["enabled"] = true } } } };
      Console.WriteLine(new JObject { ["id"] = request["id"], ["result"] = result }.ToString(Formatting.None));
    }
    return 0;
  }
  private static int FakeExec(string[] args)
  {
    var prompt = Console.In.ReadToEnd(); var isolated = args.Contains("mcp_servers." + Environment.GetEnvironmentVariable("SETTINGS_DISCOVERY_FIXTURE_MCP") + ".enabled=false");
    var output = Array.IndexOf(args, "--output-last-message"); if (!isolated || output < 0 || !prompt.Contains("DOCUMENT_")) return 39;
    File.WriteAllText(args[output + 1], "# fixture translation", Utf8); Log(new JObject { ["kind"] = "exec", ["isolated"] = isolated }); return 0;
  }
  private static async Task RawDiscoveryStaysFresh()
  {
    var directory = Path.Combine(scratch, "raw-discovery"); Directory.CreateDirectory(directory);
    Environment.SetEnvironmentVariable("SETTINGS_DISCOVERY_FIXTURE_LOG", directory); Environment.SetEnvironmentVariable("SETTINGS_DISCOVERY_FIXTURE_MCP", "old_fixture");
    await Within(SettingsDiscoveryCache.Shared.LoadModelsAsync("codex", Executable, true, CancellationToken.None), 15000);
    Environment.SetEnvironmentVariable("SETTINGS_DISCOVERY_FIXTURE_MCP", "current_fixture");
    var cached = await SettingsDiscoveryCache.Shared.LoadModelsAsync("codex", Executable, false, CancellationToken.None);
    var raw = new CliModelDiscovery(); var first = await Within(raw.LoadAsync("codex", Executable, CancellationToken.None), 15000);
    var second = await Within(raw.LoadAsync("codex", Executable, CancellationToken.None), 15000);
    Check(cached.McpServerNames.SequenceEqual(new[] { "old_fixture" }) && first.McpServerNames.SequenceEqual(new[] { "current_fixture" })
      && second.McpServerNames.SequenceEqual(new[] { "current_fixture" }), "raw model discovery stays fresh after a settings catalog has been cached");
    var options = CliProfiles.Defaults("codex", Executable); options.TimeoutSeconds = 15;
    Check(await Within(new CliTranslator().TranslateAsync("# source", options, CancellationToken.None), 15000) == "# fixture translation"
      && await Within(new CliTranslator().TranslateAsync("# source", options, CancellationToken.None), 15000) == "# fixture translation", "translation obtains fresh MCP isolation for every document despite a warm settings cache");
    var logs = Directory.GetFiles(directory, "*.json").Select(path => JObject.Parse(File.ReadAllText(path, Utf8))).ToList();
    Check(logs.Count(value => (string)value["kind"] == "server") == 5 && logs.Count(value => (string)value["kind"] == "exec" && (bool)value["isolated"]) == 2,
      "source-only fake native CLI proves settings reuse one discovery while raw discovery and translation bypass it");
  }
}
