using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AnotherMarkdown.Entities;
using AnotherMarkdown.Translation;

namespace AnotherMarkdown.Forms
{
  public partial class SettingsForm
  {
    public TranslationOptions TranslationOptions { get; private set; }
    private TranslationOptions translationDraft;
    private ComboBox translationCli, translationModel, translationProfile, translationOutput;
    private TextBox translationExecutable, translationArguments, translationManualModel;
    private NumericUpDown translationTimeout;
    private CheckBox translationShowButtons, translationAdvanced, translationCustomArguments, translationUseManualModel;
    private Button translationModelsRefresh, translationCliRefresh;
    private Label translationModelStatus;
    private CancellationTokenSource modelCancellation;
    private int modelGeneration;
    private int cliDiscoveryGeneration;
    private bool updatingTranslation;

    private sealed class ModelChoice
    {
      public string Id, Title;
      public bool Default;
      public override string ToString() => Title;
    }

    private void InitializeTranslationSettings(Settings settings)
    {
      TranslationOptions = settings.Translation.Copy();
      translationDraft = TranslationOptions.Copy();
      translationPage.AutoScroll = true;
      var layout = new TableLayoutPanel { Name = "translationLayout", Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 9, Padding = new Padding(8) };
      layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
      layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
      for (var row = 0; row < 9; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      layout.RowStyles[5] = new RowStyle(SizeType.Absolute, 0);
      translationPage.Controls.Add(layout);

      translationCli = new ComboBox { Name = "translationCli", AccessibleName = "CLI", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, DropDownWidth = 760, TabIndex = 0 };
      translationCliRefresh = new Button { Text = "Найти CLI", AutoSize = true, Dock = DockStyle.Top, TabIndex = 1 };
      translationExecutable = MakeSettingsTextBox("translationExecutable", "Путь к CLI", 2);
      translationExecutable.ReadOnly = true;
      var browse = new Button { Name = "translationBrowse", AccessibleName = "Выбрать исполняемый файл CLI", Text = "Выбрать…", Dock = DockStyle.Top, Height = 28, TabIndex = 3 };
      translationModel = new ComboBox { Name = "translationModel", AccessibleName = "Модель", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, DropDownWidth = 650, TabIndex = 4 };
      translationModelsRefresh = new Button { Text = "Обновить", AutoSize = true, Dock = DockStyle.Top, TabIndex = 5 };
      AddTranslationRow(layout, 0, "CLI", translationCli, translationCliRefresh);
      AddTranslationRow(layout, 1, "Путь", translationExecutable, browse);
      AddTranslationRow(layout, 2, "Модель", translationModel, translationModelsRefresh);
      translationModelStatus = new Label { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 10), Text = "Ищем установленные CLI…" };
      layout.Controls.Add(translationModelStatus, 0, 3); layout.SetColumnSpan(translationModelStatus, 3);
      translationAdvanced = new CheckBox { Text = "Дополнительные параметры", AutoSize = true, TabIndex = 6, Margin = new Padding(3, 8, 3, 8) };
      layout.Controls.Add(translationAdvanced, 0, 4); layout.SetColumnSpan(translationAdvanced, 3);

      var advanced = new GroupBox { Text = "Параметры выбранного CLI", Dock = DockStyle.Fill, Visible = false, Padding = new Padding(8) };
      layout.Controls.Add(advanced, 0, 5); layout.SetColumnSpan(advanced, 3);
      var advancedLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6 };
      advancedLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
      advancedLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      foreach (var height in new[] { 32, 32, 28, 115, 32, 32 }) advancedLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
      advanced.Controls.Add(advancedLayout);
      translationProfile = new ComboBox { Name = "translationProfile", AccessibleName = "Тип CLI", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, TabIndex = 0 };
      translationProfile.Items.AddRange(CliProfiles.All);
      translationTimeout = new NumericUpDown { Name = "translationTimeout", AccessibleName = "Тайм-аут, сек.", Minimum = 10, Maximum = 3600, Width = 120, TabIndex = 1 };
      translationCustomArguments = new CheckBox { Text = "Изменить параметры запуска", AutoSize = true, TabIndex = 2 };
      translationArguments = new TextBox { Name = "translationArguments", AccessibleName = "Аргументы запуска", Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, ReadOnly = true, TabIndex = 3 };
      translationOutput = new ComboBox { Name = "translationOutput", AccessibleName = "Формат ответа CLI", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, TabIndex = 4 };
      translationOutput.Items.AddRange(new object[] { "text", "json-result", "json-response", "opencode-json", "agy-json", "kimi-json" });
      translationUseManualModel = new CheckBox { Text = "Другая модель", AutoSize = true, TabIndex = 5 };
      translationManualModel = MakeSettingsTextBox("translationManualModel", "Идентификатор другой модели", 6);
      advancedLayout.Controls.Add(MakeSettingsLabel("Тип CLI"), 0, 0); advancedLayout.Controls.Add(translationProfile, 1, 0);
      advancedLayout.Controls.Add(MakeSettingsLabel("Тайм-аут, сек."), 0, 1); advancedLayout.Controls.Add(translationTimeout, 1, 1);
      advancedLayout.Controls.Add(translationCustomArguments, 0, 2); advancedLayout.SetColumnSpan(translationCustomArguments, 2);
      advancedLayout.Controls.Add(translationArguments, 0, 3); advancedLayout.SetColumnSpan(translationArguments, 2);
      advancedLayout.Controls.Add(MakeSettingsLabel("Формат ответа"), 0, 4); advancedLayout.Controls.Add(translationOutput, 1, 4);
      advancedLayout.Controls.Add(translationUseManualModel, 0, 5); advancedLayout.Controls.Add(translationManualModel, 1, 5);

      translationShowButtons = new CheckBox { Name = "translationShowButtons", Text = "Показывать кнопки перевода в панели", AutoSize = true, TabIndex = 7, Margin = new Padding(3, 12, 3, 8) };
      layout.Controls.Add(translationShowButtons, 0, 6); layout.SetColumnSpan(translationShowButtons, 3);
      var defaults = new Button { Name = "translationDefaults", Text = "Настройки по умолчанию", AutoSize = true, TabIndex = 8, Margin = new Padding(3, 8, 3, 10) };
      layout.Controls.Remove(translationAdvanced);
      var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
      defaults.Margin = new Padding(3, 4, 18, 4); defaults.TabIndex = 0;
      translationAdvanced.Margin = new Padding(3, 8, 3, 4); translationAdvanced.TabIndex = 1;
      actions.Controls.Add(defaults); actions.Controls.Add(translationAdvanced);
      layout.Controls.Add(actions, 0, 4); layout.SetColumnSpan(actions, 3);
      var description = new Label {
        Text = "Выберите CLI и модель. Параметры запуска подставляются автоматически. Перевод отображается в предпросмотре; исходный файл сохраняет свой текст.",
        AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 8, 3, 8)
      };
      layout.Controls.Add(description, 0, 8); layout.SetColumnSpan(description, 3);
      layout.SizeChanged += (_, __) => {
        var width = Math.Max(200, layout.ClientSize.Width - 24);
        description.MaximumSize = translationModelStatus.MaximumSize = new Size(width, 0);
      };
      translationAdvanced.CheckedChanged += (_, __) => { advanced.Visible = translationAdvanced.Checked; layout.RowStyles[5].Height = translationAdvanced.Checked ? 310 : 0; };
      translationCustomArguments.CheckedChanged += (_, __) => {
        translationArguments.ReadOnly = !translationCustomArguments.Checked;
        translationOutput.Enabled = translationCustomArguments.Checked;
        translationUseManualModel.Enabled = CliProfiles.Get(translationDraft.ProviderId).SupportsModelOverride || translationCustomArguments.Checked;
        if (!translationUseManualModel.Enabled) translationUseManualModel.Checked = false;
        if (!updatingTranslation && !translationCustomArguments.Checked) {
          translationOutput.SelectedItem = CliProfiles.Get(translationDraft.ProviderId).OutputFormat;
          UpdateArgumentPreview();
        }
      };
      translationUseManualModel.CheckedChanged += (_, __) => { translationManualModel.Enabled = translationUseManualModel.Checked; translationModel.Enabled = !translationUseManualModel.Checked && modelCancellation == null; UpdateArgumentPreview(); };
      translationManualModel.TextChanged += (_, __) => UpdateArgumentPreview();
      translationModel.SelectedIndexChanged += (_, __) => UpdateArgumentPreview();
      translationProfile.SelectedIndexChanged += async (_, __) => {
        if (updatingTranslation || translationCli.SelectedItem == null) return;
        var installation = (CliInstallation)translationCli.SelectedItem;
        installation.ProviderId = ((CliProfile)translationProfile.SelectedItem).Id;
        await ApplyCliAsync(installation, true);
      };
      translationCli.SelectedIndexChanged += async (_, __) => {
        if (!updatingTranslation && translationCli.SelectedItem is CliInstallation installation) await ApplyCliAsync(installation, true);
      };
      translationCliRefresh.Click += async (_, __) => await FindInstalledCliAsync();
      translationModelsRefresh.Click += async (_, __) => await LoadModelsAsync(ReadTranslationDraft());
      defaults.Click += async (_, __) => {
        if (translationCli.SelectedItem is CliInstallation installation) await ApplyCliAsync(installation, true);
      };
      browse.Click += async (_, __) => await BrowseCliAsync();
      Shown += async (_, __) => await FindInstalledCliAsync();
      FormClosing += (_, __) => CancelModelDiscovery();
      SetTranslationControls(translationDraft);
    }

    private static void AddTranslationRow(TableLayoutPanel layout, int row, string caption, Control value, Control extra)
    {
      layout.Controls.Add(MakeSettingsLabel(caption), 0, row);
      value.Margin = new Padding(3, 5, 3, 8); layout.Controls.Add(value, 1, row); layout.Controls.Add(extra, 2, row);
    }

    public void SelectTranslationTab() => settingsTabs.SelectedTab = translationPage;

    private async Task FindInstalledCliAsync()
    {
      var preferred = ReadTranslationDraft();
      translationDraft = preferred.Copy();
      var generation = ++cliDiscoveryGeneration;
      translationCliRefresh.Enabled = false;
      try {
        var installations = await Task.Run(() => CliProfiles.DiscoverInstalled());
        if (IsDisposed || Disposing || generation != cliDiscoveryGeneration) return;
        string path;
        try { path = CliTranslator.ResolveExecutable(preferred.Executable); }
        catch (Exception) { path = preferred.Executable; }
        var selected = installations.FirstOrDefault(i => string.Equals(i.Executable, path, StringComparison.OrdinalIgnoreCase));
        if (selected != null) selected.ProviderId = preferred.ProviderId;
        var legacyScript = Path.HasExtension(path) && !CliProfiles.IsExePath(path);
        if (legacyScript) {
          selected = installations.FirstOrDefault(i => i.ProviderId == preferred.ProviderId) ?? installations.FirstOrDefault();
          if (selected != null) translationDraft = CliProfiles.Defaults(selected.ProviderId, selected.Executable);
          else { translationDraft.Executable = ""; translationExecutable.Text = ""; }
        }
        if (selected == null && path == "codex" && installations.Count > 0) {
          selected = installations[0];
          preferred = CliProfiles.Defaults(selected.ProviderId, selected.Executable);
          translationDraft = preferred.Copy();
        }
        if (selected == null && !legacyScript && CliProfiles.IsExePath(path) && File.Exists(path)) {
          selected = new CliInstallation { Executable = path, ProviderId = preferred.ProviderId };
          installations.Insert(0, selected);
        }
        if (selected == null && installations.Count > 0) {
          selected = installations[0];
          translationDraft = CliProfiles.Defaults(selected.ProviderId, selected.Executable);
        }
        updatingTranslation = true;
        translationCli.Items.Clear(); translationCli.Items.AddRange(installations.ToArray());
        translationCli.SelectedItem = selected ?? installations.FirstOrDefault();
        updatingTranslation = false;
        if (translationCli.SelectedItem is CliInstallation installation) await ApplyCliAsync(installation, false);
        else translationModelStatus.Text = "CLI не найден. Нажмите «Выбрать…», чтобы указать уже установленную программу.";
      }
      catch (Exception error) { if (!IsDisposed) translationModelStatus.Text = error.Message; }
      finally { if (!IsDisposed) translationCliRefresh.Enabled = true; }
    }

    private async Task ApplyCliAsync(CliInstallation installation, bool defaults)
    {
      cliDiscoveryGeneration++;
      CancelModelDiscovery();
      var options = defaults ? CliProfiles.Defaults(installation.ProviderId, installation.Executable) : translationDraft.Copy();
      options.ProviderId = installation.ProviderId; options.Executable = installation.Executable;
      translationDraft = options;
      SetTranslationControls(options);
      await LoadModelsAsync(options.Copy());
    }

    private void SetTranslationControls(TranslationOptions options)
    {
      updatingTranslation = true;
      translationExecutable.Text = options.Executable;
      translationProfile.SelectedItem = CliProfiles.Get(options.ProviderId);
      translationTimeout.Value = Math.Max(10, Math.Min(3600, options.TimeoutSeconds));
      translationShowButtons.Checked = options.ShowButtons;
      translationCustomArguments.Checked = options.UseCustomArguments;
      translationArguments.ReadOnly = !options.UseCustomArguments;
      translationOutput.Enabled = options.UseCustomArguments;
      translationArguments.Text = CliProfiles.ArgumentsFor(options);
      translationOutput.SelectedItem = options.OutputFormat;
      if (translationOutput.SelectedIndex < 0) translationOutput.SelectedIndex = 0;
      translationManualModel.Text = options.Model;
      translationUseManualModel.Checked = false;
      translationUseManualModel.Enabled = CliProfiles.Get(options.ProviderId).SupportsModelOverride || options.UseCustomArguments;
      translationManualModel.Enabled = false;
      FillModels(new CliModelCatalog(), options);
      updatingTranslation = false;
    }

    private async Task LoadModelsAsync(TranslationOptions preference)
    {
      CancelModelDiscovery();
      var generation = modelGeneration;
      var cancellation = new CancellationTokenSource(); modelCancellation = cancellation;
      translationModelsRefresh.Enabled = translationModel.Enabled = btnSave.Enabled = false;
      translationModelStatus.Text = "Запрашиваем модели у " + CliProfiles.Get(preference.ProviderId).Name + "…";
      try {
        var catalog = await new CliModelDiscovery().LoadAsync(preference.ProviderId, preference.Executable, cancellation.Token);
        if (cancellation.IsCancellationRequested || generation != modelGeneration || IsDisposed || Disposing) return;
        updatingTranslation = true; FillModels(catalog, preference); updatingTranslation = false;
        translationModelStatus.Text = string.IsNullOrWhiteSpace(catalog.Note) ? "Моделей получено: " + catalog.Models.Count : catalog.Note;
        UpdateArgumentPreview();
      }
      catch (OperationCanceledException) { }
      catch (Exception error) {
        if (!IsDisposed && generation == modelGeneration && !cancellation.IsCancellationRequested)
          translationModelStatus.Text = "Список моделей недоступен: " + error.Message + " Можно использовать модель по умолчанию CLI.";
      }
      finally {
        if (ReferenceEquals(modelCancellation, cancellation)) {
          modelCancellation = null;
          if (!IsDisposed) { translationModelsRefresh.Enabled = btnSave.Enabled = true; translationModel.Enabled = !translationUseManualModel.Checked; }
        }
        cancellation.Dispose();
      }
    }

    private void FillModels(CliModelCatalog catalog, TranslationOptions preference)
    {
      translationModel.Items.Clear();
      var defaultId = catalog.DefaultModelId ?? (preference.ProviderId == "ollama" ? catalog.Models.FirstOrDefault()?.Id : "");
      var fallback = new ModelChoice { Default = true, Id = defaultId ?? "", Title = preference.ProviderId == "ollama" ? "Первая установленная модель" : "Модель по умолчанию в CLI" };
      translationModel.Items.Add(fallback);
      foreach (var model in catalog.Models) translationModel.Items.Add(new ModelChoice { Id = model.Id, Title = model.ToString() });
      var desired = !preference.UseDefaultModel ? translationModel.Items.Cast<ModelChoice>().FirstOrDefault(m => !m.Default && m.Id == preference.Model) : fallback;
      if (desired == null && !string.IsNullOrWhiteSpace(preference.Model)) {
        desired = new ModelChoice { Id = preference.Model, Title = "Сохранённая модель · " + preference.Model }; translationModel.Items.Add(desired);
      }
      translationModel.SelectedItem = desired ?? fallback;
    }

    private void UpdateArgumentPreview()
    {
      if (updatingTranslation || translationArguments == null || translationCustomArguments.Checked) return;
      translationArguments.Text = CliProfiles.ArgumentsFor(ReadTranslationDraft());
    }

    private TranslationOptions ReadTranslationDraft()
    {
      var options = translationDraft.Copy();
      options.Executable = translationExecutable.Text.Trim();
      options.TimeoutSeconds = (int)translationTimeout.Value; options.ShowButtons = translationShowButtons.Checked;
      options.UseCustomArguments = translationCustomArguments.Checked;
      options.Arguments = translationArguments.Text.Replace("\r", " ").Replace("\n", " ").Trim();
      options.OutputFormat = (string)translationOutput.SelectedItem ?? "text";
      var choice = translationModel.SelectedItem as ModelChoice;
      options.UseDefaultModel = !translationUseManualModel.Checked && (choice?.Default ?? true);
      options.Model = translationUseManualModel.Checked ? translationManualModel.Text.Trim() : choice?.Id ?? "";
      return options;
    }

    private async Task BrowseCliAsync()
    {
      cliDiscoveryGeneration++;
      using (var dialog = CreateCliFileDialog(translationExecutable.Text)) {
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var installation = new CliInstallation { Executable = dialog.FileName, ProviderId = CliProfiles.Identify(dialog.FileName) };
        updatingTranslation = true;
        translationCli.Items.Add(installation); translationCli.SelectedItem = installation;
        updatingTranslation = false;
        await ApplyCliAsync(installation, true);
      }
    }

    internal static OpenFileDialog CreateCliFileDialog(string path)
    {
      try { path = CliTranslator.ResolveExecutable(path); } catch (Exception) { }
      var directory = Path.IsPathRooted(path) ? Path.GetDirectoryName(path) : Environment.SystemDirectory;
      if (!Directory.Exists(directory)) directory = Environment.SystemDirectory;
      return new OpenFileDialog {
        Title = "Выберите CLI (.exe)", Filter = "CLI (*.exe)|*.exe",
        InitialDirectory = directory, FileName = File.Exists(path) ? path : "", RestoreDirectory = true, CheckFileExists = true
      };
    }

    private bool ValidateTranslationSettings()
    {
      try {
        var options = ReadTranslationDraft();
        if (options.ShowButtons) { options.Validate(); CliTranslator.ResolveExecutable(options.Executable); }
        TranslationOptions = options;
        return true;
      }
      catch (Exception error) when (error is ArgumentException || error is FileNotFoundException) {
        SelectTranslationTab(); translationModelStatus.Text = error.Message; return false;
      }
    }

    private void CancelModelDiscovery()
    {
      modelGeneration++; var cancellation = modelCancellation; modelCancellation = null; cancellation?.Cancel();
    }
  }
}
