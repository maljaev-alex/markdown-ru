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
      ModelSortingSettings();
      ApiSettings();
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
    var connection = new ApiConnection { Name = "Example API", Endpoint = "https://example.test/v1", Model = "example-model", ApiKey = "fake-ui-key" };
    settings.Translation.ConnectionMode = "api"; settings.Translation.ApiConnections.Add(connection); settings.Translation.SelectedApiConnectionId = connection.Id;
    using (var form = new BackgroundSettings(settings)) {
      form.SelectTranslationTab();
      form.Shown += (_, __) => {
        try {
          var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
          using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(directory, "settings-api-basic.png")); }
          Check(Find<TextBox>(form, "apiKey").UseSystemPasswordChar && Find<ComboBox>(form, "translationConnectionMode").SelectedIndex == 1, "live API form restores selected mode and masks the key");
          Find<CheckBox>(form, "apiAdvanced").Checked = true;
          using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height)); bitmap.Save(Path.Combine(directory, "settings-api-advanced.png")); }
          var profile = ReadDraft(form).ActiveApiConnection;
          var path = Path.Combine(directory, "api-ui-roundtrip.json");
          ApiConnectionStore.Save(path, new[] { profile });
          var loaded = ApiConnectionStore.Load(path).Single();
          Check(loaded.ApiKey == "fake-ui-key" && loaded.Model == "example-model" && !File.ReadAllText(path).Contains("fake-ui-key"), "live API form values persist encrypted and restore correctly");
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
      Check(models.Items.Cast<object>().Skip(1).Select(m => m.ToString()).SequenceEqual(new[] { "ALPHA (a-first)", "alpha (z-last)", "Bravo (middle)", "empty-name", "zeta (aaa) [\u043f\u043e \u0443\u043c\u043e\u043b\u0447\u0430\u043d\u0438\u044e]" }), "CLI models sort by display name ignoring case with deterministic IDs");
      options.UseDefaultModel = false; options.Model = "middle";
      FillCatalog(form, options, catalog);
      Check(ReadDraft(form).Model == "middle" && models.Text == "Bravo (middle)", "CLI sorting restores an explicit saved model by ID");
      models.SelectedItem = models.Items.Cast<object>().Single(m => m.ToString() == "alpha (z-last)");
      var selected = ReadDraft(form);
      catalog.Models.Reverse();
      FillCatalog(form, selected, catalog);
      Check(ReadDraft(form).Model == "z-last" && models.Text == "alpha (z-last)", "CLI refresh order does not change the selected model");
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
      presets.SelectedItem = presets.Items.Cast<object>().Single(p => p.ToString() == "OpenAI");
      Check(limit.Value == 0 && ReadDraft(form).ActiveApiConnection.MaxOutputTokens == 0, "OpenAI preset restores the service default rather than a fixed reasoning budget");
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
    using (var form = new SettingsForm(settings)) {
      Check(ReadDraft(form).UseApi && Find<TextBox>(form, "apiKey").Text == "fake-saved-key", "API mode and stored connection restore independently of CLI discovery");
      Find<TextBox>(form, "apiEndpoint").Text = "https://example.test/custom/v1";
      typeof(SettingsForm).GetMethod("btnSave_Click", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { null, EventArgs.Empty });
      Check(form.DialogResult == DialogResult.OK && form.TranslationOptions.ActiveApiConnection.Endpoint == "https://example.test/custom/v1" && settings.Translation.ActiveApiConnection.Endpoint == "https://example.test/v1", "Save captures API values without requiring a CLI executable or mutating original settings");
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
