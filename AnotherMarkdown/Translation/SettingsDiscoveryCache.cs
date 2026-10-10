using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AnotherMarkdown.Translation
{
  // Settings may reuse discovery, but translation deliberately calls the raw
  // discovery service so its per-document MCP isolation remains current.
  public sealed class SettingsDiscoveryCache
  {
    public static SettingsDiscoveryCache Shared { get; } = new SettingsDiscoveryCache();
    private const int MaximumEntries = 32;
    private static readonly string[] EnvironmentNames = {
      "PATH", "PATHEXT", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "HOME", "APPDATA", "LOCALAPPDATA",
      "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "SystemRoot", "CODEX_HOME", "CURSOR_HOME", "CURSOR_CONFIG_DIR",
      "CLAUDE_CONFIG_DIR", "GEMINI_CLI_HOME", "OPENCODE_CONFIG", "OPENCODE_CONFIG_DIR", "OLLAMA_MODELS", "OLLAMA_HOST",
      "KIMI_HOME", "KIMI_CONFIG", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME"
    };
    private readonly object gate = new object();
    private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    private readonly Dictionary<string, Flight> flights = new Dictionary<string, Flight>(StringComparer.Ordinal);
    private readonly Func<CancellationToken, Task<List<CliInstallation>>> installationLoader;
    private readonly Func<string, string, CancellationToken, Task<CliModelCatalog>> modelLoader;
    private readonly Func<ApiConnection, int, CancellationToken, Task<CliModelCatalog>> apiModelLoader;
    private readonly Func<DateTime> utcNow;
    private readonly TimeSpan lifetime;
    private readonly TimeSpan installationLifetime;
    private ModelDiskCache modelDisk;
    internal Task PersistenceReady { get; private set; } = Task.CompletedTask;
    private long access;

    internal SettingsDiscoveryCache(Func<CancellationToken, Task<List<CliInstallation>>> installationLoader = null,
      Func<string, string, CancellationToken, Task<CliModelCatalog>> modelLoader = null,
      Func<DateTime> utcNow = null, TimeSpan? lifetime = null,
      Func<ApiConnection, int, CancellationToken, Task<CliModelCatalog>> apiModelLoader = null)
    {
      this.installationLoader = installationLoader ?? (token => Task.FromResult(CliProfiles.DiscoverInstalled(token)));
      this.modelLoader = modelLoader ?? ((provider, executable, token) => new CliModelDiscovery().LoadAsync(provider, executable, token));
      this.apiModelLoader = apiModelLoader ?? ((profile, timeout, token) => new ApiTranslator().LoadModelsAsync(profile, timeout, token));
      this.utcNow = utcNow ?? (() => DateTime.UtcNow);
      this.lifetime = lifetime ?? TimeSpan.FromDays(1);
      this.installationLifetime = lifetime ?? TimeSpan.FromMinutes(5);
      if (this.lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
    }
    public void ConfigureModelPersistence(string path)
    {
      if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Model cache path is missing.", nameof(path));
      var disk = new ModelDiskCache(Path.GetFullPath(path), utcNow, lifetime);
      modelDisk = disk;
      // Read persisted metadata at plugin initialization, away from the UI.
      // Opening Settings can then use only the in-memory dictionary.
      PersistenceReady = Task.Run(() => disk.ReadFresh((key, catalog, created) => {
        lock (gate) {
          if (!ReferenceEquals(modelDisk, disk)) return;
          if (entries.TryGetValue(key, out var current) && current.Created >= created) return;
          entries[key] = new Entry { Value = CloneCatalog(catalog), Created = created, Access = ++access };
          while (entries.Count > MaximumEntries) entries.Remove(entries.OrderBy(pair => pair.Value.Access).First().Key);
        }
      }));
    }

    public bool TryInstallations(out List<CliInstallation> installations, out bool fresh) =>
      Try(InstallationKey(), CloneInstallations, false, out installations, out fresh);
    public Task<List<CliInstallation>> LoadInstallationsAsync(bool refresh, CancellationToken token) =>
      LoadAsync(InstallationKey(), refresh, installationLoader, CloneInstallations, false, token);
    public bool TryModels(string provider, string executable, out CliModelCatalog catalog, out bool fresh) =>
      TryCatalog(ModelKey(provider, executable), out catalog, out fresh);
    internal bool TryModelsInMemory(string provider, string executable, out CliModelCatalog catalog, out bool fresh) =>
      Try(ModelKey(provider, executable), CloneCatalog, true, out catalog, out fresh);
    internal bool TryApiModelsInMemory(ApiConnection connection, out CliModelCatalog catalog, out bool fresh) =>
      Try(ApiModelKey(connection), CloneCatalog, true, out catalog, out fresh);
    public Task<CliModelCatalog> LoadModelsAsync(string provider, string executable, bool refresh, CancellationToken token) =>
      LoadCatalogAsync(ModelKey(provider, executable), refresh, cancellation => modelLoader(provider, executable, cancellation), token);
    public bool TryApiModels(ApiConnection connection, int timeout, out CliModelCatalog catalog, out bool fresh) =>
      TryCatalog(ApiModelKey(connection), out catalog, out fresh);
    public Task<CliModelCatalog> LoadApiModelsAsync(ApiConnection connection, int timeout, bool refresh, CancellationToken token)
    {
      var snapshot = connection?.Copy();
      return LoadCatalogAsync(ApiModelKey(snapshot), refresh,
        cancellation => apiModelLoader(snapshot, timeout, cancellation), token);
    }

    public static string ModelKey(string provider, string executable) =>
      Hash("models-fast-v3", (provider ?? "").Trim().ToLowerInvariant(), NormalizeExecutable(executable), EnvironmentContext());
    internal static string ApiModelKey(ApiConnection connection)
    {
      if (connection == null) throw new ArgumentNullException(nameof(connection));
      // Hash the model-list request identity. The selected model and request
      // timeout do not change that list. Never persist raw credentials or URL.
      return Hash("api-models-metadata-v2", connection.Endpoint, connection.Protocol,
        connection.ApiKey, connection.AuthHeader, connection.AuthPrefix, connection.AdditionalHeadersJson,
        connection.ProxyMode, connection.ProxyAddress, connection.ProxyUsername, connection.ProxyPassword,
        connection.ProxyUseDefaultCredentials.ToString());
    }
    private static string InstallationKey() => Hash("installations", EnvironmentContext());
    private static string EnvironmentContext()
    {
      var context = new StringBuilder();
      foreach (var name in EnvironmentNames) Append(context, Environment.GetEnvironmentVariable(name));
      Append(context, Environment.CurrentDirectory);
      return context.ToString();
    }
    internal static string NormalizeExecutable(string executable)
    {
      var value = (executable ?? "").Trim();
      // Only path/profile variables are read. An arbitrary placeholder must
      // never cause a credential environment variable to be inspected.
      value = Regex.Replace(value, @"%([^%]+)%", match => EnvironmentNames.Contains(match.Groups[1].Value, StringComparer.OrdinalIgnoreCase)
        ? Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? match.Value : match.Value);
      // Lexical Windows normalization avoids legacy Path.GetFullPath expanding
      // 8.3 names through filesystem calls. Bare PATH commands remain names.
      value = NormalizePath(value);
      return value.ToUpperInvariant();
    }
    private static string NormalizePath(string value)
    {
      if (value.IndexOfAny(new[] { '\\', '/' }) < 0) return value;
      value = value.Replace('/', '\\');
      if (value.StartsWith(@"\\?\", StringComparison.Ordinal) || value.StartsWith(@"\\.\", StringComparison.Ordinal)) return value;
      var directory = Environment.CurrentDirectory.Replace('/', '\\');
      if (value.StartsWith("\\", StringComparison.Ordinal) && !value.StartsWith(@"\\", StringComparison.Ordinal)) {
        var drive = Regex.Match(directory, @"^[A-Za-z]:").Value;
        if (drive.Length == 0) return value;
        value = drive + value;
      }
      else if (!value.StartsWith(@"\\", StringComparison.Ordinal) && !Regex.IsMatch(value, @"^[A-Za-z]:\\")) {
        if (Regex.IsMatch(value, @"^[A-Za-z]:")) return value;
        value = directory.TrimEnd('\\') + "\\" + value;
      }
      string root; string remainder;
      if (value.StartsWith(@"\\", StringComparison.Ordinal)) {
        var parts = value.Substring(2).Split('\\');
        if (parts.Length < 2) return value;
        root = @"\\" + parts[0] + "\\" + parts[1] + "\\"; remainder = string.Join("\\", parts.Skip(2));
      }
      else { root = value.Substring(0, 3); remainder = value.Substring(3); }
      var segments = new List<string>();
      foreach (var part in remainder.Split('\\')) {
        if (part.Length == 0 || part == ".") continue;
        if (part == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); }
        else segments.Add(part);
      }
      return root + string.Join("\\", segments);
    }
    private static string Hash(params string[] values)
    {
      var text = new StringBuilder(); foreach (var value in values) Append(text, value);
      using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
    }
    private static void Append(StringBuilder text, string value) => text.Append((value ?? "").Length).Append(':').Append(value);

    private bool IsFresh(Entry entry, bool model)
    { var now = utcNow(); return now >= entry.Created && now - entry.Created < (model ? lifetime : installationLifetime); }
    private bool TryCatalog(string key, out CliModelCatalog catalog, out bool fresh)
    {
      var memory = Try(key, CloneCatalog, true, out catalog, out fresh);
      if (memory && fresh) return true;
      var staleCatalog = catalog;
       if (modelDisk != null && modelDisk.TryGet(key, out var diskCatalog, out var createdUtc)) {
         lock (gate) {
           // A background refresh may have completed while disk I/O ran.
           // Never replace a newer in-memory result with an older snapshot.
           if (entries.TryGetValue(key, out var current) && current.Created >= createdUtc) {
             current.Access = ++access; catalog = CloneCatalog((CliModelCatalog)current.Value);
             fresh = IsFresh(current, true); return true;
           }
           entries[key] = new Entry { Value = CloneCatalog(diskCatalog), Created = createdUtc, Access = ++access };
           catalog = diskCatalog; fresh = true; return true;
         }
       }
      catalog = staleCatalog; fresh = false; return memory;
    }
    private Task<CliModelCatalog> LoadCatalogAsync(string key, bool refresh, Func<CancellationToken, Task<CliModelCatalog>> loader, CancellationToken token)
    {
      token.ThrowIfCancellationRequested();
      if (!refresh && TryCatalog(key, out var catalog, out var fresh) && fresh) return Task.FromResult(catalog);
      return LoadAsync(key, refresh, loader, CloneCatalog, true, token);
    }
    private bool Try<T>(string key, Func<T, T> clone, bool model, out T value, out bool fresh) where T : class
    {
      lock (gate) {
        if (entries.TryGetValue(key, out var entry)) {
          entry.Access = ++access; value = clone((T)entry.Value); fresh = IsFresh(entry, model); return true;
        }
      }
      value = null; fresh = false; return false;
    }

    private async Task<T> LoadAsync<T>(string key, bool refresh, Func<CancellationToken, Task<T>> loader, Func<T, T> clone, bool model, CancellationToken token) where T : class
    {
      token.ThrowIfCancellationRequested();
      Flight flight; var created = false;
      lock (gate) {
        if (!refresh && entries.TryGetValue(key, out var entry) && IsFresh(entry, model)) {
          entry.Access = ++access; return clone((T)entry.Value);
        }
        if (!flights.TryGetValue(key, out flight) || flight.Abandoned || flight.Completed || flight.Completion.Task.IsCompleted) {
          flight = new Flight(); flights[key] = flight; created = true;
        }
        flight.Subscribers++;
      }
      if (created) {
        // A synchronous filesystem scanner must never run on the calling UI
        // thread. ExecuteAsync handles every outcome, including abandoned work.
        _ = Task.Run(() => ExecuteAsync(key, flight, loader, clone, model));
      }
      try {
        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (token.Register(() => canceled.TrySetResult(true))) {
          if (await Task.WhenAny(flight.Completion.Task, canceled.Task).ConfigureAwait(false) == canceled.Task) token.ThrowIfCancellationRequested();
          var result = await flight.Completion.Task.ConfigureAwait(false);
          token.ThrowIfCancellationRequested(); return clone((T)result);
        }
      }
      finally { Detach(key, flight); }
    }

    private async Task ExecuteAsync<T>(string key, Flight flight, Func<CancellationToken, Task<T>> loader, Func<T, T> clone, bool model) where T : class
    {
      try {
        flight.Cancellation.Token.ThrowIfCancellationRequested();
        var value = clone(await loader(flight.Cancellation.Token).ConfigureAwait(false));
        var publish = false; var createdUtc = default(DateTime); var revision = 0L;
        lock (gate) {
          flight.Cancellation.Token.ThrowIfCancellationRequested();
          if (flight.Abandoned) throw new OperationCanceledException();
          publish = flights.TryGetValue(key, out var current) && ReferenceEquals(current, flight);
          if (publish) {
            createdUtc = utcNow(); revision = ++access;
            entries[key] = new Entry { Value = value, Created = createdUtc, Access = revision };
          }
          while (entries.Count > MaximumEntries) entries.Remove(entries.OrderBy(pair => pair.Value.Access).First().Key);
          flight.Completed = true; RemoveFlight(key, flight);
        }
        flight.Completion.TrySetResult(value);
        if (publish && model && modelDisk != null) {
          // Persistence never holds up a completed model lookup or a repaint.
          var saved = CloneCatalog((CliModelCatalog)(object)value);
          _ = Task.Run(() => modelDisk.Save(key, saved, createdUtc, revision));
        }
      }
      catch (OperationCanceledException) { flight.Completion.TrySetCanceled(); }
      catch (Exception error) { flight.Completion.TrySetException(error); }
      finally {
        lock (gate) { flight.Completed = true; RemoveFlight(key, flight); }
        flight.DisposeCancellation();
      }
    }
    private void Detach(string key, Flight flight)
    {
      var cancel = false;
      lock (gate) {
        flight.Subscribers--;
        if (flight.Subscribers == 0 && !flight.Completed) {
          flight.Abandoned = true; RemoveFlight(key, flight); cancel = true;
        }
      }
      if (cancel) flight.Cancel();
    }
    private void RemoveFlight(string key, Flight flight)
    { if (flights.TryGetValue(key, out var current) && ReferenceEquals(current, flight)) flights.Remove(key); }

    private sealed class Entry { public object Value; public DateTime Created; public long Access; }

    // Catalogs are public model metadata. Connection secrets participate only in
    // the SHA-256 cache key and are never serialized to this file.
    private sealed class ModelDiskCache
    {
      private sealed class DiskEntry { public DateTime CreatedUtc; public CliModelCatalog Catalog; }
      private readonly object sync = new object();
      private readonly string path;
      private readonly Func<DateTime> now;
      private readonly TimeSpan lifetime;
      private Dictionary<string, DiskEntry> entries;
      private readonly Dictionary<string, long> latestWrite = new Dictionary<string, long>(StringComparer.Ordinal);
      internal ModelDiskCache(string path, Func<DateTime> now, TimeSpan lifetime)
      { this.path = path; this.now = now; this.lifetime = lifetime; }
      private void Load()
      {
        if (entries != null) return;
        entries = new Dictionary<string, DiskEntry>(StringComparer.Ordinal);
        try {
          var file = new FileInfo(path);
          if (!file.Exists || file.Length > 8000000) return;
          var stored = JsonConvert.DeserializeObject<Dictionary<string, DiskEntry>>(File.ReadAllText(path, Encoding.UTF8));
          if (stored == null) return;
          foreach (var pair in stored) if (pair.Key.Length == 44 && pair.Value?.Catalog?.Models != null &&
              pair.Value.CreatedUtc.Kind == DateTimeKind.Utc && pair.Value.Catalog.Models.Count <= 20000)
            entries[pair.Key] = pair.Value;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException) { }
      }
      internal bool TryGet(string key, out CliModelCatalog catalog, out DateTime createdUtc)
      {
        lock (sync) {
          Load();
          if (entries.TryGetValue(key, out var item) && now() >= item.CreatedUtc && now() - item.CreatedUtc < lifetime) {
            catalog = CloneCatalog(item.Catalog); createdUtc = item.CreatedUtc; return true;
          }
        }
        catalog = null; createdUtc = default(DateTime); return false;
      }
      internal void ReadFresh(Action<string, CliModelCatalog, DateTime> accept)
      {
        lock (sync) {
          Load();
          foreach (var pair in entries)
            if (now() >= pair.Value.CreatedUtc && now() - pair.Value.CreatedUtc < lifetime)
              accept(pair.Key, pair.Value.Catalog, pair.Value.CreatedUtc);
        }
      }
      internal void Save(string key, CliModelCatalog catalog, DateTime createdUtc, long revision)
      {
        lock (sync) {
          var pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
          try {
            Load();
            if (latestWrite.TryGetValue(key, out var previous) && previous > revision) return;
            latestWrite[key] = revision;
            var current = now();
            var metadata = CloneCatalog(catalog);
            metadata.McpServerNames.Clear(); metadata.McpConfigurationRead = false;
            entries[key] = new DiskEntry { CreatedUtc = createdUtc, Catalog = metadata };
            foreach (var expired in entries.Where(pair => current < pair.Value.CreatedUtc || current - pair.Value.CreatedUtc >= lifetime)
                .Select(pair => pair.Key).ToArray()) entries.Remove(expired);
            while (entries.Count > MaximumEntries) entries.Remove(entries.OrderBy(pair => pair.Value.CreatedUtc).First().Key);
            var json = JsonConvert.SerializeObject(entries);
            if (Encoding.UTF8.GetByteCount(json) > 8000000) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(pending, json, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(pending, path, null);
            else File.Move(pending, path);
          }
          catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException) { }
          finally { try { if (File.Exists(pending)) File.Delete(pending); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
      }
    }
    private sealed class Flight
    {
      public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
      public readonly TaskCompletionSource<object> Completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
      public int Subscribers;
      public bool Completed, Abandoned;
      private readonly object cancellationGate = new object();
      private bool disposed;
      public Flight()
      {
        // There may be no subscribers left when an abandoned loader faults.
        _ = Completion.Task.ContinueWith(task => { var observed = task.Exception; }, CancellationToken.None,
          TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
      }
      public void Cancel()
      {
        lock (cancellationGate) if (!disposed) {
          // Cleanup callbacks must not replace the consumer's cancellation
          // outcome. The worker still observes its canceled owned token.
          try { Cancellation.Cancel(); } catch (AggregateException) { }
        }
      }
      public void DisposeCancellation() { lock (cancellationGate) { if (disposed) return; disposed = true; Cancellation.Dispose(); } }
    }

    private static List<CliInstallation> CloneInstallations(List<CliInstallation> source)
    {
      if (source == null) throw new InvalidOperationException("CLI discovery returned no result.");
      return source.Select(value => value == null ? null : new CliInstallation { ProviderId = value.ProviderId, Executable = value.Executable }).ToList();
    }
    private static CliModelCatalog CloneCatalog(CliModelCatalog source)
    {
      if (source == null) throw new InvalidOperationException("Model discovery returned no result.");
      return new CliModelCatalog {
        DefaultModelId = source.DefaultModelId, ConfiguredReasoningEffort = source.ConfiguredReasoningEffort, Note = source.Note,
        McpConfigurationRead = source.McpConfigurationRead, McpServerNames = source.McpServerNames?.ToList() ?? new List<string>(),
        Models = source.Models?.Select(value => value == null ? null : new CliModel {
          Id = value.Id, Name = value.Name, IsDefault = value.IsDefault, BaseModelId = value.BaseModelId, BaseModelName = value.BaseModelName,
          FastModelId = value.FastModelId, FastOnly = value.FastOnly,
          DefaultReasoningEffort = value.DefaultReasoningEffort,
          ReasoningEfforts = value.ReasoningEfforts?.Select(effort => effort == null ? null : new CliReasoningEffort {
            Id = effort.Id, Description = effort.Description, ModelId = effort.ModelId, ModelIds = effort.ModelIds?.ToList() ?? new List<string>()
          }).ToList() ?? new List<CliReasoningEffort>()
        }).ToList() ?? new List<CliModel>()
      };
    }
  }
}
