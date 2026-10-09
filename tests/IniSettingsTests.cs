using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using AnotherMarkdown;
using AnotherMarkdown.Entities;
using AnotherMarkdown.Forms;
using AnotherMarkdown.Translation;
using Kbg.NppPluginNET.PluginInfrastructure;

internal static class IniSettingsTests
{
  private static int checks;
  private static void Check(bool condition, string label)
  {
    if (!condition) throw new Exception("FAIL: " + label);
    checks++; Console.WriteLine("PASS " + label);
  }

  private static void CliProfilePersistence(string directory)
  {
    var file = Path.Combine(directory, "cli-profiles.ini");
    var type = typeof(MarkdownPanelController);
    var controller = (MarkdownPanelController)FormatterServices.GetUninitializedObject(type);
    type.GetField("_iniFilePath", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(controller, file);
    var load = type.GetMethod("LoadSettingsFromIni", BindingFlags.NonPublic | BindingFlags.Instance);
    var save = type.GetMethod("SaveSettings", BindingFlags.NonPublic | BindingFlags.Instance);
    Func<Settings> reload = () => (Settings)load.Invoke(controller, null);
    Action<Settings> select = value => type.GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(controller, value);
    Func<bool> persist = () => (bool)save.Invoke(controller, new object[] { false });
    var active = new CliConnectionSettings {
      ProviderId = "custom", Executable = @"D:\SyntheticTools\custom.exe", Model = "manual-model-a", ReasoningEffort = "high",
      UseDefaultModel = false, UseManualModel = true, UseCustomArguments = true, Arguments = "\"first argument\" --model {model} \"last argument\"",
      OutputFormat = "json", TimeoutSeconds = 111, ParallelRequests = 4, MinimumChunkCharacters = 500, ShowButtons = false
    };
    Win32.WriteIniValue("Translation", "Executable", active.Executable, file);
    Win32.WriteIniValue("Translation", "ProviderId", "custom", file);
    Win32.WriteIniValue("Translation", "Model", "legacy-model", file);
    var legacy = reload();
    Check(legacy.Translation.CliConnections.Count == 1 && legacy.Translation.CliConnections[0].Model == "legacy-model"
      && !legacy.Translation.UseManualModel, "legacy INI selection migrates into one CLI profile with a backward-compatible manual-model default");
    Check(!File.Exists(file + ".cli.json"), "opening legacy settings does not save migrated drafts before the user saves");
    var other = active.Copy(); other.ProviderId = "cursor"; other.Model = "model-b"; other.TimeoutSeconds = 333; other.ParallelRequests = 7; other.MinimumChunkCharacters = 0;
    var otherPath = active.Copy(); otherPath.Executable = @"D:\SyntheticTools\other\custom.exe"; otherPath.Model = "model-c";
    var settings = new Settings { EnabledMarkdownPlugins = new string[0], ZoomLevel = 160 };
    active.ApplyTo(settings.Translation);
    settings.Translation.CliConnections.Add(other); settings.Translation.CliConnections.Add(otherPath);
    select(settings); Check(persist(), "controller saves the active CLI selection and all inactive provider/path drafts");
    var restored = reload(); var profiles = restored.Translation.CliConnections;
    Check(profiles.Count == 3 && profiles.Any(c => c.ProviderId == "cursor" && c.Model == "model-b" && c.TimeoutSeconds == 333 && c.ParallelRequests == 7 && c.MinimumChunkCharacters == 0)
      && profiles.Any(c => c.Executable == otherPath.Executable && c.Model == "model-c"), "controller restart retains independent providers and executable paths");
    var actual = CliConnectionSettings.Capture(restored.Translation);
    Check(actual.ProviderId == active.ProviderId && actual.Executable == active.Executable && actual.Model == active.Model && actual.ReasoningEffort == active.ReasoningEffort
      && actual.UseDefaultModel == active.UseDefaultModel && actual.UseManualModel == active.UseManualModel && actual.UseCustomArguments == active.UseCustomArguments
      && actual.Arguments == active.Arguments && actual.OutputFormat == active.OutputFormat && actual.TimeoutSeconds == active.TimeoutSeconds
      && actual.ParallelRequests == active.ParallelRequests && actual.MinimumChunkCharacters == active.MinimumChunkCharacters && actual.ShowButtons == active.ShowButtons,
      "controller round-trips every active CLI field including explicit manual model and boundary quotes");
    var stale = profiles.Single(c => CliConnectionSettings.SameIdentity(c.ProviderId, c.Executable, active.ProviderId, active.Executable));
    stale.Model = "stale-sidecar-model"; stale.TimeoutSeconds = 222;
    CliConnectionStore.Save(file + ".cli.json", profiles);
    restored = reload();
    var restoredActive = restored.Translation.CliConnections.Single(c => CliConnectionSettings.SameIdentity(c.ProviderId, c.Executable, active.ProviderId, active.Executable));
    Check(restoredActive.Model == active.Model && restoredActive.TimeoutSeconds == active.TimeoutSeconds, "active CLI-mode INI remains authoritative over a stale matching sidecar draft");
    restored.Translation.ConnectionMode = "api"; restored.Translation.TimeoutSeconds = 777; restored.Translation.ParallelRequests = 8;
    restored.Translation.MinimumChunkCharacters = 10000; restored.Translation.ShowButtons = true;
    var api = new ApiConnection { Name = "Synthetic independent API", Model = "api-model", ApiKey = "synthetic-controller-only-key" };
    restored.Translation.ApiConnections.Add(api); restored.Translation.SelectedApiConnectionId = api.Id;
    select(restored); Check(persist(), "controller saves API selection alongside unchanged CLI drafts");
    var apiRestored = reload(); var inactive = apiRestored.Translation.CliConnections.Single(c => CliConnectionSettings.SameIdentity(c.ProviderId, c.Executable, active.ProviderId, active.Executable));
    Check(apiRestored.Translation.UseApi && apiRestored.Translation.TimeoutSeconds == 777 && apiRestored.Translation.ParallelRequests == 8 && apiRestored.Translation.MinimumChunkCharacters == 10000,
      "API-mode shared controls remain the active INI values across restart");
    Check(inactive.TimeoutSeconds == 111 && inactive.ParallelRequests == 4 && inactive.MinimumChunkCharacters == 500 && !inactive.ShowButtons,
      "active API timing, concurrency, chunk size and buttons do not overwrite saved inactive CLI values");
    Check(apiRestored.Translation.ActiveApiConnection.Id == api.Id && apiRestored.Translation.ActiveApiConnection.ApiKey == "synthetic-controller-only-key",
      "adding CLI profile storage preserves existing independent API profiles and credentials");
    var oldMode = Win32.ReadIniValue("Translation", "ConnectionMode", file); var oldModel = Win32.ReadIniValue("Translation", "Model", file);
    var oldExecutable = Win32.ReadIniValue("Translation", "Executable", file); var oldTimeout = Win32.ReadIniValue("Translation", "TimeoutSeconds", file);
    File.WriteAllText(file + ".cli.json", "{ synthetic-malformed-CLI-store", new UTF8Encoding(false));
    var corrupt = File.ReadAllBytes(file + ".cli.json"); var damaged = reload();
    Check(damaged.Translation.CliConfigurationError != null && damaged.Translation.Executable == oldExecutable,
      "a damaged CLI sidecar keeps the prior active INI selection and exposes a separate load error");
    damaged.ZoomLevel = 195; damaged.Translation.ConnectionMode = "cli"; damaged.Translation.Executable = @"D:\Unsaved\new.exe";
    damaged.Translation.Model = "unsaved-model"; damaged.Translation.TimeoutSeconds = 123;
    select(damaged); Check(!persist(), "controller reports the corrupt CLI persistence error without an unhandled exception");
    Check(File.ReadAllBytes(file + ".cli.json").SequenceEqual(corrupt), "a normal save never replaces a damaged CLI catalog");
    Check(Win32.ReadIniValue("Translation", "ConnectionMode", file) == oldMode && Win32.ReadIniValue("Translation", "Model", file) == oldModel
      && Win32.ReadIniValue("Translation", "Executable", file) == oldExecutable && Win32.ReadIniValue("Translation", "TimeoutSeconds", file) == oldTimeout,
      "failed CLI persistence preserves the previous persisted active selection and CLI controls");
    Check(Win32.GetPrivateProfileInt("Options", "ZoomLevel", 0, file) == 195, "CLI persistence failure still saves unrelated preview settings");
    File.WriteAllText(file + ".cli.json", "[]"); damaged.Translation.LoadCliConnections(file + ".cli.json");
    Check(damaged.Translation.CliConfigurationError == null && persist(), "repair plus explicit reload allows CLI settings to save again");
    var beforeLocked = File.ReadAllBytes(file + ".cli.json"); var lockedModel = Win32.ReadIniValue("Translation", "Model", file);
    damaged.Translation.Model = "locked-unsaved-model"; damaged.ZoomLevel = 196;
    using (var locked = new FileStream(file + ".cli.json", FileMode.Open, FileAccess.Read, FileShare.None))
      Check(!persist(), "a locked CLI catalog reports a save failure");
    Check(File.ReadAllBytes(file + ".cli.json").SequenceEqual(beforeLocked) && Win32.ReadIniValue("Translation", "Model", file) == lockedModel
      && Win32.GetPrivateProfileInt("Options", "ZoomLevel", 0, file) == 196, "locked CLI storage preserves active selection while still saving preview changes");
    var incompleteFile = Path.Combine(directory, "incomplete-cli.ini");
    type.GetField("_iniFilePath", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(controller, incompleteFile);
    var incomplete = new Settings { EnabledMarkdownPlugins = new string[0], ZoomLevel = 197 };
    incomplete.Translation.Executable = ""; incomplete.Translation.ProviderId = "custom"; incomplete.Translation.ShowButtons = false;
    select(incomplete);
    Check(persist() && reload().Translation.Executable == "" && !reload().Translation.ShowButtons,
      "an incomplete empty-path CLI draft can save and reopen unrelated preview options");
  }

  [STAThread]
  private static int Main()
  {
    var root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    var directory = Path.Combine(root, "ini-settings-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var file = Path.Combine(directory, "settings.ini");
    try {
      File.WriteAllText(file, "[Translation]\r\nModel=before\r\n");
      Win32.WriteIniValue("Translation", "Model", "saved", file);
      Check(Win32.ReadIniValue("Translation", "Model", file, "") == "saved", "successful native INI write can be read back");
      var previous = File.ReadAllText(file);
      File.SetAttributes(file, FileAttributes.ReadOnly);
      var failed = false;
      try { Win32.WriteIniValue("Translation", "Model", "lost", file); }
      catch (IOException error) {
        failed = true;
        Check(error.Message.Contains(file), "read-only error identifies the settings file");
        Check((error.InnerException as Win32Exception)?.NativeErrorCode == 5, "read-only error retains native access-denied code");
      }
      Check(failed, "a read-only INI reports failure instead of claiming settings were saved");
      Check(File.ReadAllText(file) == previous, "failed INI write leaves previous bytes intact");
      failed = false;
      try { Win32.WriteIniValue("Translation", "Model", "lost", Path.Combine(directory, "missing", "settings.ini")); }
      catch (IOException error) {
        failed = true;
        Check((error.InnerException as Win32Exception)?.NativeErrorCode == 3, "missing-directory error retains native path-not-found code");
      }
      Check(failed, "missing configuration directory reports a native write failure");
      File.SetAttributes(file, FileAttributes.Normal);
      var longValue = new string('a', 32767);
      File.WriteAllText(file, "[Translation]\r\nArguments=" + longValue + "\r\n");
      Check(Win32.ReadIniValue("Translation", "Arguments", file, "") == longValue, "a maximum-length textbox value reloads without a startup exception or truncation");
      var unicodeFile = Path.Combine(directory, "\u6d4b\u8bd5.ini");
      File.WriteAllText(unicodeFile, "[Translation]\r\nModel=before\r\n", Encoding.Unicode);
      Win32.WriteIniValue("Translation", "Model", "\u041c\u043e\u0434\u0435\u043b\u044c \u6d4b\u8bd5", unicodeFile);
      Check(Win32.ReadIniValue("Translation", "Model", unicodeFile, "") == "\u041c\u043e\u0434\u0435\u043b\u044c \u6d4b\u8bd5", "Unicode profile paths and values round-trip independently of the ANSI code page");
      Win32.WriteIniValue("Translation", "TimeoutSeconds", "45", unicodeFile);
      Check(Win32.GetPrivateProfileInt("Translation", "TimeoutSeconds", 0, unicodeFile) == 45, "numeric INI settings use the same Unicode path");
      var longUnicodeValue = new string('\u6d4b', 32767);
      Win32.WriteIniValue("Translation", "Arguments", longUnicodeValue, unicodeFile);
      Check(Win32.ReadIniValue("Translation", "Arguments", unicodeFile, "") == longUnicodeValue, "maximum-length Unicode value survives a native write/read round-trip");
      var legacyValue = Encoding.Default.GetString(Encoding.Default.GetBytes("Legacy \u041c\u043e\u0434\u0435\u043b\u044c"));
      File.WriteAllText(file, "[Translation]\r\nModel=" + legacyValue + "\r\n", Encoding.Default);
      Check(Win32.ReadIniValue("Translation", "Model", file, "") == legacyValue, "Unicode profile API still reads an existing system-code-page INI");
      Win32.WriteIniValue("Translation", "TimeoutSeconds", "60", file);
      Check(File.ReadAllText(file, Encoding.Default).Contains("Model=" + legacyValue), "updating a legacy INI preserves its existing text encoding");
      var legacyBytes = File.ReadAllBytes(file);
      failed = false;
      try { Win32.WriteIniValue("Translation", "Model", "\u6d4b\u8bd5", file); }
      catch (IOException) { failed = true; }
      Check(failed && File.ReadAllBytes(file).SequenceEqual(legacyBytes), "unsupported Unicode in an existing ANSI profile reports an error without corrupting the saved value");
      var newUnicodeFile = Path.Combine(directory, "new-unicode.ini");
      Win32.WriteIniValue("Translation", "Model", "\u6d4b\u8bd5", newUnicodeFile);
      Check(File.ReadAllBytes(newUnicodeFile).Take(2).SequenceEqual(Encoding.Unicode.GetPreamble()) && Win32.ReadIniValue("Translation", "Model", newUnicodeFile, "") == "\u6d4b\u8bd5", "new settings files use Unicode without changing the encoding of existing files");
      File.WriteAllText(file, "[Translation]\r\nConnectionMode=cli\r\nApiConnectionId=previous-id\r\n");
      File.WriteAllText(file + ".api.json", "[]");
      var settings = new Settings { ZoomLevel = 175, EnabledMarkdownPlugins = new string[0] };
      settings.Translation.ConnectionMode = "api";
      settings.Translation.SelectedApiConnectionId = "new-id";
      settings.Translation.ReasoningEffort = "high";
      settings.Translation.ParallelRequests = 4;
      settings.Translation.MinimumChunkCharacters = 500;
      // Exercise persistence without initializing or messaging a Notepad++ window.
      var controller = (MarkdownPanelController)FormatterServices.GetUninitializedObject(typeof(MarkdownPanelController));
      var controllerType = typeof(MarkdownPanelController);
      controllerType.GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(controller, settings);
      controllerType.GetField("_iniFilePath", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(controller, file);
      var loadSettings = controllerType.GetMethod("LoadSettingsFromIni", BindingFlags.NonPublic | BindingFlags.Instance);
      Check(((Settings)loadSettings.Invoke(controller, null)).Translation.ParallelRequests == 1, "an older INI without the parallel key keeps sequential translation by default");
      Check(((Settings)loadSettings.Invoke(controller, null)).Translation.MinimumChunkCharacters == 2000,
        "an older INI without the minimum chunk key keeps the explicit 2000-character default");
      using (var locked = new FileStream(file + ".api.json", FileMode.Open, FileAccess.Read, FileShare.None)) {
        var saved = (bool)controllerType.GetMethod("SaveSettings", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(controller, new object[] { false });
        Check(!saved, "controller reports API persistence failure without an unhandled exception");
        Check(Win32.ReadIniValue("Options", "ZoomLevel", file, "") == "175", "API persistence failure does not discard unrelated preview options");
        Check(Win32.ReadIniValue("Translation", "ReasoningEffort", file, "") == "high", "API persistence failure does not discard CLI options");
        Check(Win32.GetPrivateProfileInt("Translation", "ParallelRequests", 0, file) == 4, "API persistence failure still saves the shared parallel request count");
        Check(Win32.GetPrivateProfileInt("Translation", "MinimumChunkCharacters", -1, file) == 500,
          "API persistence failure still saves the minimum chunk size alongside the parallel count");
        Check(Win32.ReadIniValue("Translation", "ConnectionMode", file, "") == "cli" && Win32.ReadIniValue("Translation", "ApiConnectionId", file, "") == "previous-id", "failed API save preserves the previous persisted connection selection");
      }
      Check(File.ReadAllText(file + ".api.json") == "[]", "controller failure leaves the locked API file unchanged");
      var originalProfile = new ApiConnection { ApiKey = "synthetic-original-profile-key" };
      ApiConnectionStore.Save(file + ".api.json", new[] { originalProfile });
      var originalBytes = File.ReadAllBytes(file + ".api.json");
      using (var locked = new FileStream(file + ".api.json", FileMode.Open, FileAccess.Read, FileShare.None)) settings.Translation.LoadApiConnections(file + ".api.json");
      settings.Translation.ApiConnections.Add(new ApiConnection { Name = "New profile after failed load" });
      var recoveredSave = (bool)controllerType.GetMethod("SaveSettings", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(controller, new object[] { false });
      Check(recoveredSave && Directory.GetFiles(directory, "settings.ini.api.json.unreadable-*").Any(path => File.ReadAllBytes(path).SequenceEqual(originalBytes)), "a transient startup load failure preserves original credential profiles after the lock clears");
      foreach (var value in new[] { 1, 4, 8 }) {
        settings.Translation.ParallelRequests = value;
        var saved = (bool)controllerType.GetMethod("SaveSettings", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(controller, new object[] { false });
        Check(saved && ((Settings)loadSettings.Invoke(controller, null)).Translation.ParallelRequests == value,
          "controller saves and reloads the shared parallel count: " + value);
      }
      foreach (var value in new[] { "-2", "0", "9", "invalid" }) {
        Win32.WriteIniValue("Translation", "ParallelRequests", value, file);
        Check(((Settings)loadSettings.Invoke(controller, null)).Translation.ParallelRequests == (value == "9" ? 8 : 1),
          "INI parallel count is safely clamped: " + value);
      }
      foreach (var value in new[] { 0, 500, 2000, 1000000 }) {
        settings.Translation.MinimumChunkCharacters = value;
        var saved = (bool)controllerType.GetMethod("SaveSettings", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(controller, new object[] { false });
        Check(saved && ((Settings)loadSettings.Invoke(controller, null)).Translation.MinimumChunkCharacters == value,
          "controller saves and reloads the minimum chunk size including disabled and maximum values: " + value);
      }
      foreach (var value in new[] { "-2", "-1", "1000001", "2147483647", "invalid" }) {
        Win32.WriteIniValue("Translation", "MinimumChunkCharacters", value, file);
        var expected = value == "invalid" ? 2000 : value.StartsWith("-", StringComparison.Ordinal) ? 0 : 1000000;
        Check(((Settings)loadSettings.Invoke(controller, null)).Translation.MinimumChunkCharacters == expected,
          "INI minimum chunk size safely clamps corrupt out-of-range values or uses the default: " + value);
      }
      settings.Translation.ParallelRequests = 4;
      settings.Translation.MinimumChunkCharacters = 2000;
      File.WriteAllText(file, "[Translation]\r\n", Encoding.Unicode);
      foreach (var template in new[] { "plain --flags", "\"first argument\" --middle \"last argument\"", "\"one whole argument\"", "'first' --middle 'last'", "", new string('a', 32765), new string('\u6d4b', 32765) }) {
        settings.Translation.Arguments = template;
        var saved = (bool)controllerType.GetMethod("SaveSettings", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(controller, new object[] { false });
        Check(saved && Win32.ReadIniValue("Translation", "Arguments", file, "") == template, "controller round-trips CLI boundary quotes and valid argument-template length " + template.Length);
      }
      var beforeOversize = File.ReadAllBytes(file);
      settings.Translation.Arguments = new string('a', TranslationOptions.MaximumStoredArgumentCharacters + 1);
      var oversizeSaved = (bool)controllerType.GetMethod("SaveSettings", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(controller, new object[] { false });
      Check(!oversizeSaved && File.ReadAllBytes(file).SequenceEqual(beforeOversize), "an oversized quoted argument template is rejected before any settings write");
      var renderCount = 0;
      var renderedFirstLineSync = false;
      var previewConstructor = typeof(MarkdownPreviewForm).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(Settings), typeof(Func<string, string, bool, Task>) }, null);
      Func<string, string, bool, Task> renderer = (text, path, translated) => {
        renderCount++;
        renderedFirstLineSync = settings.SyncViewWithFirstVisibleLine;
        return Task.CompletedTask;
      };
      using (var preview = (MarkdownPreviewForm)previewConstructor.Invoke(new object[] { settings, renderer })) {
        preview.RenderMarkdown("# Stable content", "test.md").GetAwaiter().GetResult();
        preview.RenderMarkdown("# Stable content", "test.md").GetAwaiter().GetResult();
        Check(renderCount == 1, "unchanged preview content is cached during ordinary updates");
        settings.SyncViewWithFirstVisibleLine = true;
        preview.RenderMarkdown("# Stable content", "test.md", true).GetAwaiter().GetResult();
        Check(renderCount == 2 && renderedFirstLineSync, "forced preview refresh applies changed synchronization settings to unchanged content");
        settings.SyncViewWithFirstVisibleLine = false;
        preview.RenderMarkdown("# Stable content", "test.md", true).GetAwaiter().GetResult();
        Check(renderCount == 3 && !renderedFirstLineSync, "turning synchronization off also refreshes unchanged content");
      }
      var firstLineChanged = controllerType.GetMethod("FirstLineChanged", BindingFlags.NonPublic | BindingFlags.Instance);
      firstLineChanged.Invoke(controller, new[] { Activator.CreateInstance(firstLineChanged.GetParameters()[0].ParameterType) });
      Check(true, "preview first-line events do not call Scintilla when first-line synchronization is disabled");
      var firstProfile = new ApiConnection { Name = "First endpoint" };
      var secondProfile = new ApiConnection { Name = "Selected second endpoint" };
      ApiConnectionStore.Save(file + ".api.json", new[] { firstProfile, secondProfile });
      var beforeFailedLoad = File.ReadAllBytes(file + ".api.json");
      File.WriteAllText(file, "[Translation]\r\nConnectionMode=api\r\nApiConnectionId=" + secondProfile.Id + "\r\n", Encoding.Unicode);
      settings.Translation = new TranslationOptions { ConnectionMode = "api", SelectedApiConnectionId = secondProfile.Id };
      using (var locked = new FileStream(file + ".api.json", FileMode.Open, FileAccess.Read, FileShare.None)) settings.Translation.LoadApiConnections(file + ".api.json");
      using (var form = new SettingsForm(settings)) {
        ((TrackBar)form.Controls.Find("trackBar1", true).Single()).Value = 180;
        typeof(SettingsForm).GetMethod("btnSave_Click", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, new object[] { null, EventArgs.Empty });
        Check(form.DialogResult == DialogResult.OK, "preview changes can be accepted after an API catalog load failure");
        settings.ZoomLevel = form.ZoomLevel;
        settings.Translation = form.TranslationOptions;
      }
      var savedPreviewOnly = (bool)controllerType.GetMethod("SaveSettings", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(controller, new object[] { false });
      var reloaded = new TranslationOptions { SelectedApiConnectionId = Win32.ReadIniValue("Translation", "ApiConnectionId", file) };
      reloaded.LoadApiConnections(file + ".api.json");
      Check(savedPreviewOnly && reloaded.ActiveApiConnection.Id == secondProfile.Id, "preview-only save after a transient API load failure retains the selected second endpoint across restart");
      Check(File.ReadAllBytes(file + ".api.json").SequenceEqual(beforeFailedLoad) && Win32.ReadIniValue("Options", "ZoomLevel", file) == "180", "preview-only recovery save preserves the original API catalog bytes and saves zoom");
      CliProfilePersistence(directory);
      Console.WriteLine("PASS INI: " + checks + " assertions"); return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    finally {
      if (File.Exists(file)) File.SetAttributes(file, FileAttributes.Normal);
      if (Path.GetFullPath(directory).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Directory.Delete(directory, true);
    }
  }
}
