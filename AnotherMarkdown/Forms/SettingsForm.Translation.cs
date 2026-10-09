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
    private List<CliConnectionSettings> cliDrafts;
    private bool displayedApiMode;
    private ComboBox translationCli, translationModel, translationProfile, translationOutput, translationEffort;
    private TextBox translationExecutable, translationArguments, translationManualModel;
    private NumericUpDown translationTimeout, translationParallelRequests, translationMinimumChunk;
    private CheckBox translationShowButtons, translationAdvanced, translationCustomArguments, translationUseManualModel;
    private Button translationModelsRefresh, translationCliRefresh;
    private Label translationModelStatus;
    private string translationModelSelectionNote;
    private bool hasModelCatalog;
    private CancellationTokenSource modelCancellation;
    private CancellationTokenSource cliDiscoveryCancellation;
    private Task modelDiscoveryTask;
    private string modelDiscoveryKey;
    private bool settingsShown, cliDiscoveryStarted, discoveryQueued;
    private int modelGeneration;
    private int cliDiscoveryGeneration;
    private bool updatingTranslation;

    private sealed class ModelChoice
    {
      public string Id, Title;
      public bool Default;
      public List<string> ModelIds = new List<string>();
      public List<CliReasoningEffort> Efforts = new List<CliReasoningEffort>();
      public string DefaultEffort;
      public override string ToString() => Title;
    }

    private sealed class EffortChoice
    {
      public string Id, Title, ModelId;
      public override string ToString() => Title;
    }

    private void InitializeTranslationSettings(Settings settings)
    {
      TranslationOptions = settings.Translation.Copy();
      translationDraft = TranslationOptions.Copy();
      cliDrafts = translationDraft.CliConnections.Select(c => c.Copy()).ToList();
      if (translationDraft.UseApi) FindCliDraft(translationDraft.ProviderId, translationDraft.Executable)?.ApplyTo(translationDraft);
      translationPage.AutoScroll = true;
      var layout = HoldInitialLayout(new SettingsLayoutPanel { Name = "translationLayout", Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 10, Padding = new Padding(8) });
      layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
      layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
      for (var row = 0; row < 10; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      layout.RowStyles[6] = new RowStyle(SizeType.Absolute, 0);
      translationPage.Controls.Add(layout);

      translationCli = new ComboBox { Name = "translationCli", AccessibleName = "CLI", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, DropDownWidth = 760, FormattingEnabled = true, TabIndex = 0 };
      translationCli.Format += (_, value) => {
        if (value.ListItem is CliInstallation item && translationCli.Items.Cast<CliInstallation>().Count(i => i.ToString() == item.ToString()) > 1)
          value.Value = item + " — " + Path.GetDirectoryName(item.Executable);
      };
      translationCliRefresh = new Button { Name = "translationCliRefresh", Text = "Найти CLI", AutoSize = true, Dock = DockStyle.Top, TabIndex = 1 };
      translationExecutable = MakeSettingsTextBox("translationExecutable", "Путь к CLI", 2);
      translationExecutable.ReadOnly = true;
      var browse = new Button { Name = "translationBrowse", AccessibleName = "Выбрать исполняемый файл CLI", Text = "Выбрать…", Dock = DockStyle.Top, Height = 28, TabIndex = 3 };
      translationModel = new ComboBox { Name = "translationModel", AccessibleName = "Модель", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, DropDownWidth = 650, TabIndex = 4 };
      translationModelsRefresh = new Button { Name = "translationModelsRefresh", Text = "Обновить", AutoSize = true, Dock = DockStyle.Top, TabIndex = 5 };
      AddTranslationRow(layout, 0, "CLI", translationCli, translationCliRefresh);
      AddTranslationRow(layout, 1, "Путь", translationExecutable, browse);
      translationEffort = new ComboBox { Name = "translationEffort", AccessibleName = "Усилие рассуждения (effort)", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, DropDownWidth = 280, TabIndex = 5 };
      AddModelEffortRow(layout, 2, "translationModelRow", translationModel, translationEffort, translationModelsRefresh);
      translationModelStatus = CreateSettingsStatus("translationModelStatus", "Сохранённые настройки. Список CLI и моделей обновляется в фоне.");
      layout.Controls.Add(translationModelStatus, 0, 4); layout.SetColumnSpan(translationModelStatus, 3);
      translationAdvanced = new CheckBox { Text = "Дополнительные параметры", AutoSize = true, TabIndex = 6, Margin = new Padding(3, 8, 3, 8) };
      layout.Controls.Add(translationAdvanced, 0, 5); layout.SetColumnSpan(translationAdvanced, 3);

      var advanced = HoldInitialLayout(new GroupBox { Text = "Параметры выбранного CLI", Dock = DockStyle.Fill, Visible = false, Padding = new Padding(8) });
      layout.Controls.Add(advanced, 0, 6); layout.SetColumnSpan(advanced, 3);
      var advancedLayout = HoldInitialLayout(new SettingsLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 6 });
      advancedLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
      advancedLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      foreach (var height in new[] { 32, 32, 28, 115, 32, 32 }) advancedLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
      advanced.Controls.Add(advancedLayout);
      translationProfile = new ComboBox { Name = "translationProfile", AccessibleName = "Тип CLI", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, TabIndex = 0 };
      translationProfile.Items.AddRange(CliProfiles.All);
      translationTimeout = new NumericUpDown { Name = "translationTimeout", AccessibleName = "Тайм-аут на запрос, сек.", Minimum = 10, Maximum = 3600, Width = 120, TabIndex = 1 };
      translationCustomArguments = new CheckBox { Text = "Изменить параметры запуска", AutoSize = true, TabIndex = 2 };
      translationArguments = new TextBox { Name = "translationArguments", AccessibleName = "Аргументы запуска", Multiline = true, AcceptsReturn = true, MaxLength = TranslationOptions.MaximumStoredArgumentCharacters, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, ReadOnly = true, TabIndex = 3 };
      translationOutput = new ComboBox { Name = "translationOutput", AccessibleName = "Формат ответа CLI", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, TabIndex = 4 };
      translationOutput.Items.AddRange(new object[] { "text", "json-result", "json-response", "opencode-json", "agy-json", "kimi-json" });
      translationUseManualModel = new CheckBox { Text = "Другая модель", AutoSize = true, TabIndex = 5 };
      translationManualModel = MakeSettingsTextBox("translationManualModel", "Идентификатор другой модели", 6);
      advancedLayout.Controls.Add(MakeSettingsLabel("Тип CLI"), 0, 0); advancedLayout.Controls.Add(translationProfile, 1, 0);
      advancedLayout.Controls.Add(MakeSettingsLabel("Тайм-аут на запрос, сек."), 0, 1); advancedLayout.Controls.Add(translationTimeout, 1, 1);
      advancedLayout.Controls.Add(translationCustomArguments, 0, 2); advancedLayout.SetColumnSpan(translationCustomArguments, 2);
      advancedLayout.Controls.Add(translationArguments, 0, 3); advancedLayout.SetColumnSpan(translationArguments, 2);
      advancedLayout.Controls.Add(MakeSettingsLabel("Формат ответа"), 0, 4); advancedLayout.Controls.Add(translationOutput, 1, 4);
      advancedLayout.Controls.Add(translationUseManualModel, 0, 5); advancedLayout.Controls.Add(translationManualModel, 1, 5);

      translationShowButtons = new CheckBox { Name = "translationShowButtons", Text = "Показывать кнопки перевода в панели", AutoSize = true, TabIndex = 7, Margin = new Padding(3, 12, 3, 8) };
      layout.Controls.Add(translationShowButtons, 0, 7); layout.SetColumnSpan(translationShowButtons, 3);
      var defaults = new Button { Name = "translationDefaults", Text = "Настройки по умолчанию", AutoSize = true, TabIndex = 8, Margin = new Padding(3, 8, 3, 10) };
      layout.Controls.Remove(translationAdvanced);
      var actions = HoldInitialLayout(new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight });
      defaults.Margin = new Padding(3, 4, 18, 4); defaults.TabIndex = 0;
      translationAdvanced.Margin = new Padding(3, 8, 3, 4); translationAdvanced.TabIndex = 1;
      actions.Controls.Add(defaults); actions.Controls.Add(translationAdvanced);
      layout.Controls.Add(actions, 0, 5); layout.SetColumnSpan(actions, 3);
      var description = new Label {
        Text = "Выберите CLI и модель. Параметры запуска подставляются автоматически. Перевод отображается в предпросмотре; исходный файл сохраняет свой текст.",
        AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 8, 3, 8)
      };
      layout.Controls.Add(description, 0, 9); layout.SetColumnSpan(description, 3);
      layout.SizeChanged += (_, __) => {
        var width = Math.Max(200, layout.ClientSize.Width - 24);
        description.MaximumSize = new Size(width, 0);
      };
      translationAdvanced.CheckedChanged += (_, __) => { advanced.Visible = translationAdvanced.Checked; layout.RowStyles[6].Height = translationAdvanced.Checked ? 310 : 0; };
      translationCustomArguments.CheckedChanged += (_, __) => {
        translationArguments.ReadOnly = !translationCustomArguments.Checked;
        translationOutput.Enabled = translationCustomArguments.Checked;
        UpdateEffortState();
        translationUseManualModel.Enabled = CliProfiles.Get(translationDraft.ProviderId).SupportsModelOverride || translationCustomArguments.Checked;
        if (!translationUseManualModel.Enabled) translationUseManualModel.Checked = false;
        if (!updatingTranslation && !translationCustomArguments.Checked) {
          translationOutput.SelectedItem = CliProfiles.Get(translationDraft.ProviderId).OutputFormat;
          UpdateArgumentPreview();
        }
      };
      translationUseManualModel.CheckedChanged += (_, __) => { translationManualModel.Enabled = translationUseManualModel.Checked; translationModel.Enabled = !translationUseManualModel.Checked; if (!updatingTranslation) FillEfforts(""); UpdateArgumentPreview(); };
      translationManualModel.TextChanged += (_, __) => UpdateArgumentPreview();
      translationModel.SelectedIndexChanged += (_, __) => { if (!updatingTranslation) FillEfforts(""); UpdateArgumentPreview(); };
      translationEffort.SelectedIndexChanged += (_, __) => UpdateArgumentPreview();
      translationProfile.SelectedIndexChanged += async (_, __) => {
        if (updatingTranslation || translationCli.SelectedItem == null) return;
        var installation = new CliInstallation { Executable = translationExecutable.Text, ProviderId = ((CliProfile)translationProfile.SelectedItem).Id };
        await SelectCliAsync(installation);
      };
      translationCli.SelectedIndexChanged += async (_, __) => {
        if (!updatingTranslation && translationCli.SelectedItem is CliInstallation installation) await ApplyCliAsync(installation, false);
      };
      translationCliRefresh.Click += async (_, __) => await FindInstalledCliAsync(true);
      translationModelsRefresh.Click += async (_, __) => await LoadModelsForSettingsAsync(ReadTranslationDraft(), true);
      defaults.Click += async (_, __) => {
        if (translationCli.SelectedItem is CliInstallation installation) await ApplyCliAsync(installation, true);
      };
      browse.Click += async (_, __) => await BrowseCliAsync();
      Shown += (_, __) => { settingsShown = true; QueueCliDiscovery(); };
      settingsTabs.SelectedIndexChanged += (_, __) => QueueCliDiscovery();
      FormClosing += (_, __) => { CancelCliDiscovery(); CancelModelDiscovery(); };
      SetTranslationControls(translationDraft);
      List<CliInstallation> cachedInstallations; bool fresh;
      settingsDiscovery.TryInstallations(out cachedInstallations, out fresh);
      FillInstalledCli(cachedInstallations ?? new List<CliInstallation>(), new CliInstallation { ProviderId = translationDraft.ProviderId, Executable = translationDraft.Executable });
      InitializeApiSettings(layout);
      CliModelCatalog cachedModels;
      if (settingsDiscovery.TryModels(translationDraft.ProviderId, translationDraft.Executable, out cachedModels, out fresh)) ApplyModelCatalog(cachedModels, translationDraft);
    }

    private static void AddTranslationRow(TableLayoutPanel layout, int row, string caption, Control value, Control extra)
    {
      layout.Controls.Add(MakeSettingsLabel(caption), 0, row);
      value.Margin = new Padding(3, 5, 3, 8); layout.Controls.Add(value, 1, row); layout.Controls.Add(extra, 2, row);
    }

    private void AddModelEffortRow(TableLayoutPanel layout, int row, string name, Control model, Control effort, Button refresh)
    {
      var fields = HoldInitialLayout(new SettingsLayoutPanel { Name = name, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, ColumnCount = 4, Margin = new Padding(0) });
      fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
      fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
      fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, layout.ColumnStyles[2].Width));
      model.Margin = effort.Margin = new Padding(3, 5, 3, 5);
      model.TabIndex = 0; effort.TabIndex = 1; refresh.TabIndex = 2;
      refresh.Margin = new Padding(3); refresh.Dock = DockStyle.Top;
      var label = MakeSettingsLabel("Effort"); label.Margin = new Padding(8, 8, 3, 3);
      fields.Controls.Add(model, 0, 0); fields.Controls.Add(label, 1, 0); fields.Controls.Add(effort, 2, 0); fields.Controls.Add(refresh, 3, 0);
      layout.Controls.Add(MakeSettingsLabel("Модель"), 0, row); layout.Controls.Add(fields, 1, row); layout.SetColumnSpan(fields, 2);
      var changing = false;
      bool? wrapped = null; var previousEffortWidth = 0;
      Action reflow = () => {
        if (changing || fields.ClientSize.Width <= 0) return;
        var effortWidth = Math.Max(150, TextRenderer.MeasureText("Extra High (xhigh)", fields.Font).Width + 36);
        var wrap = fields.ClientSize.Width < effortWidth + label.GetPreferredSize(Size.Empty).Width + fields.ColumnStyles[3].Width + 160;
        if (wrapped == wrap && previousEffortWidth == effortWidth) return;
        changing = true; fields.SuspendLayout();
        try {
          wrapped = wrap; previousEffortWidth = effortWidth;
          fields.SetColumnSpan(model, wrap ? 3 : 1); fields.SetColumnSpan(effort, wrap ? 3 : 1);
          fields.SetCellPosition(label, new TableLayoutPanelCellPosition(wrap ? 0 : 1, wrap ? 1 : 0));
          fields.SetCellPosition(effort, new TableLayoutPanelCellPosition(wrap ? 1 : 2, wrap ? 1 : 0));
          fields.ColumnStyles[0].SizeType = wrap ? SizeType.AutoSize : SizeType.Percent; fields.ColumnStyles[0].Width = wrap ? 0 : 100;
          fields.ColumnStyles[1].SizeType = wrap ? SizeType.Percent : SizeType.AutoSize; fields.ColumnStyles[1].Width = wrap ? 100 : 0;
          fields.ColumnStyles[2].SizeType = SizeType.Absolute; fields.ColumnStyles[2].Width = wrap ? 0 : effortWidth;
        }
        finally { fields.ResumeLayout(true); changing = false; }
      };
      fields.SizeChanged += (_, __) => reflow(); fields.FontChanged += (_, __) => reflow();
    }

    private Control CreateParallelRequestsSettings()
    {
      var group = HoldInitialLayout(new GroupBox { Name = "translationParallelGroup", Text = "Многопоточность", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Padding = new Padding(8), Margin = new Padding(8, 6, 8, 4) });
      var layout = HoldInitialLayout(new SettingsLayoutPanel { Name = "translationParallelLayout", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, ColumnCount = 5, RowCount = 2, Margin = new Padding(0) });
      for (var i = 0; i < 4; i++) layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
      layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      translationParallelRequests = new NumericUpDown {
        Name = "translationParallelRequests", AccessibleName = "Одновременных запросов", Minimum = 1, Maximum = 8,
        Value = Math.Max(1, Math.Min(8, translationDraft.ParallelRequests)), Width = 64, Margin = new Padding(3, 5, 3, 8)
      };
      var countLabel = MakeSettingsLabel("Запросов одновременно");
      layout.Controls.Add(countLabel, 0, 0); layout.Controls.Add(translationParallelRequests, 1, 0);
      translationMinimumChunk = new NumericUpDown {
        Name = "translationMinimumChunk", AccessibleName = "Минимальный размер части, символов", Minimum = 0, Maximum = 1000000, Increment = 100,
        Value = Math.Max(0, Math.Min(1000000, translationDraft.MinimumChunkCharacters)), ThousandsSeparator = true, Width = 100, Margin = new Padding(3, 5, 3, 8)
      };
      var sizeLabel = MakeSettingsLabel("Мин. размер части, символов"); sizeLabel.Margin = new Padding(18, 8, 3, 3);
      layout.Controls.Add(sizeLabel, 2, 0); layout.Controls.Add(translationMinimumChunk, 3, 0);
      var help = new Label { Name = "translationParallelHelp", Text = "Одновременно переводится не больше указанного числа частей. Их может быть меньше из-за размера или структуры текста. 0 символов — без минимума; таблицы, списки и код не разрываются.", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 0, 3, 2) };
      layout.Controls.Add(help, 0, 1); layout.SetColumnSpan(help, 5);
      var changing = false;
      bool? wrapped = null; var previousHelpWidth = 0;
      Action reflow = () => {
        if (changing || layout.ClientSize.Width <= 0) return;
        var required = countLabel.GetPreferredSize(Size.Empty).Width + translationParallelRequests.Width + sizeLabel.GetPreferredSize(Size.Empty).Width + translationMinimumChunk.Width + 48;
        var wrap = required > layout.ClientSize.Width;
        var helpWidth = Math.Max(100, layout.ClientSize.Width - 12);
        if (wrapped == wrap && previousHelpWidth == helpWidth) return;
        changing = true; layout.SuspendLayout();
        try {
          wrapped = wrap; previousHelpWidth = helpWidth;
          layout.SetCellPosition(sizeLabel, new TableLayoutPanelCellPosition(wrap ? 0 : 2, wrap ? 1 : 0));
          layout.SetCellPosition(translationMinimumChunk, new TableLayoutPanelCellPosition(wrap ? 1 : 3, wrap ? 1 : 0));
          layout.SetCellPosition(help, new TableLayoutPanelCellPosition(0, wrap ? 2 : 1));
          sizeLabel.Margin = new Padding(wrap ? 3 : 18, 8, 3, 3);
          for (var i = 2; i < 4; i++) { layout.ColumnStyles[i].SizeType = wrap ? SizeType.Absolute : SizeType.AutoSize; layout.ColumnStyles[i].Width = 0; }
          help.MaximumSize = new Size(helpWidth, 0);
        }
        finally { layout.ResumeLayout(true); changing = false; }
      };
      layout.SizeChanged += (_, __) => reflow(); layout.FontChanged += (_, __) => reflow();
      group.Controls.Add(layout);
      return group;
    }

    public void SelectTranslationTab() => settingsTabs.SelectedTab = translationPage;

    private void QueueCliDiscovery()
    {
      if (!settingsShown || IsDisposed || Disposing || IsApiMode || settingsTabs.SelectedTab != translationPage || cliDiscoveryStarted || discoveryQueued) return;
      discoveryQueued = true;
      BeginInvoke(new Action(async () => {
        discoveryQueued = false;
        if (IsDisposed || Disposing || IsApiMode || settingsTabs.SelectedTab != translationPage || cliDiscoveryStarted) return;
        cliDiscoveryStarted = true;
        var preference = ReadTranslationDraft();
        var installed = FindInstalledCliAsync();
        // A saved absolute launcher can fetch its catalog independently of PATH scanning.
        var models = Path.IsPathRooted(preference.Executable) ? LoadModelsForSettingsAsync(preference) : Task.CompletedTask;
        await Task.WhenAll(installed, models);
      }));
    }

    private void FillInstalledCli(List<CliInstallation> installations, CliInstallation selected)
    {
      var wasUpdating = updatingTranslation;
      updatingTranslation = true; translationCli.BeginUpdate();
      try {
        // Keep saved paths available even when discovery cannot currently find that installation.
        installations = installations.Select(i => new CliInstallation { ProviderId = i.ProviderId, Executable = i.Executable }).ToList();
        foreach (var saved in cliDrafts) {
          if (!installations.Any(i => CliConnectionSettings.SameIdentity(i.ProviderId, i.Executable, saved.ProviderId, saved.Executable)))
            installations.Add(new CliInstallation { ProviderId = saved.ProviderId, Executable = saved.Executable });
        }
        if (selected != null) {
          var existing = installations.FirstOrDefault(i => CliConnectionSettings.SameIdentity(i.ProviderId, i.Executable, selected.ProviderId, selected.Executable));
          if (existing != null) selected = existing;
          else if (!string.IsNullOrWhiteSpace(selected.Executable)) installations.Insert(0, selected);
        }
        translationCli.Items.Clear(); translationCli.Items.AddRange(installations.ToArray());
        translationCli.SelectedItem = selected;
      }
      finally { translationCli.EndUpdate(); updatingTranslation = wasUpdating; }
    }

    private sealed class CliScanSelection
    {
      public List<CliInstallation> Installations;
      public CliInstallation Selected;
      public bool ResetDefaults, ClearLegacy;
    }

    private static CliScanSelection ResolveCliSelection(List<CliInstallation> installations, TranslationOptions preferred, CancellationToken cancellation)
    {
      cancellation.ThrowIfCancellationRequested();
      string path;
      try { path = CliTranslator.ResolveExecutable(preferred.Executable); }
      catch (Exception) { path = preferred.Executable; }
      if (!Path.IsPathRooted(path) && CliProfiles.IsLauncherPath(path) && File.Exists(path)) path = Path.GetFullPath(path);
      cancellation.ThrowIfCancellationRequested();
      var selected = installations.FirstOrDefault(i => string.Equals(i.Executable, path, StringComparison.OrdinalIgnoreCase));
      if (selected != null) selected.ProviderId = preferred.ProviderId;
      var legacyScript = Path.HasExtension(path) && !CliProfiles.IsLauncherPath(path);
      // A saved absolute path is an intentional installation choice, even if it is temporarily unavailable.
      if (selected == null && !legacyScript && Path.IsPathRooted(path))
        selected = new CliInstallation { Executable = path, ProviderId = preferred.ProviderId };
      if (selected == null && preferred.ProviderId != "custom") selected = installations.FirstOrDefault(i => i.ProviderId == preferred.ProviderId);
      if (legacyScript) selected = installations.FirstOrDefault(i => i.ProviderId == preferred.ProviderId) ?? installations.FirstOrDefault();
      if (selected == null && !legacyScript && CliProfiles.IsLauncherPath(path) && File.Exists(path))
        selected = new CliInstallation { Executable = path, ProviderId = preferred.ProviderId };
      if (selected == null) selected = installations.FirstOrDefault();
      return new CliScanSelection {
        Installations = installations, Selected = selected, ClearLegacy = legacyScript && selected == null,
        ResetDefaults = selected != null && (legacyScript || selected.ProviderId != preferred.ProviderId)
      };
    }

    private async Task FindInstalledCliAsync(bool refresh = false)
    {
      CancelCliDiscovery();
      var preferred = ReadTranslationDraft();
      var preferenceKey = SettingsDiscoveryCache.ModelKey(preferred.ProviderId, preferred.Executable);
      var generation = cliDiscoveryGeneration;
      var cancellation = new CancellationTokenSource(); cliDiscoveryCancellation = cancellation;
      translationCliRefresh.Enabled = false;
      try {
        var installations = await settingsDiscovery.LoadInstallationsAsync(refresh, cancellation.Token);
        var result = await Task.Run(() => ResolveCliSelection(installations, preferred, cancellation.Token), cancellation.Token);
        if (cancellation.IsCancellationRequested || IsDisposed || Disposing || IsApiMode || generation != cliDiscoveryGeneration) return;
        var current = ReadTranslationDraft();
        if (preferenceKey != SettingsDiscoveryCache.ModelKey(current.ProviderId, current.Executable)) return;
        FillInstalledCli(result.Installations, result.Selected);
        if (result.Selected != null) {
          var changed = result.ResetDefaults || !CliConnectionSettings.SameIdentity(current.ProviderId, current.Executable, result.Selected.ProviderId, result.Selected.Executable);
          if (changed) RememberCliSettings();
          if (result.ResetDefaults) {
            var defaults = CliProfiles.Defaults(result.Selected.ProviderId, result.Selected.Executable);
            defaults.ParallelRequests = current.ParallelRequests; defaults.MinimumChunkCharacters = current.MinimumChunkCharacters; defaults.TimeoutSeconds = current.TimeoutSeconds; defaults.ShowButtons = current.ShowButtons;
            CliConnectionSettings.Capture(defaults).ApplyTo(current);
          }
          if (changed) FindCliDraft(result.Selected.ProviderId, result.Selected.Executable)?.ApplyTo(current);
          current.ProviderId = result.Selected.ProviderId; current.Executable = result.Selected.Executable;
          translationDraft = current;
          if (changed) {
            // Only an actual launcher migration resets catalog controls. Preserve edits made during the scan.
            var manual = translationUseManualModel.Checked;
            SetTranslationControls(current);
            if (manual && !result.ResetDefaults) translationUseManualModel.Checked = true;
          }
          await LoadModelsForSettingsAsync(current);
        }
        else {
          if (result.ClearLegacy) { CancelModelDiscovery(); translationDraft.Executable = ""; translationExecutable.Text = ""; }
          translationModelStatus.Text = "CLI не найден. Нажмите «Выбрать…», чтобы указать уже установленную программу.";
        }
      }
      catch (OperationCanceledException) { }
      catch (Exception error) { if (!IsDisposed && generation == cliDiscoveryGeneration && !cancellation.IsCancellationRequested) translationModelStatus.Text = error.Message; }
      finally {
        if (ReferenceEquals(cliDiscoveryCancellation, cancellation)) {
          cliDiscoveryCancellation = null;
          if (!IsDisposed) translationCliRefresh.Enabled = true;
        }
        cancellation.Dispose();
      }
    }

    private async Task ApplyCliAsync(CliInstallation installation, bool defaults)
    {
      var current = ReadCliDraft();
      if (!defaults && CliConnectionSettings.SameIdentity(current.ProviderId, current.Executable, installation.ProviderId, installation.Executable)) return;
      CancelCliDiscovery(); cliDiscoveryStarted = true;
      CancelModelDiscovery();
      RememberCliSettings();
      var options = current.Copy();
      var saved = defaults ? null : FindCliDraft(installation.ProviderId, installation.Executable);
      if (saved != null) saved.ApplyTo(options);
      else {
        var initial = CliProfiles.Defaults(installation.ProviderId, installation.Executable);
        initial.ParallelRequests = current.ParallelRequests; initial.MinimumChunkCharacters = current.MinimumChunkCharacters; initial.ShowButtons = current.ShowButtons;
        CliConnectionSettings.Capture(initial).ApplyTo(options);
      }
      options.ProviderId = installation.ProviderId; options.Executable = installation.Executable;
      translationDraft = options;
      SetTranslationControls(options);
      await LoadModelsForSettingsAsync(options.Copy());
    }

    private CliConnectionSettings FindCliDraft(string provider, string executable) => cliDrafts.FirstOrDefault(c => CliConnectionSettings.SameIdentity(c.ProviderId, c.Executable, provider, executable));

    private void RememberCliSettings()
    {
      var current = ReadCliDraft();
      CliConnectionSettings.Upsert(cliDrafts, CliConnectionSettings.Capture(current));
      translationDraft = current;
    }

    private async Task SelectCliAsync(CliInstallation installation)
    {
      FillInstalledCli(translationCli.Items.Cast<CliInstallation>().ToList(), installation);
      await ApplyCliAsync(installation, false);
    }

    private Task SelectCliPathAsync(string path)
    {
      // Browsing the current file must not reinterpret a user-selected adapter or create a duplicate.
      var provider = CliProfiles.Identify(path);
      var saved = cliDrafts.Where(c => CliConnectionSettings.SameIdentity("", c.Executable, "", path)).ToList();
      provider = (saved.FirstOrDefault(c => c.ProviderId == provider) ?? saved.LastOrDefault())?.ProviderId ?? provider;
      if (CliConnectionSettings.SameIdentity(translationDraft.ProviderId, translationExecutable.Text, translationDraft.ProviderId, path)) provider = translationDraft.ProviderId;
      return SelectCliAsync(new CliInstallation { Executable = path, ProviderId = provider });
    }

    private void SetTranslationControls(TranslationOptions options)
    {
      updatingTranslation = true;
      hasModelCatalog = false;
      translationExecutable.Text = options.Executable;
      translationProfile.SelectedItem = CliProfiles.Get(options.ProviderId);
      translationTimeout.Value = Math.Max(10, Math.Min(3600, options.TimeoutSeconds));
      translationShowButtons.Checked = options.ShowButtons;
      if (!displayedApiMode && translationParallelRequests != null) SetParallelControls(options.ParallelRequests, options.MinimumChunkCharacters);
      translationCustomArguments.Checked = options.UseCustomArguments;
      translationArguments.ReadOnly = !options.UseCustomArguments;
      translationOutput.Enabled = options.UseCustomArguments;
      translationArguments.Text = CliProfiles.ArgumentsFor(options);
      translationOutput.SelectedItem = options.OutputFormat;
      if (translationOutput.SelectedIndex < 0) translationOutput.SelectedIndex = 0;
      translationManualModel.Text = options.Model;
      translationUseManualModel.Enabled = CliProfiles.Get(options.ProviderId).SupportsModelOverride || options.UseCustomArguments;
      translationUseManualModel.Checked = options.UseManualModel && translationUseManualModel.Enabled;
      translationManualModel.Enabled = translationUseManualModel.Checked;
      translationModel.Enabled = !translationUseManualModel.Checked;
      FillModels(new CliModelCatalog(), options);
      updatingTranslation = false;
      CliModelCatalog cached; bool fresh;
      if (translationParallelRequests != null && settingsDiscovery.TryModels(options.ProviderId, options.Executable, out cached, out fresh)) ApplyModelCatalog(cached, options);
    }

    private void SetParallelControls(int parallel, int minimum)
    {
      translationParallelRequests.Value = Math.Max(1, Math.Min(8, parallel));
      translationMinimumChunk.Value = Math.Max(0, Math.Min(1000000, minimum));
    }

    private Task LoadModelsForSettingsAsync(TranslationOptions preference, bool refresh = false)
    {
      var key = SettingsDiscoveryCache.ModelKey(preference.ProviderId, preference.Executable);
      if (modelCancellation != null && modelDiscoveryKey == key) return modelDiscoveryTask ?? Task.CompletedTask;
      CancelModelDiscovery();
      CliModelCatalog cached; bool fresh;
      if (settingsDiscovery.TryModels(preference.ProviderId, preference.Executable, out cached, out fresh)) {
        ApplyModelCatalog(cached, ReadTranslationDraft());
        if (fresh && !refresh) return Task.CompletedTask;
      }
      var cancellation = new CancellationTokenSource(); modelCancellation = cancellation; modelDiscoveryKey = key;
      modelDiscoveryTask = LoadModelCatalogAsync(preference, key, refresh, cancellation, modelGeneration);
      return modelDiscoveryTask;
    }

    private void ApplyModelCatalog(CliModelCatalog catalog, TranslationOptions preference)
    {
      var manual = translationUseManualModel.Checked;
      var manualModel = translationManualModel.Text;
      updatingTranslation = true;
      try {
        hasModelCatalog = true; FillModels(catalog, preference);
        translationManualModel.Text = manualModel; translationUseManualModel.Checked = manual;
        if (manual) FillEfforts("");
      }
      finally { updatingTranslation = false; }
      translationModelStatus.Text = string.IsNullOrWhiteSpace(catalog.Note) ? "Моделей получено: " + catalog.Models.Count : catalog.Note;
      if (!string.IsNullOrEmpty(translationModelSelectionNote)) translationModelStatus.Text += " " + translationModelSelectionNote;
      translationModel.Enabled = !manual; UpdateEffortState(); UpdateArgumentPreview();
    }

    private async Task LoadModelCatalogAsync(TranslationOptions preference, string key, bool refresh, CancellationTokenSource cancellation, int generation)
    {
      translationModelsRefresh.Enabled = false;
      translationModelStatus.Text = "Запрашиваем модели у " + CliProfiles.Get(preference.ProviderId).Name + "…";
      try {
        var catalog = await settingsDiscovery.LoadModelsAsync(preference.ProviderId, preference.Executable, refresh, cancellation.Token);
        if (cancellation.IsCancellationRequested || generation != modelGeneration || IsDisposed || Disposing || IsApiMode) return;
        var current = ReadTranslationDraft();
        if (key != SettingsDiscoveryCache.ModelKey(current.ProviderId, current.Executable)) return;
        ApplyModelCatalog(catalog, current);
      }
      catch (OperationCanceledException) { }
      catch (Exception error) {
        if (!IsDisposed && generation == modelGeneration && !cancellation.IsCancellationRequested)
          translationModelStatus.Text = "Список моделей недоступен: " + error.Message + " Сохранённый выбор модели доступен.";
      }
      finally {
        if (ReferenceEquals(modelCancellation, cancellation)) {
          modelCancellation = null; modelDiscoveryKey = null; modelDiscoveryTask = null;
          if (!IsDisposed) {
            translationModelsRefresh.Enabled = true; translationModel.Enabled = !translationUseManualModel.Checked;
            UpdateEffortState();
          }
        }
        cancellation.Dispose();
      }
    }

    private void FillModels(CliModelCatalog catalog, TranslationOptions preference)
    {
      translationModel.BeginUpdate();
      try {
      translationModel.Items.Clear();
      translationModelSelectionNote = "";
      var savedModel = preference.Model;
      var savedEffort = preference.ReasoningEffort;
      var useDefaultModel = preference.UseDefaultModel;
      if (hasModelCatalog && preference.ProviderId == "cursor" && !useDefaultModel && (savedModel ?? "").EndsWith("-fast", StringComparison.Ordinal)) {
        var normalModel = savedModel.Substring(0, savedModel.Length - "-fast".Length);
        if (catalog.Models.Any(m => m.Id == normalModel)) {
          savedModel = normalModel;
          translationModelSelectionNote = "Сохранённый вариант Fast заменён соответствующей обычной моделью из списка CLI.";
        }
        else {
          savedModel = ""; savedEffort = ""; useDefaultModel = true;
          translationModelSelectionNote = "Для сохранённого варианта Fast нет обычной модели в списке CLI. Выбрана модель по умолчанию в CLI.";
        }
      }
      var defaultId = catalog.DefaultModelId ?? (preference.ProviderId == "ollama" ? catalog.Models.FirstOrDefault()?.Id : "");
      var defaultModel = catalog.Models.FirstOrDefault(m => m.Id == defaultId);
      var fallback = new ModelChoice { Default = true, Id = defaultId ?? "", Title = preference.ProviderId == "ollama" ? "Первая установленная модель" : "Модель по умолчанию в CLI", Efforts = defaultModel?.ReasoningEfforts ?? new List<CliReasoningEffort>(), DefaultEffort = catalog.ConfiguredReasoningEffort ?? defaultModel?.DefaultReasoningEffort };
      var choices = catalog.Models.GroupBy(m => m.BaseModelId ?? m.Id).Select(group => {
        var model = group.FirstOrDefault(m => m.BaseModelId != null && m.DefaultReasoningEffort == "") ?? group.FirstOrDefault(m => m.IsDefault) ?? group.First();
        return new ModelChoice { Id = model.Id, Title = model.BaseModelName ?? model.ToString(), ModelIds = group.Select(m => m.Id).ToList(), Efforts = model.ReasoningEfforts, DefaultEffort = catalog.ConfiguredReasoningEffort ?? model.DefaultReasoningEffort };
      }).ToList();
      var desired = !useDefaultModel ? choices.FirstOrDefault(m => m.Id == savedModel || m.ModelIds.Contains(savedModel)) : fallback;
      if (desired == null && !string.IsNullOrWhiteSpace(savedModel)) {
        desired = new ModelChoice { Id = savedModel, Title = "Сохранённая модель · " + CliModel.CleanDisplayName(savedModel) }; choices.Add(desired);
      }
      translationModel.Items.Add(fallback);
      translationModel.Items.AddRange(choices.OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToArray());
      translationModel.SelectedItem = desired ?? fallback;
      if (preference.ProviderId == "cursor" && !useDefaultModel && string.IsNullOrEmpty(savedEffort))
        savedEffort = catalog.Models.FirstOrDefault(m => m.Id == savedModel && m.BaseModelId != null)?.DefaultReasoningEffort;
      FillEfforts(savedEffort, useDefaultModel ? null : savedModel);
      }
      finally { translationModel.EndUpdate(); }
    }

    private void FillEfforts(string preferred, string preferredModelId = null)
    {
      var wasUpdating = updatingTranslation;
      updatingTranslation = true;
      translationEffort.BeginUpdate();
      try {
      var model = translationModel.SelectedItem as ModelChoice;
      translationEffort.Items.Clear();
      var automatic = new EffortChoice { Id = "", Title = "По умолчанию в CLI" + (string.IsNullOrWhiteSpace(model?.DefaultEffort) ? "" : " · " + model.DefaultEffort) };
      var cursorVariants = translationDraft.ProviderId == "cursor" && !translationUseManualModel.Checked && model != null && !model.Default && model.Efforts.Count > 0;
      if (!cursorVariants) translationEffort.Items.Add(automatic);
      if (!translationUseManualModel.Checked && model != null)
        foreach (var effort in model.Efforts.Where(e => cursorVariants || !string.IsNullOrEmpty(e.Id)))
          translationEffort.Items.Add(new EffortChoice { Id = effort.Id, Title = string.IsNullOrEmpty(effort.Id) ? "По умолчанию в CLI" : effort.ToString(), ModelId = preferredModelId != null && effort.ModelIds.Contains(preferredModelId) ? preferredModelId : effort.ModelId });
      var desired = translationEffort.Items.Cast<EffortChoice>().FirstOrDefault(e => e.Id == preferred);
      if (desired == null && !string.IsNullOrEmpty(preferred)) {
        desired = new EffortChoice { Id = preferred, Title = "Сохранено · " + preferred }; translationEffort.Items.Add(desired);
      }
      translationEffort.SelectedItem = desired ?? (cursorVariants ? translationEffort.Items.Cast<EffortChoice>().FirstOrDefault(e => e.Id == model.DefaultEffort) ?? translationEffort.Items[0] : automatic);
      UpdateEffortState();
      }
      finally { translationEffort.EndUpdate(); updatingTranslation = wasUpdating; }
    }

    private bool CanEditEffort => hasModelCatalog && !translationCustomArguments.Checked && !translationUseManualModel.Checked && translationEffort.Items.Count > 1 && ((translationModel.SelectedItem as ModelChoice)?.Efforts.Count ?? 0) > 0;

    private void UpdateEffortState()
    {
      translationEffort.Enabled = CanEditEffort;
      var selected = translationEffort.SelectedItem as EffortChoice;
      var known = (translationModel.SelectedItem as ModelChoice)?.Efforts.Any(e => e.Id == selected?.Id) ?? false;
      var note = translationCustomArguments.Checked ? "Effort задан в аргументах запуска." : !translationUseManualModel.Checked && !string.IsNullOrEmpty(selected?.Id) && !known ? "Сохранённый effort; этот вариант пока отсутствует в каталоге." : !translationUseManualModel.Checked && ((translationModel.SelectedItem as ModelChoice)?.Efforts.Count ?? 0) > 0 ? "Уровень рассуждений выбранной модели." : "Уровень рассуждений задаётся в CLI.";
      translationEffort.AccessibleDescription = note;
      settingsToolTips.SetToolTip(translationEffort, note);
      if (translationEffort.Parent != null)
        foreach (var label in translationEffort.Parent.Controls.OfType<Label>()) settingsToolTips.SetToolTip(label, note);
    }

    private void UpdateArgumentPreview()
    {
      if (updatingTranslation || translationArguments == null || translationCustomArguments.Checked) return;
      translationArguments.Text = CliProfiles.ArgumentsFor(ReadTranslationDraft());
    }

    private TranslationOptions ReadCliDraft()
    {
      var options = translationDraft.Copy();
      if (!displayedApiMode && translationParallelRequests != null) {
        options.ParallelRequests = (int)translationParallelRequests.Value;
        options.MinimumChunkCharacters = (int)translationMinimumChunk.Value;
      }
      options.Executable = translationExecutable.Text.Trim();
      options.TimeoutSeconds = (int)translationTimeout.Value; options.ShowButtons = translationShowButtons.Checked;
      options.UseCustomArguments = translationCustomArguments.Checked;
      options.Arguments = translationArguments.Text.Replace("\r", " ").Replace("\n", " ").Trim();
      options.OutputFormat = (string)translationOutput.SelectedItem ?? "text";
      var choice = translationModel.SelectedItem as ModelChoice;
      options.UseDefaultModel = !translationUseManualModel.Checked && (choice?.Default ?? true);
      options.UseManualModel = translationUseManualModel.Checked;
      options.Model = translationUseManualModel.Checked ? translationManualModel.Text.Trim() : choice?.Id ?? "";
      var effort = translationEffort.SelectedItem as EffortChoice;
      options.ReasoningEffort = translationUseManualModel.Checked ? "" : effort?.Id ?? "";
      if (!translationUseManualModel.Checked && !string.IsNullOrEmpty(effort?.ModelId)) {
        options.Model = effort.ModelId; options.UseDefaultModel = false;
      }
      return options;
    }

    private TranslationOptions ReadTranslationDraft()
    {
      var options = ReadCliDraft();
      options.CliConnections = cliDrafts.Select(c => c.Copy()).ToList();
      CliConnectionSettings.Upsert(options.CliConnections, CliConnectionSettings.Capture(options));
      ReadApiSettings(options);
      return options;
    }

    private async Task BrowseCliAsync()
    {
      CancelCliDiscovery();
      using (var dialog = CreateCliFileDialog(translationExecutable.Text)) {
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await SelectCliPathAsync(dialog.FileName);
      }
    }

    internal static OpenFileDialog CreateCliFileDialog(string path)
    {
      try { path = CliTranslator.ResolveExecutable(path); } catch (Exception) { }
      var directory = Path.IsPathRooted(path) ? Path.GetDirectoryName(path) : Environment.SystemDirectory;
      if (!Directory.Exists(directory)) directory = Environment.SystemDirectory;
      return new OpenFileDialog {
        Title = "Выберите программу или штатный запускатель CLI", Filter = "CLI (*.exe;*.cmd;*.bat)|*.exe;*.cmd;*.bat",
        InitialDirectory = directory, FileName = File.Exists(path) ? path : "", RestoreDirectory = true, CheckFileExists = true
      };
    }

    private bool ValidateTranslationSettings()
    {
      try {
        var options = ReadTranslationDraft();
        options.ValidateArgumentStorage();
        foreach (var connection in options.ApiConnections) {
          ApiTranslator.ValidateEndpointCredentials(connection.Endpoint);
          ApiTranslator.ValidateProxyDraft(connection);
        }
        if (options.ShowButtons) options.Validate(requireReady: false);
        TranslationOptions = options;
        return true;
      }
      catch (Exception error) when (error is ArgumentException || error is FileNotFoundException) {
        SelectTranslationTab(); if (IsApiMode) apiStatus.Text = error.Message; else translationModelStatus.Text = error.Message; return false;
      }
    }

    private void CancelModelDiscovery()
    {
      modelGeneration++; var cancellation = modelCancellation; modelCancellation = null; modelDiscoveryTask = null; modelDiscoveryKey = null; cancellation?.Cancel();
      if (!IsDisposed && translationModelsRefresh != null) translationModelsRefresh.Enabled = true;
    }

    private void CancelCliDiscovery()
    {
      cliDiscoveryGeneration++; var cancellation = cliDiscoveryCancellation; cliDiscoveryCancellation = null; cancellation?.Cancel();
      if (!IsDisposed && translationCliRefresh != null) translationCliRefresh.Enabled = true;
    }
  }
}
