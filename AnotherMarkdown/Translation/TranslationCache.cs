using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace AnotherMarkdown.Translation
{
  public sealed class TranslationCache
  {
    private readonly Dictionary<string, string> values = new Dictionary<string, string>();
    private readonly Queue<string> order = new Queue<string>();
    private int characters;

    public static string Key(string source, TranslationOptions options)
    {
      var api = options.ActiveApiConnection;
      var fields = options.UseApi
        ? new[] { CliTranslator.PromptVersion, "api", api?.Endpoint, api?.Protocol, api?.Model, api?.ReasoningEffort, api?.MaxOutputTokens.ToString(CultureInfo.InvariantCulture), api?.Temperature?.ToString(CultureInfo.InvariantCulture), api?.AdditionalParametersJson, api?.AdditionalHeadersJson, api?.AuthHeader, api?.AuthPrefix, api?.ApiKey, source }
        : new[] { CliTranslator.PromptVersion, "cli", options.Executable, CliProfiles.ArgumentsFor(options), options.ProviderId, options.Model, options.UseDefaultModel.ToString(), options.OutputFormat, source };
      var input = new StringBuilder();
      foreach (var field in fields) input.Append((field ?? "").Length).Append(':').Append(field);
      using (var sha = SHA256.Create()) return System.Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(input.ToString())));
    }

    public bool TryGet(string key, out string value) => values.TryGetValue(key, out value);

    public void Add(string key, string value)
    {
      if (values.ContainsKey(key) || value.Length > 4000000) return;
      while (values.Count >= 8 || characters + value.Length > 4000000) {
        var oldest = order.Dequeue();
        characters -= values[oldest].Length;
        values.Remove(oldest);
      }
      values.Add(key, value);
      order.Enqueue(key);
      characters += value.Length;
    }
  }
}
