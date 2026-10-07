using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AnotherMarkdown.Translation;

internal static class ApiLiveTests
{
  private static int Main(string[] args)
  {
    try { return Run(args).GetAwaiter().GetResult(); }
    catch (Exception error) {
      Console.Error.WriteLine("Live API check failed; credentials and server details were suppressed.");
      if (error is InvalidOperationException || error is TimeoutException) Console.Error.WriteLine(error.Message);
      return 1;
    }
  }

  private static async Task<int> Run(string[] args)
  {
    if (args.Length != 1) throw new ArgumentException("Pass the authorized environment file path.");
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var raw in File.ReadAllLines(args[0], Encoding.UTF8)) {
      var match = Regex.Match(raw.TrimStart('\uFEFF'), @"^\s*(?:export\s+)?(EMBEDDING_API_BASE|EMBEDDING_API_KEY|CHAT_MODEL)\s*=\s*(.*)$");
      if (!match.Success) continue;
      var value = match.Groups[2].Value.Trim();
      if (value.Length > 1 && (value[0] == '\'' || value[0] == '"')) {
        var end = value.IndexOf(value[0], 1); if (end < 0) throw new InvalidDataException(); value = value.Substring(1, end - 1);
      }
      else { var comment = value.IndexOf(" #", StringComparison.Ordinal); if (comment >= 0) value = value.Substring(0, comment).TrimEnd(); }
      values[match.Groups[1].Value] = value;
    }
    foreach (var required in new[] { "EMBEDDING_API_BASE", "EMBEDDING_API_KEY", "CHAT_MODEL" })
      if (!values.ContainsKey(required) || string.IsNullOrWhiteSpace(values[required])) throw new InvalidDataException();
    var connection = new ApiConnection { Endpoint = values["EMBEDDING_API_BASE"], ApiKey = values["EMBEDDING_API_KEY"], Model = values["CHAT_MODEL"], MaxOutputTokens = 1024 };
    var options = new TranslationOptions { ConnectionMode = "api", ApiConnections = new List<ApiConnection> { connection }, SelectedApiConnectionId = connection.Id, TimeoutSeconds = 90 };
    options.Validate();
    var translator = new ApiTranslator();
    try {
      var models = await translator.LoadModelsAsync(connection, 30, CancellationToken.None);
      Console.WriteLine("PASS live API model list: " + models.Models.Count + " models; configured model listed: " + models.Models.Any(m => m.Id == connection.Model));
    }
    catch (InvalidOperationException error) { Console.WriteLine("Model list unavailable; testing the configured model directly. " + error.Message); }
    const string source = "---\nname: translation-test\ndescription: \"A short connection test for technical documentation.\"\nenabled: true\n---\n\n# Quick start\nOpen the settings and choose a model.\n\n`WorkPackage.allowed_to`\n";
    var translated = await new CliTranslator().TranslateAsync(source, options, CancellationToken.None);
    if (!Regex.IsMatch(translated, "[\u0400-\u04ff]") || !translated.Contains("WorkPackage.allowed_to") || !translated.Contains("name: translation-test") || !translated.Contains("enabled: true") || !Regex.IsMatch(translated, @"(?m)^description:.*[\u0400-\u04ff]"))
      throw new InvalidDataException();
    Console.WriteLine("PASS live API translation: Russian prose/description, Markdown and technical YAML preserved.");
    Console.WriteLine("No credentials were printed or saved; plugin settings were not changed.");
    return 0;
  }
}
