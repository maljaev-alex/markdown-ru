using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace AnotherMarkdown.Translation
{
  public sealed class TranslationOptions
  {
    public const string DefaultArguments = "exec --ignore-user-config --skip-git-repo-check --ephemeral --sandbox read-only --model {model} --disable shell_tool --disable apps --disable plugins --disable multi_agent --disable memories --disable browser_use --disable computer_use -c project_doc_max_bytes=0 -c web_search=\"disabled\" -c model_reasoning_effort=\"low\" --output-last-message {output} -";

    public string Executable { get; set; } = FindCodex();
    public string ProviderId { get; set; } = "codex";
    public string Model { get; set; } = "gpt-6-astra";
    public string ReasoningEffort { get; set; } = "";
    public bool UseDefaultModel { get; set; }
    public bool UseCustomArguments { get; set; }
    public string OutputFormat { get; set; } = "text";
    public string Arguments { get; set; } = DefaultArguments;
    public int TimeoutSeconds { get; set; } = 300;
    public bool ShowButtons { get; set; } = true;
    public string ConnectionMode { get; set; } = "cli";
    public List<ApiConnection> ApiConnections { get; set; } = new List<ApiConnection>();
    public string SelectedApiConnectionId { get; set; } = "";
    public string ApiConfigurationError { get; set; }
    public bool UseApi => ConnectionMode == "api";
    public ApiConnection ActiveApiConnection => ApiConnections?.FirstOrDefault(c => c.Id == SelectedApiConnectionId) ?? ApiConnections?.FirstOrDefault();

    public TranslationOptions Copy()
    {
      var copy = (TranslationOptions)MemberwiseClone();
      copy.ApiConnections = ApiConnections?.Select(c => c.Copy()).ToList() ?? new List<ApiConnection>();
      return copy;
    }

    public void Validate()
    {
      if (ConnectionMode != "cli" && ConnectionMode != "api") throw new ArgumentException("Выберите способ подключения: CLI или API.");
      if (TimeoutSeconds < 10 || TimeoutSeconds > 3600) throw new ArgumentException("Тайм-аут должен быть от 10 до 3600 секунд.");
      if (UseApi) {
        if (ActiveApiConnection == null) throw new ArgumentException("Добавьте подключение API.");
        ApiTranslator.Validate(ActiveApiConnection);
        if (string.IsNullOrWhiteSpace(ActiveApiConnection.Model)) throw new ArgumentException("Выберите или укажите модель API.");
        return;
      }
      if (string.IsNullOrWhiteSpace(Executable)) throw new ArgumentException("Укажите путь к CLI или его имя в PATH.");
      if (!UseDefaultModel && string.IsNullOrWhiteSpace(Model)) throw new ArgumentException("Выберите модель перевода или модель по умолчанию в CLI.");
      if (!CliProfiles.Get(ProviderId).SupportsModelOverride && !UseDefaultModel && !UseCustomArguments)
        throw new ArgumentException("Этот CLI использует модель из своего профиля. Выберите модель по умолчанию в CLI.");
      if (CliProfiles.Get(ProviderId).RequiresModel && string.IsNullOrWhiteSpace(Model)) throw new ArgumentException("В выбранном CLI нет модели по умолчанию. Выберите установленную модель.");
      if (Executable.IndexOfAny(new[] { '\r', '\n', '"' }) >= 0)
        throw new ArgumentException("Путь к CLI нужно указать без кавычек и аргументов.");
      if (Path.HasExtension(Executable) && !CliProfiles.IsLauncherPath(Executable))
        throw new ArgumentException("Можно выбрать CLI с расширением .exe, .cmd или .bat. Другие скриптовые файлы не поддерживаются.");
      if (TimeoutSeconds < 10 || TimeoutSeconds > 3600)
        throw new ArgumentException("Тайм-аут должен быть от 10 до 3600 секунд.");
      foreach (var placeholder in new[] { "model", "output", "config", "policy", "prompt", "agent" })
        if ((Arguments ?? "").Contains("\"{" + placeholder + "}\""))
          throw new ArgumentException("Не заключайте {" + placeholder + "} в кавычки: плагин добавит их сам.");
    }

    public static string FindCodex()
    {
      var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "OpenAI", "Codex", "bin", "codex.exe");
      return File.Exists(path) ? path : "codex";
    }
  }
}
