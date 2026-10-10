using System;
using System.Linq;

namespace AnotherMarkdown.Translation
{
  internal sealed class ApiEffortOptions
  {
    internal string[] Values { get; private set; } = new string[0];
    internal string Note { get; private set; }
    internal string WireField { get; private set; }

    internal static ApiEffortOptions For(ApiConnection connection, CliModel model = null)
    {
      var protocol = (connection?.Protocol ?? "chat-completions").ToLowerInvariant();
      var result = new ApiEffortOptions {
        WireField = protocol == "responses" ? "reasoning.effort" : protocol == "anthropic" ? "output_config.effort"
          : protocol == "gemini" ? "generationConfig.thinkingConfig.thinkingLevel" : "reasoning_effort"
      };
      var protocolValues = protocol == "anthropic" ? new[] { "low", "medium", "high", "xhigh", "max" }
        : protocol == "gemini" ? new[] { "minimal", "low", "medium", "high" }
        : new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max" };
      var advertised = model?.ReasoningEfforts?.Select(e => e?.Id?.ToLowerInvariant()).Where(e => !string.IsNullOrEmpty(e))
        .Where(e => protocolValues.Contains(e, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal)
        .OrderBy(e => Array.IndexOf(protocolValues, e)).ToArray();
      if (advertised != null && advertised.Length > 0) result.Values = advertised;
      result.Note = result.Values.Length > 0 ? "Для выбранной модели доступны: " + string.Join(", ", result.Values) + "."
        : "API не сообщил допустимые уровни для этой модели. Effort недоступен; используется настройка сервиса по умолчанию.";
      return result;
    }
  }
}
