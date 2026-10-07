using System;
using System.IO;

namespace AnotherMarkdown.Translation
{
  public sealed class TranslationOptions
  {
    public const string DefaultArguments = "exec --ignore-user-config --skip-git-repo-check --ephemeral --sandbox read-only --model {model} --disable shell_tool --disable apps --disable plugins --disable multi_agent --disable memories --disable browser_use --disable computer_use -c project_doc_max_bytes=0 -c web_search=\"disabled\" -c model_reasoning_effort=\"low\" --output-last-message {output} -";

    public string Executable { get; set; } = FindCodex();
    public string ProviderId { get; set; } = "codex";
    public string Model { get; set; } = "gpt-6-astra";
    public bool UseDefaultModel { get; set; }
    public bool UseCustomArguments { get; set; }
    public string OutputFormat { get; set; } = "text";
    public string Arguments { get; set; } = DefaultArguments;
    public int TimeoutSeconds { get; set; } = 300;
    public bool ShowButtons { get; set; } = true;

    public TranslationOptions Copy() => (TranslationOptions)MemberwiseClone();

    public void Validate()
    {
      if (string.IsNullOrWhiteSpace(Executable)) throw new ArgumentException("Укажите путь к CLI или его имя в PATH.");
      if (!UseDefaultModel && string.IsNullOrWhiteSpace(Model)) throw new ArgumentException("Выберите модель перевода или модель по умолчанию в CLI.");
      if (!CliProfiles.Get(ProviderId).SupportsModelOverride && !UseDefaultModel && !UseCustomArguments)
        throw new ArgumentException("Этот CLI использует модель из своего профиля. Выберите модель по умолчанию в CLI.");
      if (CliProfiles.Get(ProviderId).RequiresModel && string.IsNullOrWhiteSpace(Model)) throw new ArgumentException("В выбранном CLI нет модели по умолчанию. Выберите установленную модель.");
      if (Executable.IndexOfAny(new[] { '\r', '\n', '"' }) >= 0)
        throw new ArgumentException("Путь к CLI нужно указать без кавычек и аргументов.");
      if (Path.HasExtension(Executable) && !CliProfiles.IsExePath(Executable))
        throw new ArgumentException("Можно выбрать только CLI с расширением .exe. Скриптовые файлы не поддерживаются.");
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
