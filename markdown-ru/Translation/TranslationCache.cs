using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.IO;

namespace MarkdownRu.Translation
{
  public sealed class TranslationCache
  {
    private const int MaxEntries = 8;
    private const int MaxCharacters = 4000000;
    private readonly Dictionary<string, string> values = new Dictionary<string, string>();
    private readonly LinkedList<string> order = new LinkedList<string>();
    private int characters;

    public static string Key(string source, TranslationOptions options)
    {
      var api = options.ActiveApiConnection;
      var fields = options.UseApi
        ? new[] { CliTranslator.PromptVersion, "api", api?.Endpoint, api?.Protocol, api?.Model, api?.ReasoningEffort, api?.ReasoningEffortModel, api?.ReasoningEffortCatalogKey, api?.MaxOutputTokens.ToString(CultureInfo.InvariantCulture), api?.TokenLimitParameter, api?.Temperature?.ToString(CultureInfo.InvariantCulture), api?.AdditionalParametersJson, api?.AdditionalHeadersJson, api?.AuthHeader, api?.AuthPrefix, api?.ApiKey,
          options.ParallelRequests.ToString(CultureInfo.InvariantCulture), options.MinimumChunkCharacters.ToString(CultureInfo.InvariantCulture), api?.ProxyMode, api?.ProxyAddress, api?.ProxyUseDefaultCredentials.ToString(),
          HashFields(new[] { api?.ProxyUsername, api?.ProxyPassword,
            api?.PreserveEncryptedProxyUsername == true ? api.EncryptedProxyUsername : "",
            api?.PreserveEncryptedProxyPassword == true ? api.EncryptedProxyPassword : "" }), source }
        : new[] { CliTranslator.PromptVersion, "cli", options.ParallelRequests.ToString(CultureInfo.InvariantCulture), options.MinimumChunkCharacters.ToString(CultureInfo.InvariantCulture), options.Executable, CliProfiles.ArgumentsFor(options), options.ProviderId, options.Model, options.UseDefaultModel.ToString(), options.OutputFormat, source };
      return HashFields(fields);
    }

    private static string HashFields(string[] fields)
    {
      using (var sha = SHA256.Create()) {
        using (var stream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) {
          foreach (var field in fields) {
            writer.Write((field?.Length ?? 0).ToString(CultureInfo.InvariantCulture));
            writer.Write(':');
            writer.Write(field);
          }
        }
        return System.Convert.ToBase64String(sha.Hash);
      }
    }

    public bool TryGet(string key, out string value) => values.TryGetValue(key, out value);

    public void Add(string key, string value)
    {
      if (key == null) throw new System.ArgumentNullException(nameof(key));
      if (value == null) throw new System.ArgumentNullException(nameof(value));
      var previous = order.Find(key);
      if (previous != null) Remove(previous);
      if (value.Length > MaxCharacters) return;
      while (values.Count >= MaxEntries || characters + value.Length > MaxCharacters) Remove(order.First);
      values.Add(key, value);
      order.AddLast(key);
      characters += value.Length;
    }

    private void Remove(LinkedListNode<string> entry)
    {
      characters -= values[entry.Value].Length;
      values.Remove(entry.Value);
      order.Remove(entry);
    }
  }
}
