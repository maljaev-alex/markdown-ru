using System;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using AnotherMarkdown.Entities;
using AnotherMarkdown.Forms;
using AnotherMarkdown.Translation;

internal static class SettingsTests
{
  private static int passed;
  [STAThread]
  private static int Main(string[] args)
  {
    try {
      Application.EnableVisualStyles();
      Console.WriteLine("Host stdin encoding: " + Console.InputEncoding.WebName + "; preamble: " + BitConverter.ToString(Console.InputEncoding.GetPreamble()));
      if (args.Length > 0 && args[0] == "live-ui") return LiveUi();
      if (args.Length > 0 && args[0] == "live-api-ui") return LiveApiUi();
      using (var picker = SettingsForm.CreateCliFileDialog(Assembly.GetExecutingAssembly().Location)) {
        Check(picker.InitialDirectory == Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) && picker.FileName == Assembly.GetExecutingAssembly().Location, "CLI picker starts in selected executable directory");
        Check(picker.Filter == "CLI (*.exe;*.cmd;*.bat)|*.exe;*.cmd;*.bat", "CLI picker supports native programs and official batch launchers");
      }
      var settings = new Settings { ZoomLevel = 9000, EnabledMarkdownPlugins = new[] { "attrs" } };
      EffortSettings();
      SimpleModelSettings();
      ModelSortingSettings();
      ApiSettings();
      ApiProxySettings();
      SocksProxySettings();
      DraftSettings();
      GeometrySettings();
      settings.Translation.ShowButtons = false;
      using (var form = new SettingsForm(settings)) {
        Check(Find<TrackBar>(form, "trackBar1").Value == 800, "invalid stored zoom is clamped");
        Check(Find<TextBox>(form, "tbCssFile").Text == "", "empty CSS keeps default reset semantics");
        Check(!Find<ComboBox>(form, "translationOutput").Enabled && Find<TextBox>(form, "translationArguments").ReadOnly, "automatic arguments and output format are read-only");
        var customArguments = (CheckBox)typeof(SettingsForm).GetField("translationCustomArguments", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
        customArguments.Checked = true;
        Find<ComboBox>(form, "translationOutput").SelectedItem = "agy-json";
        Find<TextBox>(form, "translationArguments").Text = "custom arguments";
        customArguments.Checked = false;
        Check((string)Find<ComboBox>(form, "translationOutput").SelectedItem == "text" && Find<TextBox>(form, "translationArguments").Text.Contains("exec --skip-git-repo-check"), "leaving custom arguments restores the matching adapter format and preview");
        var tabs = Find<TabControl>(form, "settingsTabs");
        form.SelectTranslationTab();
        Check(tabs.SelectedTab.Name == "translationPage", "translation tab is a native tab page");
        Find<TextBox>(form, "tbCssFile").Text = "changed.css";
        Find<TextBox>(form, "tbDarkmodeCssFile").Text = "dark.css";
        Find<TrackBar>(form, "trackBar1").Value = 175;
        Find<CheckBox>(form, "cbShowToolbar").Checked = true;
        typeof(SettingsForm).GetMethod("btnSave_Click", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { null, EventArgs.Empty });
        Check(form.DialogResult == DialogResult.OK && form.CssFileName == "changed.css" && form.CssDarkModeFileName == "dark.css" && form.ZoomLevel == 175 && form.ShowToolbar, "save captures edited preview controls");
        Check(settings.CssFileName == null && settings.ZoomLevel == 9000 && !settings.ShowToolbar, "dialog edits do not mutate supplied settings before acceptance");
        Check(!form.TranslationOptions.ShowButtons, "translation visibility persists");
      }
      Console.WriteLine("PASS settings: " + passed + " assertions");
      return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
  }
  private static void Save(SettingsForm form) => typeof(SettingsForm).GetMethod("btnSave_Click", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { null, EventArgs.Empty });
  private static void DraftSettings()
  {
    var settings = new Settings { ZoomLevel = 100, EnabledMarkdownPlugins = new[] { "attrs" } };
    settings.Translation.Executable = "missing-cli-xyz";
    settings.Translation.Model = "";
    settings.Translation.ShowButtons = true;
    using (var form = new SettingsForm(settings)) {
      Find<TrackBar>(form, "trackBar1").Value = 150;
      Find<TextBox>(form, "tbCssFile").Text = "draft.css";
      Save(form);
      Check(form.DialogResult == DialogResult.OK && form.ZoomLevel == 150 && form.CssFileName == "draft.css", "preview settings save with translation buttons enabled before a CLI is installed");
      Check(form.TranslationOptions.ShowButtons && form.TranslationOptions.Executable == "missing-cli-xyz" && form.TranslationOptions.Model == "", "saving an incomplete CLI draft preserves its missing executable and empty model");
    }
    using (var form = new SettingsForm(settings)) {
      Find<TextBox>(form, "translationExecutable").Text = "";
      Save(form);
      Check(form.DialogResult == DialogResult.OK && form.TranslationOptions.Executable == "", "an empty CLI draft does not block preview settings");
    }
    using (var form = new SettingsForm(settings)) {
      Find<TextBox>(form, "translationExecutable").Text = "\"invalid.exe\"";
      Save(form);
      Check(form.DialogResult != DialogResult.OK, "draft saving still rejects malformed CLI executable syntax");
    }
    using (var form = new SettingsForm(settings)) {
      Find<TextBox>(form, "tbAssetsPath").Text = "%TEMP%";
      Save(form);
      Check(form.DialogResult == DialogResult.OK && Directory.Exists(form.AssetsPath) && form.AssetsPath == Environment.ExpandEnvironmentVariables("%TEMP%"), "assets path saves the expanded environment-variable directory that was validated");
    }
    settings.Translation.ShowButtons = false;
    settings.Translation.UseCustomArguments = true;
    using (var form = new SettingsForm(settings)) {
      var arguments = Find<TextBox>(form, "translationArguments");
      Check(arguments.MaxLength == TranslationOptions.MaximumStoredArgumentCharacters, "argument editor retains the lossless INI storage limit");
      arguments.Text = new string('a', TranslationOptions.MaximumStoredArgumentCharacters + 1);
      Save(form);
      Check(form.DialogResult != DialogResult.OK, "hidden translation buttons cannot bypass the custom-argument storage limit");
    }

    settings.Translation = new TranslationOptions { ConnectionMode = "api", ShowButtons = true };
    using (var form = new SettingsForm(settings)) {
      Find<TrackBar>(form, "trackBar1").Value = 160;
      Save(form);
      Check(form.DialogResult == DialogResult.OK && form.ZoomLevel == 160 && form.TranslationOptions.ApiConnections.Count == 0, "preview settings save when API mode has no connection yet");
    }
    settings.Translation.ApiConnections.Add(new ApiConnection { Protocol = "anthropic", Endpoint = "https://example.test/v1", Model = "", MaxOutputTokens = 0 });
    using (var form = new SettingsForm(settings)) {
      Find<TextBox>(form, "tbCssFile").Text = "api-draft.css";
      Save(form);
      Check(form.DialogResult == DialogResult.OK && form.CssFileName == "api-draft.css" && form.TranslationOptions.ActiveApiConnection.Model == "" && form.TranslationOptions.ActiveApiConnection.MaxOutputTokens == 0, "incomplete API model and required output budget can be saved as a draft with preview settings");
    }
    using (var form = new SettingsForm(settings)) {
      Find<TextBox>(form, "apiEndpoint").Text = "not an API URL";
      Save(form);
      Check(form.DialogResult != DialogResult.OK, "incomplete API readiness never bypasses malformed endpoint validation");
    }
    using (var form = new SettingsForm(settings)) {
      Find<TextBox>(form, "apiHeaders").Text = "{invalid-json";
      Save(form);
      Check(form.DialogResult != DialogResult.OK, "incomplete API readiness never bypasses malformed header JSON validation");
    }
    settings.Translation.ConnectionMode = "cli";
    settings.Translation.ShowButtons = false;
    settings.Translation.ApiConnections.Add(new ApiConnection { Endpoint = "https://example.test/v1?key=SYNTHETIC_INACTIVE_SECRET" });
    using (var form = new SettingsForm(settings)) {
      Save(form);
      var status = typeof(SettingsForm).GetField("translationModelStatus", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form).As<Label>();
      Check(form.DialogResult != DialogResult.OK && !status.Text.Contains("SYNTHETIC_INACTIVE_SECRET"), "inactive API profiles retain the credential URL guard even while CLI buttons are hidden");
    }
  }
  private static Rectangle BoundsInForm(Control control, Form form)
  {
    var bounds = control.Bounds;
    for (var parent = control.Parent; parent != null && parent != form; parent = parent.Parent) bounds.Offset(parent.Location);
    return bounds;
  }
  private static void GeometrySettings()
  {
    foreach (var workingArea in new[] { new Rectangle(0, 0, 1920, 1032), new Rectangle(-1366, 40, 1366, 728) }) {
      var settings = new Settings { EnabledMarkdownPlugins = new[] { "attrs" } };
      settings.Translation.ShowButtons = false;
      using (var form = new SettingsForm(settings)) {
        form.Scale(new SizeF(1.5F, 1.5F));
        form.Location = new Point(workingArea.Right + 100, workingArea.Bottom + 100);
        form.ClampToWorkingArea(workingArea);
        Check(workingArea.Contains(form.Bounds), "scaled settings outer bounds fit a " + workingArea.Width + "x" + workingArea.Height + " working area");
        Check(form.ClientRectangle.Contains(BoundsInForm(Find<Button>(form, "btnSave"), form)) && form.ClientRectangle.Contains(BoundsInForm(Find<Button>(form, "btnCancel"), form)), "scaled Save and Cancel remain inside the client and screen at " + workingArea.Width + "x" + workingArea.Height);
        Check(Find<TabPage>(form, "previewPage").AutoScroll && Find<TabPage>(form, "translationPage").AutoScroll && Find<TableLayoutPanel>(form, "previewLayout").AutoScroll, "settings pages can scroll when scaled content exceeds available space");
      }
    }
    var apiSettings = new Settings { EnabledMarkdownPlugins = new[] { "attrs" } };
    apiSettings.Translation = new TranslationOptions { ConnectionMode = "api", ApiConnections = { new ApiConnection {
      Endpoint = "https://example.test/v1", Model = "test-model", ProxyMode = "custom", ProxyAddress = "http://127.0.0.1:3128"
    } } };
    using (var form = new BackgroundSettings(apiSettings)) {
      form.SelectTranslationTab(); form.Show();
      form.Font = new Font("Segoe UI", 13.5F);
      form.ClampToWorkingArea(new Rectangle(form.Left, form.Top, 640, 480));
      var actions = Find<FlowLayoutPanel>(form, "apiActions");
      Find<TabPage>(form, "translationPage").ScrollControlIntoView(Find<Button>(form, "apiClearCredentials"));
      form.PerformLayout(); Application.DoEvents();
      Check(actions.Controls.Cast<Control>().All(control => actions.ClientRectangle.Contains(control.Bounds)),
        "all API action buttons remain reachable at 640x480 with a large font");
      Check(form.ClientRectangle.Contains(BoundsInForm(Find<Button>(form, "btnSave"), form)) && form.ClientRectangle.Contains(BoundsInForm(Find<Button>(form, "btnCancel"), form)),
        "large-font small API window retains the Save and Cancel footer");
      form.Close();
    }
  }
  private sealed class BackgroundSettings : SettingsForm
  {
    public BackgroundSettings(Settings settings) : base(settings) { }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; } }
  }
  private static int LiveUi()
  {
    var result = 1;
    var settings = new Settings { ZoomLevel = 100, EnabledMarkdownPlugins = new[] { "attrs" } };
    settings.Translation.Executable = "codex.cmd";
    using (var form = new BackgroundSettings(settings)) {
      form.SelectTranslationTab();
      form.Shown += async (_, __) => {
        try {
          await WaitFor(() => Find<ComboBox>(form, "translationModel").Items.Count > 2 && Find<Button>(form, "btnSave").Enabled);
          Check(Find<ComboBox>(form, "translationModel").Items.Cast<object>().Any(m => m.ToString().IndexOf("gpt-6-astra", StringComparison.OrdinalIgnoreCase) >= 0), "settings obtains native Codex models");
          using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "settings-basic.png")); }
          var discovered = Find<ComboBox>(form, "translationCli").Items.Cast<CliInstallation>().ToList();
          Check(discovered.All(i => CliProfiles.IsLauncherPath(i.Executable)) && discovered.GroupBy(i => i.ProviderId).All(g => g.Count() == 1) && CliProfiles.IsExePath(Find<TextBox>(form, "translationExecutable").Text), "saved duplicate Codex script migrates to preferred EXE while official launchers remain available");
          Check(discovered.Any(i => i.ProviderId == "cursor"), "installed Cursor launcher is discovered");
          Check(Find<ComboBox>(form, "translationEffort").Items.Count > 2 && Find<ComboBox>(form, "translationEffort").Enabled, "live Codex model exposes available effort choices");
          typeof(SettingsForm).GetField("translationCustomArguments", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form).As<CheckBox>().Checked = true;
          Find<TextBox>(form, "translationArguments").Text = "custom arguments";
          Find<NumericUpDown>(form, "translationTimeout").Value = 23;
          Find<Button>(form, "translationDefaults").PerformClick();
          await WaitFor(() => Find<ComboBox>(form, "translationModel").Items.Count > 2 && Find<Button>(form, "btnSave").Enabled);
          Check(Find<TextBox>(form, "translationArguments").ReadOnly && Find<TextBox>(form, "translationArguments").Text.Contains("exec --skip-git-repo-check") && Find<NumericUpDown>(form, "translationTimeout").Value == 300, "defaults button restores selected CLI arguments and timeout");
          var cliPicker = Find<ComboBox>(form, "translationCli");
          cliPicker.SelectedItem = discovered.Single(i => i.ProviderId == "cursor");
          await WaitFor(() => Find<ComboBox>(form, "translationModel").Items.Count > 2 && Find<Button>(form, "btnSave").Enabled);
          var modelPicker = Find<ComboBox>(form, "translationModel");
          modelPicker.SelectedItem = modelPicker.Items.Cast<object>().Single(m => m.ToString() == "Grok 4.7");
          var effortPicker = Find<ComboBox>(form, "translationEffort");
          effortPicker.SelectedItem = effortPicker.Items.Cast<object>().Single(e => e.ToString().Contains("xhigh"));
          Check(effortPicker.Enabled && ReadDraft(form).Model == "grok-4.7-xhigh", "live Cursor settings select Grok Extra High by advertised alias");
          using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "settings-cursor.png")); }
          cliPicker.SelectedItem = discovered.Single(i => i.ProviderId == "codex");
          await WaitFor(() => Find<ComboBox>(form, "translationModel").Items.Count > 2 && Find<Button>(form, "btnSave").Enabled);
          typeof(SettingsForm).GetField("translationAdvanced", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form).As<CheckBox>().Checked = true;
          using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "settings-advanced.png")); }
          typeof(SettingsForm).GetMethod("btnSave_Click", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { null, EventArgs.Empty });
          Check(form.TranslationOptions.ProviderId == "codex" && form.TranslationOptions.UseDefaultModel && form.TranslationOptions.OutputFormat == "text", "settings commits selected CLI and adapter format");
          result = 0;
        }
        catch (Exception error) {
          Console.Error.WriteLine(error);
          Console.Error.WriteLine("CLI: " + Find<ComboBox>(form, "translationCli").Text + "; path: " + Find<TextBox>(form, "translationExecutable").Text + "; models: " + Find<ComboBox>(form, "translationModel").Items.Count + "; save: " + Find<Button>(form, "btnSave").Enabled);
          foreach (var label in form.Controls.Find("translationLayout", true).Single().Controls.OfType<Label>()) Console.Error.WriteLine(label.Text);
          using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "settings-failure.png")); }
        }
        finally { form.Close(); }
      };
      Application.Run(form);
    }
    return result;
  }
  private static int LiveApiUi()
  {
    var result = 1;
    var settings = new Settings { EnabledMarkdownPlugins = new[] { "attrs" } };
    var connection = new ApiConnection { Name = "Example API", Endpoint = "https://example.test/v1", Model = "example-model", ApiKey = "fake-ui-key",
      ProxyMode = "custom", ProxyAddress = "http://127.0.0.1:3128", ProxyUsername = "fake-ui-proxy-user", ProxyPassword = "fake-ui-proxy-password" };
    settings.Translation.ConnectionMode = "api"; settings.Translation.ApiConnections.Add(connection); settings.Translation.SelectedApiConnectionId = connection.Id;
    using (var form = new BackgroundSettings(settings)) {
      form.SelectTranslationTab();
      form.Shown += (_, __) => {
        try {
          var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
          using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(directory, "settings-api-basic.png")); }
          Check(Find<TextBox>(form, "apiKey").UseSystemPasswordChar && Find<ComboBox>(form, "translationConnectionMode").SelectedIndex == 1, "live API form restores selected mode and masks the key");
          Check(Find<TextBox>(form, "apiProxyPassword").UseSystemPasswordChar && Find<TableLayoutPanel>(form, "apiProxyLayout").Visible,
            "live API form exposes custom proxy fields and masks the proxy password");
          Find<CheckBox>(form, "apiAdvanced").Checked = true;
          using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(directory, "settings-api-advanced.png")); }
          var profile = ReadDraft(form).ActiveApiConnection;
          var path = Path.Combine(directory, "api-ui-roundtrip.json");
          ApiConnectionStore.Save(path, new[] { profile });
          var loaded = ApiConnectionStore.Load(path).Single();
          Check(loaded.ApiKey == "fake-ui-key" && loaded.Model == "example-model" && !File.ReadAllText(path).Contains("fake-ui-key"), "live API form values persist encrypted and restore correctly");
          Check(loaded.ProxyMode == "custom" && loaded.ProxyUsername == "fake-ui-proxy-user" && loaded.ProxyPassword == "fake-ui-proxy-password"
            && !File.ReadAllText(path).Contains("fake-ui-proxy"), "live proxy fields roundtrip through protected storage");
          Find<Button>(form, "btnSave").PerformClick();
          Check(form.DialogResult == DialogResult.OK && form.TranslationOptions.UseApi, "live API form saves without invoking CLI or external API");
          result = 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); }
        finally { form.Close(); }
      };
      Application.Run(form);
    }
    return result;
  }
  private static T As<T>(this object value) => (T)value;
  private static void Click(Button button) => typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(button, new object[] { EventArgs.Empty });
  private static void EffortSettings()
  {
    var settings = new Settings { EnabledMarkdownPlugins = new[] { "attrs" } };
    settings.Translation.ShowButtons = false;
    using (var form = new SettingsForm(settings)) {
      var options = CliProfiles.Defaults("codex", Assembly.GetExecutingAssembly().Location);
      var model = new CliModel { Id = "test", Name = "Test", DefaultReasoningEffort = "low", IsDefault = true };
      model.ReasoningEfforts.Add(new CliReasoningEffort { Id = "low" });
      model.ReasoningEfforts.Add(new CliReasoningEffort { Id = "ultra" });
      var catalog = new CliModelCatalog { DefaultModelId = "test", Models = { model, new CliModel { Id = "plain" } } };
      FillCatalog(form, options, catalog);
      var efforts = Find<ComboBox>(form, "translationEffort");
      Check(efforts.Enabled && efforts.Items.Count == 3, "Codex effort selector uses advertised choices");
      var custom = (CheckBox)typeof(SettingsForm).GetField("translationCustomArguments", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
      var note = (Label)typeof(SettingsForm).GetField("translationEffortNote", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
      custom.Checked = true; custom.Checked = false;
      Check(efforts.Enabled && note.Text == "", "leaving custom arguments refreshes effort control and note");
      using (var refreshing = new System.Threading.CancellationTokenSource()) {
        typeof(SettingsForm).GetField("modelCancellation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, refreshing);
        custom.Checked = true; custom.Checked = false;
        Check(!efforts.Enabled, "custom toggle cannot enable stale effort choices during model refresh");
        typeof(SettingsForm).GetField("modelCancellation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, null);
      }
      efforts.SelectedIndex = 2;
      Check(ReadDraft(form).ReasoningEffort == "ultra" && Find<TextBox>(form, "translationArguments").Text.Contains("model_reasoning_effort=ultra"), "effort selection updates saved draft and argument preview");
      var models = Find<ComboBox>(form, "translationModel");
      models.SelectedItem = models.Items.Cast<object>().Single(m => m.ToString() == "plain");
      Check(!efforts.Enabled && ReadDraft(form).ReasoningEffort == "", "switching to model without effort clears previous override");
      options = CliProfiles.Defaults("cursor", options.Executable); options.UseDefaultModel = false; options.Model = "grok-4.7-xhigh";
      catalog = CliModelDiscovery.ParseCommandOutput("cursor", "auto - Auto (default)\ngrok-4.7-low - Grok 4.7 Low\ngrok-4.7-medium - Grok 4.7 Medium\ngrok-4.7-xhigh - Grok 4.7 Extra High\ngrok-4.7-xhigh-fast - Grok 4.7 Extra High Fast\n", default(System.Threading.CancellationToken));
      FillCatalog(form, options, catalog);
      Check(Find<ComboBox>(form, "translationModel").Items.Count == 3 && efforts.Items.Count == 3 && ReadDraft(form).Model == "grok-4.7-xhigh", "Cursor groups exact effort variants, hides Fast and restores saved full model ID");
      efforts.SelectedIndex = 1;
      Check(ReadDraft(form).Model == "grok-4.7-medium" && ReadDraft(form).ReasoningEffort == "medium" && !ReadDraft(form).UseDefaultModel, "Cursor effort selects returned model ID instead of fabricated base");
      Check(!Find<ComboBox>(form, "translationModel").Items.Cast<object>().Any(m => m.ToString().IndexOf("Fast", StringComparison.OrdinalIgnoreCase) >= 0), "Cursor Fast variants remain hidden from the model picker");
      Find<ComboBox>(form, "translationModel").SelectedIndex = 0;
      Check(!efforts.Enabled && ReadDraft(form).UseDefaultModel && ReadDraft(form).ReasoningEffort == "", "Cursor CLI default retains Auto and exposes no invented effort");
      options.Model = "grok-4.7-xhigh-fast";
      FillCatalog(form, options, catalog);
      Check(ReadDraft(form).Model == "grok-4.7-xhigh" && ReadDraft(form).ReasoningEffort == "xhigh", "saved Cursor Fast model migrates only to its exact advertised normal variant and retains effort");
      options.Model = "not-advertised-low-fast";
      FillCatalog(form, options, catalog);
      Check(ReadDraft(form).UseDefaultModel && ReadDraft(form).ReasoningEffort == "" && !Find<ComboBox>(form, "translationModel").Items.Cast<object>().Any(m => m.ToString().IndexOf("Fast", StringComparison.OrdinalIgnoreCase) >= 0), "missing normal Cursor counterpart falls back to CLI default without reintroducing a saved Fast row");
      var nativeEfforts = new System.Collections.Generic.List<CliReasoningEffort> {
        new CliReasoningEffort { Id = "low", ModelId = "native-low" },
        new CliReasoningEffort { Id = "", ModelId = "native", Description = "CLI default" }
      };
      catalog = new CliModelCatalog { DefaultModelId = "native", Models = {
        new CliModel { Id = "native-low", Name = "Native Low", BaseModelId = "native", BaseModelName = "Native", DefaultReasoningEffort = "low", ReasoningEfforts = nativeEfforts },
        new CliModel { Id = "native", Name = "Native", BaseModelId = "native", BaseModelName = "Native", DefaultReasoningEffort = "", ReasoningEfforts = nativeEfforts }
      } };
      options.Model = "native"; options.ReasoningEffort = "";
      FillCatalog(form, options, catalog);
      Check(efforts.Items.Count == 2 && efforts.Text == "\u041f\u043e \u0443\u043c\u043e\u043b\u0447\u0430\u043d\u0438\u044e \u0432 CLI" && ReadDraft(form).Model == "native" && ReadDraft(form).ReasoningEffort == "", "native Cursor default restores its exact unsuffixed model with one readable effort row");
      efforts.SelectedIndex = 0;
      Check(ReadDraft(form).Model == "native-low", "native default and explicit Cursor effort retain distinct returned IDs");
      options.UseDefaultModel = true;
      FillCatalog(form, options, catalog);
      Check(efforts.Items.Count == 2 && efforts.Items.Cast<object>().Count(e => e.ToString().StartsWith("\u041f\u043e \u0443\u043c\u043e\u043b\u0447\u0430\u043d\u0438\u044e \u0432 CLI", StringComparison.Ordinal)) == 1 && ReadDraft(form).UseDefaultModel, "global CLI fallback does not duplicate or pin the native-default effort alias");
      options = CliProfiles.Defaults("codex", options.Executable); options.UseDefaultModel = false; options.Model = "other-fast";
      catalog = new CliModelCatalog { Models = { new CliModel { Id = "other-fast", Name = "Other Fast" } } };
      FillCatalog(form, options, catalog);
      Check(ReadDraft(form).Model == "other-fast" && !ReadDraft(form).UseDefaultModel, "Fast filtering and migration do not affect another CLI provider");
    }
  }
  private static void SimpleModelSettings()
  {
    var settings = new Settings { EnabledMarkdownPlugins = new[] { "attrs" } };
    settings.Translation.ShowButtons = false;
    using (var form = new SettingsForm(settings)) {
      var catalog = CliModelDiscovery.ParseCommandOutput("cursor", "claude-haiku-5-5-low - Claude Haiku 5.5 No Thinking\nclaude-haiku-5-5-thinking-low - Claude Haiku 5.5 Thinking\nclaude-haiku-5-5-thinking-max - Claude Haiku 5.5 Thinking\nclaude-fable-5-1m - Claude Fable 5 1M (NO ZDR)\nclaude-fable-5-1m-thinking - Claude Fable 5 1M Thinking (NO ZDR)\n", default(System.Threading.CancellationToken));
      var options = CliProfiles.Defaults("cursor", "agent.cmd");
      options.UseDefaultModel = false; options.Model = "claude-haiku-5-5-low"; options.ReasoningEffort = "low";
      FillCatalog(form, options, catalog);
      var models = Find<ComboBox>(form, "translationModel");
      var efforts = Find<ComboBox>(form, "translationEffort");
      Check(models.Items.Count == 3 && models.Items.Cast<object>().Skip(1).Select(m => m.ToString()).SequenceEqual(new[] { "Claude Fable 5 1M", "Claude Haiku 5.5" }), "Cursor model selector shows one simple name per family without ZDR or Thinking variants");
      Check(ReadDraft(form).Model == "claude-haiku-5-5-low" && ReadDraft(form).ReasoningEffort == "low", "restoring a saved normal alias preserves its exact advertised ID when an equivalent thinking alias exists");
      efforts.SelectedItem = efforts.Items.Cast<object>().Single(e => e.ToString() == "max");
      Check(ReadDraft(form).Model == "claude-haiku-5-5-thinking-max" && ReadDraft(form).ReasoningEffort == "max", "the effort selector chooses the exact advertised thinking alias behind the simple model name");
      options.Model = "claude-haiku-5-5-thinking-low";
      FillCatalog(form, options, catalog);
      Check(ReadDraft(form).Model == options.Model, "restoring a saved thinking alias also retains its exact advertised ID");
      models.SelectedItem = models.Items.Cast<object>().Single(model => model.ToString() == "Claude Fable 5 1M");
      Check(efforts.Items.Count == 1 && !efforts.Enabled, "effort is disabled when the CLI exposes only one unspecified mode rather than an actual level choice");
      options.Model = "missing-thinking-model-no-zdr";
      FillCatalog(form, options, new CliModelCatalog());
      Check(models.Text.IndexOf("thinking", StringComparison.OrdinalIgnoreCase) < 0 && models.Text.IndexOf("zdr", StringComparison.OrdinalIgnoreCase) < 0 && ReadDraft(form).Model == options.Model, "saved-model fallback hides technical markers while preserving its request ID");
      var apiCatalog = new CliModelCatalog { Models = {
        new CliModel { Id = "vendor/beta-thinking", Name = "Beta Thinking (NO ZDR)" },
        new CliModel { Id = "vendor/alpha", Name = "Alpha ZDR" }
      } };
      typeof(SettingsForm).GetMethod("FillApiModels", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { apiCatalog, "vendor/beta-thinking" });
      var apiModels = Find<ComboBox>(form, "apiModel");
      Check(apiModels.Items.Cast<CliModel>().Select(m => m.ToString()).SequenceEqual(new[] { "Alpha", "Beta" }) && (apiModels.SelectedItem as CliModel)?.Id == "vendor/beta-thinking", "API model display is simple and sorted while exact request IDs are retained");
    }
  }
  private static TranslationOptions ReadDraft(SettingsForm form) => (TranslationOptions)typeof(SettingsForm).GetMethod("ReadTranslationDraft", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
  private static void ModelSortingSettings()
  {
    var settings = new Settings { EnabledMarkdownPlugins = new[] { "attrs" } };
    settings.Translation.ShowButtons = false;
    using (var form = new SettingsForm(settings)) {
      var catalog = new CliModelCatalog { DefaultModelId = "aaa", Models = {
        new CliModel { Id = "aaa", Name = "zeta", IsDefault = true },
        new CliModel { Id = "z-last", Name = "alpha" },
        new CliModel { Id = "a-first", Name = "ALPHA" },
        new CliModel { Id = "middle", Name = "Bravo" },
        new CliModel { Id = "empty-name", Name = "" }
      } };
      var options = CliProfiles.Defaults("codex", Assembly.GetExecutingAssembly().Location);
      FillCatalog(form, options, catalog);
      var models = Find<ComboBox>(form, "translationModel");
      Check(models.SelectedIndex == 0 && ReadDraft(form).UseDefaultModel, "sorted CLI list keeps the configured default fallback first");
      Check(models.Items.Cast<object>().Skip(1).Select(m => m.ToString()).SequenceEqual(new[] { "ALPHA", "alpha", "Bravo", "empty-name", "zeta" }), "CLI models sort by display name ignoring case with deterministic IDs");
      options.UseDefaultModel = false; options.Model = "middle";
      FillCatalog(form, options, catalog);
      Check(ReadDraft(form).Model == "middle" && models.Text == "Bravo", "CLI sorting restores an explicit saved model by ID");
      models.SelectedItem = models.Items.Cast<object>().Single(m => m.ToString() == "alpha");
      var selected = ReadDraft(form);
      catalog.Models.Reverse();
      FillCatalog(form, selected, catalog);
      Check(ReadDraft(form).Model == "z-last" && models.Text == "alpha", "CLI refresh order does not change the selected model");
      selected.Model = "outside-catalog";
      FillCatalog(form, selected, catalog);
      Check(ReadDraft(form).Model == "outside-catalog", "sorting preserves a saved CLI model absent from discovery");

      Find<ComboBox>(form, "translationConnectionMode").SelectedIndex = 1;
      var fill = typeof(SettingsForm).GetMethod("FillApiModels", BindingFlags.Instance | BindingFlags.NonPublic);
      fill.Invoke(form, new object[] { catalog, "middle" });
      var apiModels = Find<ComboBox>(form, "apiModel");
      Check(apiModels.Items.Cast<CliModel>().Select(m => m.Id).SequenceEqual(new[] { "a-first", "z-last", "middle", "empty-name", "aaa" }), "API models sort by name ignoring case then ID, using ID for empty names");
      Check(apiModels.SelectedItem.As<CliModel>().Id == "middle" && ReadDraft(form).ActiveApiConnection.Model == "middle", "API sorting restores the saved ID rather than the first sorted model");
      apiModels.SelectedItem = apiModels.Items.Cast<CliModel>().Single(m => m.Id == "z-last");
      var apiSelected = ReadDraft(form).ActiveApiConnection.Model;
      catalog.Models.Reverse();
      fill.Invoke(form, new object[] { catalog, apiSelected });
      Check(apiModels.SelectedItem.As<CliModel>().Id == "z-last", "API refresh order preserves an explicit selection");
      fill.Invoke(form, new object[] { catalog, "manual-unlisted-model" });
      Check(apiModels.SelectedItem == null && apiModels.Text == "manual-unlisted-model" && ReadDraft(form).ActiveApiConnection.Model == "manual-unlisted-model", "sorting preserves a manually entered API model absent from discovery");
    }
  }
  private static void ApiSettings()
  {
    var settings = new Settings { EnabledMarkdownPlugins = new[] { "attrs" } };
    settings.Translation.ShowButtons = false;
    settings.Translation.ApiConfigurationError = "Unreadable config preserved";
    using (var form = new SettingsForm(settings)) {
      form.SelectTranslationTab();
      Find<ComboBox>(form, "translationConnectionMode").SelectedIndex = 1;
      Check(ReadDraft(form).UseApi && ReadDraft(form).ApiConnections.Count == 1, "switching to API creates an editable connection without changing CLI settings");
      var limit = Find<NumericUpDown>(form, "apiMaxTokens");
      Check(limit.Minimum == 0 && limit.Value == 0, "new API connection preserves the service default output limit");
      var presets = Find<ComboBox>(form, "apiPreset");
      presets.SelectedItem = presets.Items.Cast<object>().Single(p => p.ToString() == "Anthropic");
      Check(limit.Value == 8192, "Anthropic preset supplies its required explicit output limit");
      Check(Find<NumericUpDown>(form, "apiTemperature").Maximum == 1, "Anthropic temperature control uses its protocol-specific maximum");
      presets.SelectedItem = presets.Items.Cast<object>().Single(p => p.ToString() == "OpenAI");
      Check(limit.Value == 0 && ReadDraft(form).ActiveApiConnection.MaxOutputTokens == 0, "OpenAI preset restores the service default rather than a fixed reasoning budget");
      Check(Find<NumericUpDown>(form, "apiTemperature").Maximum == 2, "switching API protocol restores the matching temperature range");
      Find<TextBox>(form, "apiName").Text = "First endpoint";
      Find<TextBox>(form, "apiEndpoint").Text = "https://example.test/v1";
      Find<TextBox>(form, "apiKey").Text = "fake-key-for-settings-test";
      Find<ComboBox>(form, "apiModel").Text = "test-model";
      Find<TextBox>(form, "apiHeaders").Text = "{\"X-Extra\":\"fake-header\"}";
      var first = ReadDraft(form).ActiveApiConnection;
      Check(Find<TextBox>(form, "apiKey").UseSystemPasswordChar && first.ApiKey == "fake-key-for-settings-test" && first.Model == "test-model", "API key is masked and entered values reach the draft");
      using (var waiting = new System.Threading.CancellationTokenSource()) {
        typeof(SettingsForm).GetField("apiCancellation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, waiting);
        Click(Find<Button>(form, "apiAdd"));
        Check(waiting.IsCancellationRequested, "adding another API profile cancels the previous model discovery");
      }
      var connections = Find<ComboBox>(form, "apiConnections");
      connections.SelectedIndex = 0;
      Check(Find<TextBox>(form, "apiKey").Text == "fake-key-for-settings-test" && Find<ComboBox>(form, "apiModel").Text == "test-model", "saved draft profiles retain independent model and credential values");
      var draft = ReadDraft(form); draft.ActiveApiConnection.ApiKey = "changed copy";
      Check(ReadDraft(form).ActiveApiConnection.ApiKey == "fake-key-for-settings-test" && settings.Translation.ApiConnections.Count == 0, "API draft copies and canceled dialog never mutate supplied settings");
      typeof(SettingsForm).GetField("translationDraft", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, CliProfiles.Defaults("cursor", "agent.cmd"));
      Check(ReadDraft(form).ApiConfigurationError == "Unreadable config preserved", "CLI defaults preserve API load-error guard against overwriting corrupt settings");
      var endpoint = connections.SelectedItem.As<ApiConnection>();
      endpoint.EncryptedApiKey = "unreadable-ciphertext";
      Click(Find<Button>(form, "apiClearCredentials"));
      Check(ReadDraft(form).ActiveApiConnection.EncryptedApiKey == "" && ReadDraft(form).ActiveApiConnection.ApiKey == "", "explicit API credential clearing resets ciphertext and visible key");
      Click(Find<Button>(form, "apiRemove")); Click(Find<Button>(form, "apiRemove"));
      Check(ReadDraft(form).ApiConnections.Count == 0, "deleting all API profiles produces an empty persisted draft");
    }
    settings.Translation = new TranslationOptions { ConnectionMode = "api", ApiConnections = { new ApiConnection { Name = "Saved API", Endpoint = "https://example.test/v1", Model = "saved-model", ApiKey = "fake-saved-key" } } };
    settings.Translation.SelectedApiConnectionId = settings.Translation.ApiConnections[0].Id;
    settings.Translation.ShowButtons = false;
    using (var form = new SettingsForm(settings)) {
      Find<TextBox>(form, "apiEndpoint").Text = "https://example.test/v1?key=SYNTHETIC_URL_SECRET";
      typeof(SettingsForm).GetMethod("btnSave_Click", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { null, EventArgs.Empty });
      Check(form.DialogResult != DialogResult.OK && !Find<Label>(form, "apiStatus").Text.Contains("SYNTHETIC_URL_SECRET"), "hidden translation buttons cannot bypass endpoint-secret validation on save");
    }
    settings.Translation.ShowButtons = true;
    using (var form = new SettingsForm(settings)) {
      Check(ReadDraft(form).UseApi && Find<TextBox>(form, "apiKey").Text == "fake-saved-key", "API mode and stored connection restore independently of CLI discovery");
      Find<TextBox>(form, "apiEndpoint").Text = "https://example.test/custom/v1";
      typeof(SettingsForm).GetMethod("btnSave_Click", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { null, EventArgs.Empty });
      Check(form.DialogResult == DialogResult.OK && form.TranslationOptions.ActiveApiConnection.Endpoint == "https://example.test/custom/v1" && settings.Translation.ActiveApiConnection.Endpoint == "https://example.test/v1", "Save captures API values without requiring a CLI executable or mutating original settings");
    }
  }
  private static void ApiProxySettings()
  {
    var settings = new Settings { EnabledMarkdownPlugins = new[] { "attrs" } };
    var original = new ApiConnection { Name = "Proxy endpoint", Endpoint = "https://example.test/v1", Model = "test-model" };
    settings.Translation = new TranslationOptions { ConnectionMode = "api", ApiConnections = { original }, SelectedApiConnectionId = original.Id };
    using (var form = new SettingsForm(settings)) {
      form.SelectTranslationTab();
      var mode = Find<ComboBox>(form, "apiProxyMode");
      var address = Find<TextBox>(form, "apiProxyAddress");
      var username = Find<TextBox>(form, "apiProxyUsername");
      var password = Find<TextBox>(form, "apiProxyPassword");
      var windows = Find<CheckBox>(form, "apiProxyUseDefaultCredentials");
      Check(ReadDraft(form).ActiveApiConnection.ProxyMode == "system" && password.UseSystemPasswordChar,
        "older API profiles default to the system proxy and proxy passwords are masked");
      mode.SelectedIndex = 2;
      address.Text = "http://127.0.0.1:3128";
      username.Text = "synthetic-proxy-user";
      password.Text = "  synthetic-proxy-password  ";
      var profile = ReadDraft(form).ActiveApiConnection;
      Check(profile.ProxyMode == "custom" && profile.ProxyAddress == address.Text && profile.ProxyUsername == username.Text && profile.ProxyPassword == password.Text,
        "custom proxy fields reach the draft without trimming significant credential whitespace");
      windows.Checked = true;
      Check(!username.Enabled && !password.Enabled && ReadDraft(form).ActiveApiConnection.ProxyUseDefaultCredentials,
        "Windows proxy authentication disables explicit credentials without discarding them");
      windows.Checked = false;
      Check(username.Enabled && password.Enabled && password.Text == "  synthetic-proxy-password  ",
        "switching proxy authentication back restores the explicit credentials");
      using (var waiting = new System.Threading.CancellationTokenSource()) {
        typeof(SettingsForm).GetField("apiCancellation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, waiting);
        address.Text = "http://127.0.0.1:3129";
        Check(waiting.IsCancellationRequested, "editing the proxy cancels an outstanding API discovery request");
      }
      var preset = Find<ComboBox>(form, "apiPreset");
      preset.SelectedItem = preset.Items.Cast<object>().Single(p => p.ToString() == "Anthropic");
      profile = ReadDraft(form).ActiveApiConnection;
      Check(profile.ProxyMode == "custom" && profile.ProxyPassword == "  synthetic-proxy-password  ",
        "changing the API service preserves the independently configured proxy");
      Find<ComboBox>(form, "apiModel").Text = "test-model";
      Click(Find<Button>(form, "apiAdd"));
      Check(ReadDraft(form).ActiveApiConnection.ProxyMode == "system" && Find<TextBox>(form, "apiProxyPassword").Text == "",
        "new endpoint starts with independent proxy settings and no copied password");
      Find<ComboBox>(form, "apiConnections").SelectedIndex = 0;
      Check(Find<TextBox>(form, "apiProxyPassword").Text == "  synthetic-proxy-password  " && Find<TextBox>(form, "apiProxyAddress").Text.EndsWith(":3129"),
        "switching endpoints restores each proxy draft");
      mode.SelectedIndex = 1;
      Check(!Find<TableLayoutPanel>(form, "apiProxyLayout").Visible && ReadDraft(form).ActiveApiConnection.ProxyMode == "direct",
        "direct mode hides proxy details while retaining the draft for switching back");
      mode.SelectedIndex = 2;
      var copy = ReadDraft(form); copy.ActiveApiConnection.ProxyPassword = "modified-copy";
      Check(ReadDraft(form).ActiveApiConnection.ProxyPassword == "  synthetic-proxy-password  " && original.ProxyMode == "system" && original.ProxyPassword == "",
        "proxy edits and draft copies do not mutate the supplied settings");
      Save(form);
      profile = form.TranslationOptions.ActiveApiConnection;
      Check(form.DialogResult == DialogResult.OK && profile.ProxyMode == "custom" && profile.ProxyPassword == "  synthetic-proxy-password  ",
        "Save captures the selected endpoint proxy and credentials");
    }
    original.ProxyMode = "custom"; original.ProxyAddress = "http://127.0.0.1:3128";
    original.ProxyUsername = "synthetic-proxy-user"; original.ProxyPassword = "synthetic-proxy-password";
    using (var form = new SettingsForm(settings)) {
      Click(Find<Button>(form, "apiClearCredentials"));
      Check(ReadDraft(form).ActiveApiConnection.ProxyUsername == "" && ReadDraft(form).ActiveApiConnection.ProxyPassword == "" && Find<TextBox>(form, "apiProxyPassword").Text == "",
        "explicit credential clearing also clears both saved proxy credentials");
    }
    using (var form = new SettingsForm(settings)) {
      Find<TextBox>(form, "apiProxyAddress").Text = "http://user:SYNTHETIC_PROXY_SECRET@proxy.example:3128";
      Save(form);
      Check(form.DialogResult != DialogResult.OK && !Find<Label>(form, "apiStatus").Text.Contains("SYNTHETIC_PROXY_SECRET"),
        "proxy credentials inside the URL are rejected on save without exposing them");
    }
  }

  private static void SocksProxySettings()
  {
    var settings = new Settings { EnabledMarkdownPlugins = new[] { "attrs" } };
    var connection = new ApiConnection { Endpoint = "https://example.test/v1", Model = "test-model", ProxyMode = "custom" };
    settings.Translation = new TranslationOptions { ConnectionMode = "api", ApiConnections = { connection }, SelectedApiConnectionId = connection.Id };
    using (var form = new SettingsForm(settings)) {
      var type = Find<ComboBox>(form, "apiProxyProtocol"); var address = Find<TextBox>(form, "apiProxyAddress");
      var windows = Find<CheckBox>(form, "apiProxyUseDefaultCredentials");
      type.SelectedIndex = 1; address.Text = "127.0.0.1:8090";
      Check(ReadDraft(form).ActiveApiConnection.ProxyAddress == "socks5h://127.0.0.1:8090" && !windows.Enabled && !windows.Checked,
        "SOCKS5 choice builds the proxy URL and disables Windows authentication");
      Find<TextBox>(form, "apiProxyPassword").Text = "fake-socks-password";
      type.SelectedIndex = 0;
      Check(ReadDraft(form).ActiveApiConnection.ProxyAddress == "http://127.0.0.1:8090" && windows.Enabled,
        "switching proxy type changes the saved scheme while retaining the address");
      windows.Checked = true;
      address.Text = "socks5://127.0.0.1:8090";
      Check(type.SelectedIndex == 2 && !windows.Checked && !windows.Enabled && Find<TextBox>(form, "apiProxyPassword").Enabled,
        "pasting a SOCKS5 URL selects the matching DNS mode and restores manual authentication");
      Check(ReadDraft(form).ActiveApiConnection.ProxyPassword == "fake-socks-password", "proxy type changes preserve the draft password");
      Save(form);
      Check(form.DialogResult == DialogResult.OK && form.TranslationOptions.ActiveApiConnection.ProxyAddress == "socks5://127.0.0.1:8090",
        "SOCKS5 settings save with their explicit DNS mode");
    }
    connection.ProxyAddress = "socks5h://127.0.0.1:8090";
    using (var form = new SettingsForm(settings)) {
      Check(Find<ComboBox>(form, "apiProxyProtocol").SelectedIndex == 1 && !Find<CheckBox>(form, "apiProxyUseDefaultCredentials").Enabled,
        "saved SOCKS5 endpoint restores the proxy type and supported authentication controls");
    }
  }

  private static void FillCatalog(SettingsForm form, TranslationOptions options, CliModelCatalog catalog)
  {
    var type = typeof(SettingsForm);
    type.GetField("translationDraft", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, options.Copy());
    type.GetMethod("SetTranslationControls", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { options });
    type.GetField("hasModelCatalog", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, true);
    type.GetField("updatingTranslation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, true);
    type.GetMethod("FillModels", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { catalog, options });
    type.GetField("updatingTranslation", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, false);
  }
  private static async Task WaitFor(Func<bool> condition)
  {
    for (var i = 0; i < 400; i++) { if (condition()) return; await Task.Delay(100); }
    throw new TimeoutException("Settings did not complete model discovery.");
  }
  private static T Find<T>(Control root, string name) where T : Control => (T)root.Controls.Find(name, true).Single();
  private static void Check(bool condition, string label) { if (!condition) throw new Exception("FAIL " + label); passed++; Console.WriteLine("PASS " + label); }
}
