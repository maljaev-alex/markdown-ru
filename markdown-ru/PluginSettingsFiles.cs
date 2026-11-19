using System;
using System.Diagnostics;
using System.IO;
using Kbg.NppPluginNET.PluginInfrastructure;

namespace MarkdownRu
{
  internal static class PluginSettingsFiles
  {
    internal static string ResolveIniPath(string configRoot, string pluginDirectory)
    {
      if (!Path.IsPathRooted(configRoot)) throw new IOException("Notepad++ did not provide its plugin configuration directory.");
      const string fileName = PluginBranding.Name + ".ini";
      var profile = Path.Combine(configRoot, PluginBranding.Name);
      var current = Path.Combine(profile, fileName);
      if (Directory.Exists(profile)) return current;

      var legacy = Path.Combine(configRoot, PluginBranding.LegacyModuleName + ".ini");
      var staging = Path.Combine(configRoot, "." + PluginBranding.Name + "-" + Guid.NewGuid().ToString("N"));
      try {
        Directory.CreateDirectory(staging);
        foreach (var suffix in new[] { "", ".cli.json", ".api.json", ".models.json" }) {
          // Preserve encoding and DPAPI ciphertext. A profile need not have every sidecar.
          try { File.Copy(legacy + suffix, Path.Combine(staging, fileName + suffix)); }
          catch (FileNotFoundException) { }
        }
        UpdateResourcePaths(Path.Combine(staging, fileName), pluginDirectory);
        // Make the entire profile visible together. Concurrent startups can only publish once.
        Directory.Move(staging, profile);
        return current;
      }
      catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) {
        if (Directory.Exists(profile)) return current;
        Trace.TraceWarning("Could not initialize the markdown-ru profile: {0}", error.Message);
        // A locked source file must not replace the user's settings with defaults.
        return File.Exists(legacy) ? legacy : current;
      }
      finally {
        try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) {
          Trace.TraceWarning("Could not remove the temporary profile: {0}", error.Message);
        }
      }
    }

    private static void UpdateResourcePaths(string ini, string pluginDirectory)
    {
      if (!File.Exists(ini)) return;
      var installed = new DirectoryInfo(pluginDirectory);
      var legacyPrefix = Path.Combine(installed.Parent.FullName, PluginBranding.LegacyModuleName) + Path.DirectorySeparatorChar;
      foreach (var key in new[] { "AssetsPath", "CssFileName", "CssDarkModeFileName" }) {
        var stored = Win32.ReadIniValue("Options", key, ini).Replace('/', Path.DirectorySeparatorChar);
        if (!stored.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase)) continue;
        var replacement = Path.Combine(installed.FullName, stored.Substring(legacyPrefix.Length));
        if (key == "AssetsPath" ? Directory.Exists(replacement) : File.Exists(replacement))
          Win32.WriteIniValue("Options", key, replacement.Replace('\\', '/'), ini);
      }
    }
  }
}
