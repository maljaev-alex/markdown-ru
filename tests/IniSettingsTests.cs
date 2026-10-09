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
      // Exercise persistence without initializing or messaging a Notepad++ window.
      var controller = (MarkdownPanelController)FormatterServices.GetUninitializedObject(typeof(MarkdownPanelController));
      var controllerType = typeof(MarkdownPanelController);
      controllerType.GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(controller, settings);
      controllerType.GetField("_iniFilePath", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(controller, file);
      var loadSettings = controllerType.GetMethod("LoadSettingsFromIni", BindingFlags.NonPublic | BindingFlags.Instance);
      Check(((Settings)loadSettings.Invoke(controller, null)).Translation.ParallelRequests == 1, "an older INI without the parallel key keeps sequential translation by default");
      using (var locked = new FileStream(file + ".api.json", FileMode.Open, FileAccess.Read, FileShare.None)) {
        var saved = (bool)controllerType.GetMethod("SaveSettings", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(controller, new object[] { false });
        Check(!saved, "controller reports API persistence failure without an unhandled exception");
        Check(Win32.ReadIniValue("Options", "ZoomLevel", file, "") == "175", "API persistence failure does not discard unrelated preview options");
        Check(Win32.ReadIniValue("Translation", "ReasoningEffort", file, "") == "high", "API persistence failure does not discard CLI options");
        Check(Win32.GetPrivateProfileInt("Translation", "ParallelRequests", 0, file) == 4, "API persistence failure still saves the shared parallel request count");
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
      settings.Translation.ParallelRequests = 4;
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
      Console.WriteLine("PASS INI: " + checks + " assertions"); return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    finally {
      if (File.Exists(file)) File.SetAttributes(file, FileAttributes.Normal);
      if (Path.GetFullPath(directory).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Directory.Delete(directory, true);
    }
  }
}
