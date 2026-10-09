using System;
using System.Collections.Generic;

namespace AnotherMarkdown.Translation
{
  public sealed class CliConnectionSettings
  {
    public string ProviderId { get; set; } = "custom";
    public string Executable { get; set; } = "";
    public string Model { get; set; } = "";
    public string ReasoningEffort { get; set; } = "";
    public bool UseDefaultModel { get; set; } = true;
    public bool UseManualModel { get; set; }
    public bool UseCustomArguments { get; set; }
    public string Arguments { get; set; } = "";
    public string OutputFormat { get; set; } = "text";
    public int TimeoutSeconds { get; set; } = 300;
    public int ParallelRequests { get; set; } = 1;
    public int MinimumChunkCharacters { get; set; } = 2000;
    public bool ShowButtons { get; set; } = true;

    public CliConnectionSettings Copy() => (CliConnectionSettings)MemberwiseClone();
    public static CliConnectionSettings Capture(TranslationOptions options)
    {
      if (options == null) throw new ArgumentNullException(nameof(options));
      return new CliConnectionSettings {
        ProviderId = options.ProviderId, Executable = options.Executable, Model = options.Model, ReasoningEffort = options.ReasoningEffort,
        UseDefaultModel = options.UseDefaultModel, UseManualModel = options.UseManualModel, UseCustomArguments = options.UseCustomArguments,
        Arguments = options.Arguments, OutputFormat = options.OutputFormat, TimeoutSeconds = options.TimeoutSeconds,
        ParallelRequests = options.ParallelRequests, MinimumChunkCharacters = options.MinimumChunkCharacters, ShowButtons = options.ShowButtons
      };
    }
    public void ApplyTo(TranslationOptions options)
    {
      if (options == null) throw new ArgumentNullException(nameof(options));
      options.ProviderId = ProviderId; options.Executable = Executable; options.Model = Model; options.ReasoningEffort = ReasoningEffort;
      options.UseDefaultModel = UseDefaultModel; options.UseManualModel = UseManualModel; options.UseCustomArguments = UseCustomArguments;
      options.Arguments = Arguments; options.OutputFormat = OutputFormat; options.TimeoutSeconds = TimeoutSeconds;
      options.ParallelRequests = ParallelRequests; options.MinimumChunkCharacters = MinimumChunkCharacters; options.ShowButtons = ShowButtons;
    }
    public static void Upsert(IList<CliConnectionSettings> connections, CliConnectionSettings connection)
    {
      if (connections == null) throw new ArgumentNullException(nameof(connections));
      if (connection == null) throw new ArgumentNullException(nameof(connection));
      var first = -1;
      for (var i = 0; i < connections.Count; i++) {
        var current = connections[i];
        if (current == null || !SameIdentity(current.ProviderId, current.Executable, connection.ProviderId, connection.Executable)) continue;
        if (first < 0) first = i; else connections.RemoveAt(i--);
      }
      if (first < 0) connections.Add(connection.Copy()); else connections[first] = connection.Copy();
    }
    public static bool SameIdentity(string provider, string executable, string otherProvider, string otherExecutable) =>
      string.Equals((provider ?? "").Trim(), (otherProvider ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
      && string.Equals(SettingsDiscoveryCache.NormalizeExecutable(executable), SettingsDiscoveryCache.NormalizeExecutable(otherExecutable), StringComparison.Ordinal);
  }
}
