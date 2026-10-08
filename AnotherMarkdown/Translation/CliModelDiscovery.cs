using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnotherMarkdown.Translation
{
  public sealed class CliReasoningEffort
  {
    public string Id { get; set; }
    public string Description { get; set; }
    public string ModelId { get; set; }
    public List<string> ModelIds { get; set; } = new List<string>();
    public override string ToString() => string.IsNullOrEmpty(Id) ? Description ?? "CLI default" : Id == "xhigh" ? "Extra High (xhigh)" : Id;
  }

  public sealed class CliModel
  {
    public string Id { get; set; }
    public string Name { get; set; }
    public bool IsDefault { get; set; }
    public string BaseModelId { get; set; }
    public string BaseModelName { get; set; }
    public string DefaultReasoningEffort { get; set; }
    public List<CliReasoningEffort> ReasoningEfforts { get; set; } = new List<CliReasoningEffort>();

    public static string CleanDisplayName(string value)
    {
      var label = Regex.Replace(value ?? "", @"\b(?:no[\s_-]+)?(?:thinking|zdr)\b", "", RegexOptions.IgnoreCase);
      label = Regex.Replace(label, @"\([\s,;/|_-]*\)|\[[\s,;/|_-]*\]", "");
      label = Regex.Replace(label, @"([_-])(?:\s*[_-])+", "$1");
      return Regex.Replace(label, @"\s{2,}", " ").Trim(' ', '-', '_');
    }

    public override string ToString() => CleanDisplayName(string.IsNullOrWhiteSpace(Name) ? Id : Name);
  }

  public sealed class CliModelCatalog
  {
    public List<CliModel> Models { get; set; } = new List<CliModel>();
    public string DefaultModelId { get; set; }
    public string ConfiguredReasoningEffort { get; set; }
    public string Note { get; set; }
    public List<string> McpServerNames { get; set; } = new List<string>();
    public bool McpConfigurationRead { get; set; }
  }

  public sealed class CliModelDiscovery
  {
    private sealed class CursorVariant
    {
      public CliModel Model;
      public string BaseId, Name, Effort, Context;
      public bool Thinking, NoThinking;
    }
    private const int TimeoutSeconds = 25;
    private const int MaximumLineCharacters = 2000000;
    private const int MaximumOutputCharacters = 8000000;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Regex Ansi = new Regex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\))", RegexOptions.Compiled);
    private static readonly Regex CursorRow = new Regex(@"^([A-Za-z0-9][A-Za-z0-9_.:/-]*)\s+-\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex OpenCodeRow = new Regex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][^\s]*$", RegexOptions.Compiled);
    private static readonly Regex OllamaRow = new Regex(@"^(\S+)\s+[A-Fa-f0-9]{12,64}\s+\S+\s+.+$", RegexOptions.Compiled);
    private static readonly Regex AgyRow = new Regex(@"^([a-z0-9][a-z0-9_.:/-]*)\s+(.+)$", RegexOptions.Compiled);

    public Task<CliModelCatalog> LoadAsync(string providerId, string executable, CancellationToken cancellation) =>
      Task.Run(() => LoadCoreAsync(providerId, executable, cancellation), cancellation);

    private async Task<CliModelCatalog> LoadCoreAsync(string providerId, string executable, CancellationToken cancellation)
    {
      cancellation.ThrowIfCancellationRequested();
      var provider = (providerId ?? "").Trim().ToLowerInvariant();
      if (provider == "codex") return await LoadCodexAsync(executable, cancellation).ConfigureAwait(false);
      if (provider != "cursor" && provider != "opencode" && provider != "ollama" && provider != "agy" && provider != "kimi")
        return new CliModelCatalog { Note = "У этого CLI нет поддерживаемой команды списка моделей. Можно использовать модель из настроек CLI или указать её ID в дополнительных параметрах." };

      var arguments = provider == "ollama" ? "list" : provider == "kimi" ? "provider list --json" : "models";
      var result = await CliCommand.RunAsync(executable, arguments, null,
        TimeoutSeconds, cancellation, providerId: provider).ConfigureAwait(false);
      cancellation.ThrowIfCancellationRequested();
      if (result.ExitCode != 0) {
        var detail = provider == "kimi" ? "" : CleanText(string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError).Trim();
        if (detail.Length > 1500) detail = detail.Substring(detail.Length - 1500);
        var message = provider + " model discovery failed (exit " + result.ExitCode + ").";
        if (provider == "ollama") message += " Check that the Ollama service is available.";
        throw new InvalidOperationException(message + (detail.Length == 0 ? "" : "\r\n" + detail));
      }

      var catalog = ParseCommandOutput(provider, result.StandardOutput, cancellation);
      var modelCount = catalog.Models.Select(model => model.BaseModelId ?? model.Id).Distinct(StringComparer.Ordinal).Count();
      catalog.Note = catalog.Models.Count == 0
        ? (provider == "ollama" ? "В Ollama нет установленных моделей." : "CLI не вернул распознаваемый список моделей.")
        : "Моделей получено от CLI: " + modelCount;
      return catalog;
    }

    internal static CliModelCatalog ParseCommandOutput(string provider, string output, CancellationToken cancellation)
    {
      var catalog = new CliModelCatalog();
      var models = new Dictionary<string, CliModel>(StringComparer.Ordinal);
      if (provider == "kimi") {
        JObject root;
        try { root = JObject.Parse(output); }
        catch (JsonException) { throw new InvalidOperationException("Kimi Code вернул нераспознаваемый список моделей."); }
        // The provider table can contain credentials. Retain only model aliases.
        var aliases = root["models"] as JObject;
        if (aliases != null) foreach (var property in aliases.Properties()) {
          cancellation.ThrowIfCancellationRequested();
          if (property.Value is JObject model)
            AddModel(catalog, models, property.Name, model["model"]?.Type == JTokenType.String ? (string)model["model"] : property.Name, false);
        }
        return catalog;
      }
      using (var reader = new StringReader(CleanText(output))) {
        string raw;
        while ((raw = reader.ReadLine()) != null) {
          cancellation.ThrowIfCancellationRequested();
          var line = raw.Trim();
          if (line.Length == 0) continue;
          string id, name;
          var isDefault = false;
          if (provider == "cursor") {
            var row = CursorRow.Match(line);
            if (!row.Success) continue;
            id = row.Groups[1].Value; name = row.Groups[2].Value.Trim();
            const string marker = "(default)";
            if (name.EndsWith(marker, StringComparison.OrdinalIgnoreCase)) {
              isDefault = true; name = name.Substring(0, name.Length - marker.Length).TrimEnd();
            }
          }
          else if (provider == "agy") {
            var row = AgyRow.Match(line);
            if (!row.Success) continue;
            id = row.Groups[1].Value; name = row.Groups[2].Value.Trim();
          }
          else if (provider == "opencode") {
            if (!OpenCodeRow.IsMatch(line)) continue;
            id = name = line;
          }
          else {
            var row = OllamaRow.Match(line);
            if (!row.Success) continue;
            id = name = row.Groups[1].Value;
          }
          AddModel(catalog, models, id, name, isDefault);
        }
      }
      if (provider == "cursor") ReadCursorReasoning(catalog);
      return catalog;
    }

    private static void ReadCursorReasoning(CliModelCatalog catalog)
    {
      // These aliases come from the account catalog. Metadata groups the model
      // picker; commands always use an advertised, unchanged alias.
      catalog.Models.RemoveAll(model => model.Id.EndsWith("-fast", StringComparison.OrdinalIgnoreCase));
      var suffix = new Regex(@"^(?<base>.+?)-(?<effort>extra-high|xhigh|minimal|none|low|medium|high|max)(?<variant>(?:-(?:thinking|context|[0-9]+(?:\.[0-9]+)?[km]))*)$", RegexOptions.IgnoreCase);
      var labelEffort = new Regex(@"\b(extra\s+high|minimal|none|low|medium|high|max)\b", RegexOptions.IgnoreCase);
      var context = new Regex(@"\b[0-9]+(?:\.[0-9]+)?[km]\b", RegexOptions.IgnoreCase);
      var effortOrder = new[] { "", "none", "minimal", "low", "medium", "high", "xhigh", "max" };
      var variants = new List<CursorVariant>();
      foreach (var model in catalog.Models) {
        var match = suffix.Match(model.Id);
        var baseId = match.Success ? match.Groups["base"].Value + match.Groups["variant"].Value : model.Id;
        var noThinking = Regex.IsMatch(model.Name, @"\bno[\s_-]+thinking\b", RegexOptions.IgnoreCase);
        variants.Add(new CursorVariant {
          Model = model,
          BaseId = Regex.Replace(baseId, @"(?:^|-)(?:no-)?thinking(?=-|$)", "", RegexOptions.IgnoreCase).Trim('-'),
          Name = CliModel.CleanDisplayName(match.Success ? labelEffort.Replace(model.Name, "") : model.Name),
          Effort = match.Success ? match.Groups["effort"].Value.ToLowerInvariant().Replace("extra-high", "xhigh") : "",
          Context = context.Match(model.Name).Value.ToLowerInvariant(), NoThinking = noThinking,
          Thinking = !noThinking && (Regex.IsMatch(model.Id, @"(?:^|-)thinking(?:-|$)", RegexOptions.IgnoreCase) || Regex.IsMatch(model.Name, @"\bthinking\b", RegexOptions.IgnoreCase))
        });
        model.Name = CliModel.CleanDisplayName(model.Name);
      }
      foreach (var family in variants.GroupBy(v => v.BaseId)) {
        // Context stays explicit. Thinking is a hidden alias choice rather than
        // a duplicate model row. No unknown numeric effort is inferred.
        var groups = family.GroupBy(v => v.Context).ToList();
        foreach (var group in groups) {
          if (group.Count() == 1 && group.All(v => v.Effort.Length == 0)) continue;
          var name = (group.FirstOrDefault(v => v.Effort.Length == 0 && !v.Thinking) ?? group.First()).Name;
          var efforts = new List<CliReasoningEffort>();
          foreach (var level in group.GroupBy(v => v.Effort)) {
            // Prefer the reasoning alias for a level and the native alias for
            // unknown/default effort. ModelIds preserves saved aliases exactly.
            var aliases = level.OrderByDescending(v => v.Model.IsDefault).ThenByDescending(v => level.Key.Length == 0 ? !v.Thinking : v.Thinking).ToList();
            efforts.Add(new CliReasoningEffort {
              Id = level.Key, Description = level.Key.Length == 0 ? "CLI default" : null,
              ModelId = aliases[0].Model.Id, ModelIds = aliases.Select(v => v.Model.Id).ToList()
            });
          }
          {
            // Absence of a Thinking label does not establish effort=none.
            // Only an explicit No Thinking or a real -none alias permits it.
            var aliases = group.Where(v => v.NoThinking || v.Effort == "none").OrderByDescending(v => v.Model.IsDefault).ThenByDescending(v => v.Effort.Length == 0).ToList();
            if (aliases.Count > 0) {
              var none = efforts.FirstOrDefault(e => e.Id == "none");
              if (none == null) { none = new CliReasoningEffort { Id = "none", ModelId = aliases[0].Model.Id }; efforts.Add(none); }
              none.ModelIds = none.ModelIds.Concat(aliases.Select(v => v.Model.Id)).Distinct(StringComparer.Ordinal).ToList();
            }
          }
          efforts = efforts.OrderBy(e => Array.IndexOf(effortOrder, e.Id)).ToList();
          foreach (var variant in group) {
            var model = variant.Model;
            model.BaseModelId = family.Key + (groups.Count > 1 ? "[context=" + (group.Key.Length == 0 ? "default" : group.Key) + "]" : "");
            model.BaseModelName = name;
            model.DefaultReasoningEffort = variant.NoThinking && variant.Effort.Length == 0 ? "none" : variant.Effort;
            model.ReasoningEfforts = efforts;
          }
        }
      }
    }

    private static CliModel AddModel(CliModelCatalog catalog, Dictionary<string, CliModel> models,
      string id, string name, bool isDefault)
    {
      if (string.IsNullOrWhiteSpace(id)) return null;
      CliModel existing;
      if (models.TryGetValue(id, out existing)) existing.IsDefault |= isDefault;
      else {
        existing = new CliModel { Id = id, Name = string.IsNullOrWhiteSpace(name) ? id : name, IsDefault = isDefault };
        models.Add(id, existing); catalog.Models.Add(existing);
      }
      if (isDefault && catalog.DefaultModelId == null) catalog.DefaultModelId = id;
      return existing;
    }

    internal static void ReadCodexReasoning(CliModel model, JObject entry)
    {
      model.DefaultReasoningEffort = ReadString(entry["defaultReasoningEffort"]);
      model.ReasoningEfforts.Clear();
      var efforts = entry["supportedReasoningEfforts"] as JArray;
      if (efforts == null) return;
      var seen = new HashSet<string>(StringComparer.Ordinal);
      foreach (var effort in efforts.OfType<JObject>()) {
        var id = ReadString(effort["reasoningEffort"]);
        if (id != null && Regex.IsMatch(id, @"^[a-z][a-z0-9_-]*$") && seen.Add(id))
          model.ReasoningEfforts.Add(new CliReasoningEffort { Id = id, Description = ReadString(effort["description"]) });
      }
    }

    private static async Task<CliModelCatalog> LoadCodexAsync(string executable, CancellationToken cancellation)
    {
      cancellation.ThrowIfCancellationRequested();
      using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
      using (var job = new ProcessJob()) {
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        CliProcess process = null;
        Task<string> stderr = null;
        var stage = "startup";
        try {
          cancellation.ThrowIfCancellationRequested();
          process = CliProcess.Start(CliCommand.StartInfo(executable, "app-server", Path.GetTempPath(), "codex"), job);
          stderr = DrainErrorAsync(process.StandardError);
          using (timeout.Token.Register(() => StopOwnedProcess(process, job))) {
            var reader = new RpcLineReader(process.StandardOutput);
            stage = "initialize";
            await SendAsync(process, new JObject {
              ["method"] = "initialize", ["id"] = 1,
              ["params"] = new JObject { ["clientInfo"] = new JObject {
                ["name"] = "markdown_ru", ["title"] = "Markdown RU", ["version"] = "0.1.12.5"
              } }
            }, timeout.Token).ConfigureAwait(false);
            await ReadResponseAsync(reader, 1, timeout.Token).ConfigureAwait(false);
            await SendAsync(process, new JObject { ["method"] = "initialized", ["params"] = new JObject() }, timeout.Token).ConfigureAwait(false);

            var catalog = new CliModelCatalog();
            var models = new Dictionary<string, CliModel>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string cursor = null;
            var requestId = 2;
            for (var page = 0; ; page++) {
              stage = "model/list";
              if (page >= 100) throw new InvalidOperationException("Codex model pagination exceeded 100 pages.");
              var parameters = new JObject { ["limit"] = 100, ["includeHidden"] = false };
              if (cursor != null) parameters["cursor"] = cursor;
              await SendAsync(process, new JObject { ["method"] = "model/list", ["id"] = requestId, ["params"] = parameters }, timeout.Token).ConfigureAwait(false);
              var response = await ReadResponseAsync(reader, requestId++, timeout.Token).ConfigureAwait(false);
              var entries = response["data"] as JArray;
              if (entries == null) throw new InvalidOperationException("Codex model/list returned no model array.");
              foreach (var entry in entries) {
                timeout.Token.ThrowIfCancellationRequested();
                var model = entry as JObject;
                if (model == null || ReadBoolean(model["hidden"])) continue;
                var id = ReadString(model["model"]) ?? ReadString(model["id"]);
                var discovered = AddModel(catalog, models, id, ReadString(model["displayName"]), ReadBoolean(model["isDefault"]));
                if (discovered != null) ReadCodexReasoning(discovered, model);
              }
              cursor = ReadString(response["nextCursor"]);
              if (cursor == null) break;
              if (!cursors.Add(cursor)) throw new InvalidOperationException("Codex model/list repeated a pagination cursor.");
            }

            catalog.Note = catalog.Models.Count == 0 ? "Codex не вернул доступных моделей." : "Моделей получено от Codex: " + catalog.Models.Count;
            // Retain only model, effort and MCP names; never the complete configuration.
            using (var optional = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token)) {
              stage = "config/read";
              optional.CancelAfter(TimeSpan.FromSeconds(2));
              try {
                await SendAsync(process, new JObject { ["method"] = "config/read", ["id"] = requestId,
                  ["params"] = new JObject { ["includeLayers"] = false } }, optional.Token).ConfigureAwait(false);
                var response = await ReadResponseAsync(reader, requestId, optional.Token).ConfigureAwait(false);
                var config = response["config"] as JObject;
                if (config != null) {
                  catalog.McpConfigurationRead = true;
                  catalog.ConfiguredReasoningEffort = ReadString(config["model_reasoning_effort"]);
                  var servers = config["mcp_servers"] as JObject;
                  if (servers != null) foreach (var property in servers.Properties()) catalog.McpServerNames.Add(property.Name);
                  var configuredModel = ReadString(config["model"]);
                  if (configuredModel != null) {
                    catalog.DefaultModelId = configuredModel;
                    foreach (var model in catalog.Models) model.IsDefault = string.Equals(model.Id, configuredModel, StringComparison.Ordinal);
                    if (!models.ContainsKey(configuredModel)) catalog.Note += " Настроенная в CLI модель отсутствует в этом списке.";
                  }
                }
              }
              catch (OperationCanceledException) when (!timeout.IsCancellationRequested) {
                catalog.Note += " Не удалось вовремя прочитать настроенную модель и имена MCP-серверов.";
              }
              catch (InvalidOperationException) {
                catalog.Note += " CLI не предоставил настроенную модель и имена MCP-серверов.";
              }
              catch (IOException) when (!timeout.IsCancellationRequested) {
                catalog.Note += " CLI завершился до чтения настроенной модели и имён MCP-серверов.";
              }
            }
            timeout.Token.ThrowIfCancellationRequested();
            return catalog;
          }
        }
        catch (Exception) when (timeout.IsCancellationRequested) {
          cancellation.ThrowIfCancellationRequested();
          StopOwnedProcess(process, job);
          var detail = stderr != null && stderr.IsCompleted && !stderr.IsFaulted ? stderr.Result : "";
          if (detail.Length > 1000) detail = detail.Substring(detail.Length - 1000);
          throw new TimeoutException("Codex не ответил на " + stage + " за 25 секунд." + (detail.Length == 0 ? "" : " " + detail));
        }
        finally {
          if (process != null) StopOwnedProcess(process, job);
          process?.Dispose();
          if (stderr != null) Observe(stderr);
        }
      }
    }

    private static string ReadString(JToken value)
    {
      if (value == null || value.Type != JTokenType.String) return null;
      var text = (string)value;
      return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool ReadBoolean(JToken value)
    {
      return value != null && value.Type == JTokenType.Boolean && (bool)value;
    }

    private static async Task<JObject> ReadResponseAsync(RpcLineReader reader, int requestId, CancellationToken cancellation)
    {
      var expectedId = requestId.ToString(CultureInfo.InvariantCulture);
      while (true) {
        var line = await reader.ReadLineAsync(cancellation).ConfigureAwait(false);
        if (line == null) throw new InvalidOperationException("Codex app-server ended before returning the requested response.");
        if (string.IsNullOrWhiteSpace(line)) continue;
        JObject message;
        try { message = JObject.Parse(line); }
        catch (JsonException error) { throw new InvalidOperationException("Codex app-server returned invalid JSON.", error); }
        var id = message["id"];
        if (id == null || (id.Type != JTokenType.Integer && id.Type != JTokenType.String) || id.ToString() != expectedId) continue;
        var errorResponse = message["error"];
        if (errorResponse != null && errorResponse.Type != JTokenType.Null) {
          var errorObject = errorResponse as JObject;
          var detail = errorObject == null ? null : ReadString(errorObject["message"]);
          if (detail != null && detail.Length > 500) detail = detail.Substring(0, 500);
          throw new InvalidOperationException("Codex app-server rejected the request." + (detail == null ? "" : " " + detail));
        }
        var result = message["result"] as JObject;
        if (result == null) throw new InvalidOperationException("Codex app-server returned an invalid result object.");
        return result;
      }
    }

    private static async Task SendAsync(CliProcess process, JObject message, CancellationToken cancellation)
    {
      var bytes = Utf8.GetBytes(message.ToString(Formatting.None) + "\n");
      await WithCancellationAsync(process.StandardInput.BaseStream.WriteAsync(bytes, 0, bytes.Length), cancellation).ConfigureAwait(false);
      await WithCancellationAsync(process.StandardInput.BaseStream.FlushAsync(), cancellation).ConfigureAwait(false);
    }

    private static async Task<string> DrainErrorAsync(StreamReader reader)
    {
      var retained = new StringBuilder();
      var buffer = new char[4096];
      int count;
      while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
        if (retained.Length < 4096) retained.Append(buffer, 0, Math.Min(count, 4096 - retained.Length));
      return retained.ToString();
    }

    private static void StopOwnedProcess(CliProcess process, ProcessJob job)
    {
      job.Dispose();
      if (process == null) return;
      try { if (!process.HasExited) process.Kill(); }
      catch (InvalidOperationException) { }
      catch (Win32Exception) { }
    }

    private static async Task WithCancellationAsync(Task task, CancellationToken cancellation)
    {
      Observe(task);
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

    private static void Observe(Task task)
    {
      task.ContinueWith(faulted => { var ignored = faulted.Exception; }, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static string CleanText(string text)
    {
      var clean = Ansi.Replace(text ?? "", "");
      var result = new StringBuilder(clean.Length);
      foreach (var character in clean)
        if (char.GetUnicodeCategory(character) != UnicodeCategory.Format) result.Append(character);
      return result.ToString();
    }

    private sealed class RpcLineReader
    {
      private readonly StreamReader reader;
      private readonly char[] buffer = new char[4096];
      private int offset, count, total;

      public RpcLineReader(StreamReader reader) { this.reader = reader; }

      public async Task<string> ReadLineAsync(CancellationToken cancellation)
      {
        var line = new StringBuilder();
        while (true) {
          cancellation.ThrowIfCancellationRequested();
          if (offset == count) {
            count = await WithCancellationAsync(reader.ReadAsync(buffer, 0, buffer.Length), cancellation).ConfigureAwait(false);
            offset = 0;
            if (count == 0) return line.Length == 0 ? null : line.ToString().TrimEnd('\r');
            total += count;
            if (total > MaximumOutputCharacters) throw new InvalidOperationException("Codex app-server output exceeded 8 million characters.");
          }
          var character = buffer[offset++];
          if (character == '\n') return line.ToString().TrimEnd('\r');
          line.Append(character);
          if (line.Length > MaximumLineCharacters) throw new InvalidOperationException("Codex app-server response line exceeded 2 million characters.");
        }
      }
    }
  }
}
