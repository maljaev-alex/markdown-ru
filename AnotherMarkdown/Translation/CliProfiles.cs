using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AnotherMarkdown.Translation
{
  public sealed class CliProfile
  {
    public string Id { get; }
    public string Name { get; }
    public string OutputFormat { get; }
    public bool RequiresModel => Id == "ollama";
    public bool SupportsModelOverride => Id != "dsh";
    public CliProfile(string id, string name, string outputFormat) { Id = id; Name = name; OutputFormat = outputFormat; }
    public override string ToString() => Name;
  }

  public sealed class CliInstallation
  {
    public string ProviderId { get; set; }
    public string Executable { get; set; }
    public override string ToString() => CliProfiles.Get(ProviderId).Name + " · " + Path.GetFileName(Executable);
  }

  public static class CliProfiles
  {
    public static readonly CliProfile[] All = {
      new CliProfile("codex", "Codex CLI", "text"),
      new CliProfile("cursor", "Cursor Agent", "json-result"),
      new CliProfile("claude", "Claude Code", "text"),
      new CliProfile("gemini", "Gemini CLI", "json-response"),
      new CliProfile("opencode", "OpenCode", "opencode-json"),
      new CliProfile("ollama", "Ollama", "text"),
      new CliProfile("dsh", "DeepSeek Harness", "text"),
      new CliProfile("qwen", "Qwen Code", "json-result"),
      new CliProfile("kimi", "Kimi Code", "kimi-json"),
      new CliProfile("agy", "Antigravity CLI", "agy-json"),
      new CliProfile("copilot", "GitHub Copilot CLI", "text"),
      new CliProfile("custom", "Другой CLI", "text")
    };

    public static CliProfile Get(string id) => All.FirstOrDefault(p => p.Id == id) ?? All.Last();

    public static bool IsExePath(string path) => string.Equals(Path.GetExtension(path ?? ""), ".exe", StringComparison.OrdinalIgnoreCase);

    public static string Identify(string path)
    {
      var name = Path.GetFileNameWithoutExtension(path ?? "").ToLowerInvariant();
      if (name == "agent" || name == "cursor-agent") return "cursor";
      return All.Any(p => p.Id == name) ? name : "custom";
    }

    public static List<CliInstallation> DiscoverInstalled()
    {
      var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').ToList();
      var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
      var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
      var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
      directories.AddRange(new[] {
        Path.Combine(local, "Programs", "OpenAI", "Codex", "bin"),
        Path.Combine(local, "cursor-agent"), Path.Combine(local, "Programs", "Ollama"),
        Path.Combine(local, "agy", "bin"), Path.Combine(local, "qwen-code", "bin"),
        Path.Combine(local, "kimi", "bin"), Path.Combine(local, "Programs", "Kimi", "bin"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "antigravity-cli"),
        Path.Combine(roaming, "npm"), Path.Combine(user, ".local", "bin"), Path.Combine(user, ".cargo", "bin")
      });
      var names = All.Where(p => p.Id != "custom" && p.Id != "cursor").Select(p => p.Id).Concat(new[] { "agent", "cursor-agent" }).ToArray();
      var result = new List<CliInstallation>();
      var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var name in names) {
        foreach (var directory in directories.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase)) {
          foreach (var extension in new[] { ".exe" }) {
            string path;
            try { path = Path.GetFullPath(Path.Combine(Environment.ExpandEnvironmentVariables(directory.Trim().Trim('"')), name + extension)); }
            catch (ArgumentException) { continue; }
            catch (NotSupportedException) { continue; }
            if (!File.Exists(path)) continue;
            // Only executable CLI entries are eligible; shell scripts are excluded.
            if (found.Add(Path.Combine(Path.GetDirectoryName(path), Identify(path))))
              result.Add(new CliInstallation { ProviderId = Identify(path), Executable = path });
            break;
          }
        }
      }
      return result;
    }

    public static string ArgumentsFor(TranslationOptions options)
    {
      if (options.UseCustomArguments) return options.Arguments ?? "";
      var model = !options.UseDefaultModel || options.ProviderId == "ollama";
      var modelArgument = model && !string.IsNullOrWhiteSpace(options.Model) ? " --model {model}" : "";
      switch (options.ProviderId) {
        case "codex":
          return "exec --skip-git-repo-check --ephemeral --sandbox read-only" + modelArgument +
            " --disable shell_tool --disable apps --disable plugins --disable hooks --disable multi_agent --disable memories --disable browser_use --disable computer_use -c project_doc_max_bytes=0 -c developer_instructions=\"\" -c web_search=\"disabled\" --output-last-message {output} -";
        case "cursor": return "--print --mode ask --output-format json --trust" + modelArgument;
        case "claude": return "--print --output-format text --tools \"\" --strict-mcp-config --mcp-config {config} --no-session-persistence" + modelArgument;
        case "gemini": return "--output-format json --approval-mode plan --admin-policy {policy}" + modelArgument;
        case "opencode": return "--pure run --format json --agent plan" + modelArgument;
        case "ollama": return "run --nowordwrap {model}";
        case "dsh": return "--profile headless {prompt}";
        case "qwen": return "--safe-mode --approval-mode plan --input-format text --output-format json" + modelArgument;
        case "kimi": return "-p {prompt} --output-format stream-json --agent-file {agent}" + modelArgument;
        case "agy": return "--print {prompt} --output-format json" + modelArgument;
        case "copilot": return "--silent --no-ask-user --deny-tool \"shell,write,read,url,memory\"" + modelArgument;
        default: return options.Arguments ?? "";
      }
    }

    public static TranslationOptions Defaults(string providerId, string executable)
    {
      var options = new TranslationOptions {
        ProviderId = providerId, Executable = executable, Model = "", UseDefaultModel = true,
        UseCustomArguments = providerId == "custom", OutputFormat = Get(providerId).OutputFormat,
        TimeoutSeconds = 300, ShowButtons = true
      };
      options.Arguments = ArgumentsFor(options);
      return options;
    }
  }
}
