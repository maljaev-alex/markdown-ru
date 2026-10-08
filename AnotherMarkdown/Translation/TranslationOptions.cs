using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace AnotherMarkdown.Translation
{
  public sealed class TranslationOptions
  {
    public const int MaximumStoredArgumentCharacters = 32765;
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

    public void LoadApiConnections(string path)
    {
      try { ApiConnections = ApiConnectionStore.Load(path); ApiConfigurationError = null; }
      catch (Exception error) when (error is InvalidDataException || error is IOException || error is UnauthorizedAccessException || error is ArgumentException) {
        ApiConnections = new List<ApiConnection>();
        ApiConfigurationError = "Не удалось прочитать сохранённые подключения API. Исходный файл настроек сохранён.";
      }
    }

    public TranslationOptions Copy()
    {
      var copy = (TranslationOptions)MemberwiseClone();
      copy.ApiConnections = ApiConnections?.Select(c => c.Copy()).ToList() ?? new List<ApiConnection>();
      return copy;
    }

    public void Validate() => Validate(true);

    public void Validate(bool requireReady)
    {
      ValidateArgumentStorage();
      if (ConnectionMode != "cli" && ConnectionMode != "api") throw new ArgumentException("Выберите способ подключения: CLI или API.");
      if (TimeoutSeconds < 10 || TimeoutSeconds > 3600) throw new ArgumentException("Тайм-аут должен быть от 10 до 3600 секунд.");
      if (UseApi) {
        if (ActiveApiConnection == null) {
          if (requireReady) throw new ArgumentException("Добавьте подключение API.");
          return;
        }
        if (requireReady) ApiTranslator.Validate(ActiveApiConnection); else ApiTranslator.ValidateDraft(ActiveApiConnection);
        if (requireReady && string.IsNullOrWhiteSpace(ActiveApiConnection.Model)) throw new ArgumentException("Выберите или укажите модель API.");
        return;
      }
      if (requireReady && string.IsNullOrWhiteSpace(Executable)) throw new ArgumentException("Укажите путь к CLI или его имя в PATH.");
      if (requireReady && !UseDefaultModel && string.IsNullOrWhiteSpace(Model)) throw new ArgumentException("Выберите модель перевода или модель по умолчанию в CLI.");
      if (requireReady && !CliProfiles.Get(ProviderId).SupportsModelOverride && !UseDefaultModel && !UseCustomArguments)
        throw new ArgumentException("Этот CLI использует модель из своего профиля. Выберите модель по умолчанию в CLI.");
      if (requireReady && CliProfiles.Get(ProviderId).RequiresModel && string.IsNullOrWhiteSpace(Model)) throw new ArgumentException("В выбранном CLI нет модели по умолчанию. Выберите установленную модель.");
      if ((Executable ?? "").IndexOfAny(new[] { '\r', '\n', '"' }) >= 0)
        throw new ArgumentException("Путь к CLI нужно указать без кавычек и аргументов.");
      if (Path.HasExtension(Executable) && !CliProfiles.IsLauncherPath(Executable))
        throw new ArgumentException("Можно выбрать CLI с расширением .exe, .cmd или .bat. Другие скриптовые файлы не поддерживаются.");
      if (!UseCustomArguments && !CliProfiles.Get(ProviderId).SupportsBatchLauncher &&
          (CliProfiles.IsBatchPath(Executable) || (requireReady && CliProfiles.IsBatchPath(CliTranslator.ResolveExecutable(Executable)))))
        throw new ArgumentException("Для этого профиля CLI нужен нативный .exe: официальный режим запроса передаёт многострочный текст аргументом. Выберите .exe или настройте другой способ запуска в профиле «Другой CLI».");
      if (TimeoutSeconds < 10 || TimeoutSeconds > 3600)
        throw new ArgumentException("Тайм-аут должен быть от 10 до 3600 секунд.");
      foreach (var placeholder in new[] { "model", "output", "config", "policy", "prompt", "agent" })
        if ((Arguments ?? "").Contains("\"{" + placeholder + "}\""))
          throw new ArgumentException("Не заключайте {" + placeholder + "} в кавычки: плагин добавит их сам.");
    }

    public void ValidateArgumentStorage()
    {
      if ((Arguments ?? "").Length > MaximumStoredArgumentCharacters)
        throw new ArgumentException("Шаблон аргументов CLI слишком длинный. Максимум: " + MaximumStoredArgumentCharacters + " символов.");
    }

    public static string FindCodex()
    {
      var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "OpenAI", "Codex", "bin", "codex.exe");
      return File.Exists(path) ? path : "codex";
    }
  }
}
