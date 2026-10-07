using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AnotherMarkdown.Translation;

namespace AnotherMarkdown.Forms
{
  public partial class SettingsForm
  {
    private ComboBox connectionMode, apiConnections, apiProtocol, apiPreset, apiModel, apiEffort, apiTokenParameter;
    private TextBox apiName, apiEndpoint, apiKey, apiHeaders, apiParameters, apiAuthHeader, apiAuthPrefix;
    private NumericUpDown apiMaxTokens, apiTimeout, apiTemperature;
    private CheckBox apiUseTemperature, apiShowButtons;
    private Button apiRefresh, apiTest, apiRemove;
    private Label apiStatus;
    private TableLayoutPanel apiLayout;
    private List<ApiConnection> apiDrafts;
    private ApiConnection activeApiDraft;
    private bool updatingApi, apiKeyEdited, apiHeadersEdited;
    private CancellationTokenSource apiCancellation;
    private int apiGeneration;
    private string apiConfigurationError;
    private bool IsApiMode => connectionMode?.SelectedIndex == 1;

    private sealed class ApiChoice
    {
      public string Id, Title, Endpoint, Protocol, TokenParameter;
      public override string ToString() => Title;
    }

    private void InitializeApiSettings(TableLayoutPanel cliLayout)
    {
      apiDrafts = translationDraft.ApiConnections.Select(c => c.Copy()).ToList();
      apiConfigurationError = translationDraft.ApiConfigurationError;
      var root = new TableLayoutPanel { Name = "translationConnections", AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, RowCount = 3 };
      root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      for (var i = 0; i < 3; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      var modeRow = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(8, 8, 8, 0) };
      modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      connectionMode = new ComboBox { Name = "translationConnectionMode", AccessibleName = "Способ подключения", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
      connectionMode.Items.AddRange(new object[] { "CLI — установленная программа", "API — HTTP-эндпойнт" });
      modeRow.Controls.Add(MakeSettingsLabel("Подключение"), 0, 0); modeRow.Controls.Add(connectionMode, 1, 0);
      translationPage.Controls.Remove(cliLayout);
      root.Controls.Add(modeRow, 0, 0); root.Controls.Add(cliLayout, 0, 1);
      apiLayout = new TableLayoutPanel { Name = "apiLayout", AutoSize = true, Dock = DockStyle.Top, ColumnCount = 3, RowCount = 13, Padding = new Padding(8) };
      apiLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); apiLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); apiLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
      for (var i = 0; i < 13; i++) apiLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      root.Controls.Add(apiLayout, 0, 2); translationPage.Controls.Add(root);

      apiConnections = new ComboBox { Name = "apiConnections", AccessibleName = "Сохранённое подключение API", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
      var profileActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
      var add = new Button { Name = "apiAdd", Text = "+", Width = 36, Height = 27, AccessibleName = "Добавить подключение API" };
      apiRemove = new Button { Name = "apiRemove", Text = "−", Width = 36, Height = 27, AccessibleName = "Удалить подключение API" };
      profileActions.Controls.Add(add); profileActions.Controls.Add(apiRemove);
      AddTranslationRow(apiLayout, 0, "Сохранено", apiConnections, profileActions);
      apiName = MakeSettingsTextBox("apiName", "Название подключения API", 1);
      AddTranslationRow(apiLayout, 1, "Название", apiName, new Label());
      apiPreset = new ComboBox { Name = "apiPreset", AccessibleName = "Настройки сервиса API", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
      apiPreset.Items.AddRange(new object[] {
        new ApiChoice { Id = "custom", Title = "Своя настройка" },
        new ApiChoice { Id = "openai", Title = "OpenAI", Endpoint = "https://api.openai.com/v1", Protocol = "responses", TokenParameter = "max_completion_tokens" },
        new ApiChoice { Id = "deepseek", Title = "DeepSeek", Endpoint = "https://api.deepseek.com/v1", Protocol = "chat-completions" },
        new ApiChoice { Id = "openrouter", Title = "OpenRouter", Endpoint = "https://openrouter.ai/api/v1", Protocol = "chat-completions" },
        new ApiChoice { Id = "anthropic", Title = "Anthropic", Endpoint = "https://api.anthropic.com/v1", Protocol = "anthropic" },
        new ApiChoice { Id = "gemini", Title = "Google Gemini", Endpoint = "https://generativelanguage.googleapis.com/v1beta", Protocol = "gemini" },
        new ApiChoice { Id = "ollama", Title = "Ollama (локально)", Endpoint = "http://localhost:11434/v1", Protocol = "chat-completions" },
        new ApiChoice { Id = "lmstudio", Title = "LM Studio (локально)", Endpoint = "http://localhost:1234/v1", Protocol = "chat-completions" }
      });
      AddTranslationRow(apiLayout, 2, "Сервис", apiPreset, new Label());
      apiProtocol = new ComboBox { Name = "apiProtocol", AccessibleName = "Протокол API", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
      apiProtocol.Items.AddRange(new object[] {
        new ApiChoice { Id = "chat-completions", Title = "OpenAI-совместимый · Chat Completions" },
        new ApiChoice { Id = "responses", Title = "OpenAI Responses" },
        new ApiChoice { Id = "anthropic", Title = "Anthropic Messages" },
        new ApiChoice { Id = "gemini", Title = "Google Gemini · Generate Content" }
      });
      AddTranslationRow(apiLayout, 3, "Протокол", apiProtocol, new Label());
      apiEndpoint = MakeSettingsTextBox("apiEndpoint", "Адрес API", 4);
      AddTranslationRow(apiLayout, 4, "Эндпойнт", apiEndpoint, new Label());
      apiKey = MakeSettingsTextBox("apiKey", "Ключ API", 5); apiKey.UseSystemPasswordChar = true;
      var reveal = new CheckBox { Text = "Показать", AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
      reveal.CheckedChanged += (_, __) => apiKey.UseSystemPasswordChar = !reveal.Checked;
      AddTranslationRow(apiLayout, 5, "Ключ API", apiKey, reveal);
      apiModel = new ComboBox { Name = "apiModel", AccessibleName = "Модель API", DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
      apiRefresh = new Button { Name = "apiRefreshModels", Text = "Модели", AutoSize = true, Dock = DockStyle.Top };
      AddTranslationRow(apiLayout, 6, "Модель", apiModel, apiRefresh);
      var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
      apiTest = new Button { Name = "apiTest", Text = "Проверить подключение", AutoSize = true };
      var advancedToggle = new CheckBox { Name = "apiAdvanced", Text = "Дополнительные параметры", AutoSize = true, Margin = new Padding(12, 8, 3, 3) };
      var clearSecrets = new Button { Name = "apiClearCredentials", Text = "Очистить секреты", AutoSize = true };
      actions.Controls.Add(apiTest); actions.Controls.Add(advancedToggle);
      actions.Controls.Add(clearSecrets);
      apiLayout.Controls.Add(actions, 0, 7); apiLayout.SetColumnSpan(actions, 3);
      apiStatus = new Label { Name = "apiStatus", AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 6, 3, 8) };
      apiLayout.Controls.Add(apiStatus, 0, 8); apiLayout.SetColumnSpan(apiStatus, 3);

      var advanced = new TableLayoutPanel { Name = "apiAdvancedLayout", AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Visible = false };
      advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145)); advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      apiLayout.Controls.Add(advanced, 0, 9); apiLayout.SetColumnSpan(advanced, 3);
      apiEffort = new ComboBox { Name = "apiEffort", AccessibleName = "Effort API (если поддерживается моделью)", DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
      apiEffort.Items.AddRange(new object[] { "", "none", "minimal", "low", "medium", "high", "xhigh", "max" });
      apiMaxTokens = new NumericUpDown { Name = "apiMaxTokens", AccessibleName = "Лимит ответа: 0 — по умолчанию сервиса", Minimum = 0, Maximum = 2000000, Value = 0, Width = 150 };
      apiTokenParameter = new ComboBox { Name = "apiTokenParameter", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
      apiTokenParameter.Items.AddRange(new object[] { "max_tokens", "max_completion_tokens" });
      var temperatureRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
      apiUseTemperature = new CheckBox { Name = "apiUseTemperature", Text = "Передавать", AutoSize = true };
      apiTemperature = new NumericUpDown { Name = "apiTemperature", DecimalPlaces = 2, Minimum = 0, Maximum = 2, Increment = .1M, Enabled = false };
      temperatureRow.Controls.Add(apiUseTemperature); temperatureRow.Controls.Add(apiTemperature);
      apiUseTemperature.CheckedChanged += (_, __) => apiTemperature.Enabled = apiUseTemperature.Checked;
      apiAuthHeader = MakeSettingsTextBox("apiAuthHeader", "Заголовок авторизации API", 12);
      apiAuthPrefix = MakeSettingsTextBox("apiAuthPrefix", "Префикс ключа API", 13);
      apiParameters = new TextBox { Name = "apiParameters", AccessibleName = "Дополнительные параметры JSON", Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Height = 78 };
      apiHeaders = new TextBox { Name = "apiHeaders", AccessibleName = "Дополнительные HTTP-заголовки JSON", Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Height = 65 };
      var advancedControls = new Control[] { apiEffort, apiMaxTokens, apiTokenParameter, temperatureRow, apiAuthHeader, apiAuthPrefix, apiParameters, apiHeaders };
      var captions = new[] { "Effort (необязательно)", "Лимит ответа, токенов", "Поле лимита (Chat)", "Temperature", "Заголовок ключа", "Префикс ключа", "Параметры JSON", "Заголовки JSON" };
      for (var i = 0; i < captions.Length; i++) { advanced.RowStyles.Add(new RowStyle(SizeType.AutoSize)); advanced.Controls.Add(MakeSettingsLabel(captions[i]), 0, i); advanced.Controls.Add(advancedControls[i], 1, i); advancedControls[i].Margin = new Padding(3, 4, 3, 6); }
      advancedToggle.CheckedChanged += (_, __) => advanced.Visible = advancedToggle.Checked;
      var advancedHelp = new Label { AutoSize = true, Dock = DockStyle.Fill, Text = "Лимит ответа 0 — по умолчанию сервиса; Anthropic требует явный лимит. Пустой effort, выключенная temperature и пустой заголовок ключа сохраняют настройки протокола. Поддержка параметров зависит от модели. Дополнительные заголовки шифруются вместе с ключом." };
      advanced.Controls.Add(advancedHelp, 0, 8); advanced.SetColumnSpan(advancedHelp, 2);
      apiTimeout = new NumericUpDown { Name = "apiTimeout", Minimum = 10, Maximum = 3600, Value = translationDraft.TimeoutSeconds, Width = 130 };
      AddTranslationRow(apiLayout, 10, "Тайм-аут, сек.", apiTimeout, new Label());
      apiShowButtons = new CheckBox { Name = "apiShowButtons", Text = "Показывать кнопки перевода в панели", Checked = translationDraft.ShowButtons, AutoSize = true };
      apiLayout.Controls.Add(apiShowButtons, 0, 11); apiLayout.SetColumnSpan(apiShowButtons, 3);
      var help = new Label { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 8, 3, 8), Text = "Можно указать базовый URL API или полный адрес операции. Ключ необязателен для локального сервера. «Модели» получает список; ID модели можно ввести вручную. «Проверить» отправляет короткий тестовый запрос. Ключи защищены Windows для текущего пользователя. Перевод отображается только в предпросмотре." };
      apiLayout.Controls.Add(help, 0, 12); apiLayout.SetColumnSpan(help, 3);
      apiLayout.SizeChanged += (_, __) => { var width = Math.Max(200, apiLayout.ClientSize.Width - 24); help.MaximumSize = apiStatus.MaximumSize = advancedHelp.MaximumSize = new Size(width, 0); };

      apiConnections.SelectedIndexChanged += (_, __) => {
        if (updatingApi) return;
        CaptureApiControls(activeApiDraft); CancelApiDiscovery();
        activeApiDraft = apiConnections.SelectedItem as ApiConnection; FillApiControls();
      };
      add.Click += (_, __) => { CaptureApiControls(activeApiDraft); CancelApiDiscovery(); var profile = new ApiConnection { Name = "Подключение " + (apiDrafts.Count + 1) }; apiDrafts.Add(profile); RefillApiProfiles(profile.Id); };
      apiRemove.Click += (_, __) => { if (activeApiDraft == null) return; CancelApiDiscovery(); apiDrafts.Remove(activeApiDraft); activeApiDraft = null; RefillApiProfiles(apiDrafts.FirstOrDefault()?.Id); };
      apiName.Leave += (_, __) => { if (activeApiDraft != null) { CaptureApiControls(activeApiDraft); RefillApiProfiles(activeApiDraft.Id); } };
      apiPreset.SelectedIndexChanged += (_, __) => ApplyApiPreset();
      apiEndpoint.TextChanged += (_, __) => ApiAddressChanged();
      apiProtocol.SelectedIndexChanged += (_, __) => ApiAddressChanged();
      apiKey.TextChanged += (_, __) => { if (!updatingApi) { apiKeyEdited = true; ApiAddressChanged(); } };
      apiHeaders.TextChanged += (_, __) => { if (!updatingApi) { apiHeadersEdited = true; ApiAddressChanged(); } };
      foreach (var text in new TextBox[] { apiAuthHeader, apiAuthPrefix, apiParameters }) text.TextChanged += (_, __) => ApiAddressChanged();
      apiEffort.TextChanged += (_, __) => ApiAddressChanged();
      apiModel.TextChanged += (_, __) => ApiAddressChanged();
      apiTokenParameter.SelectedIndexChanged += (_, __) => ApiAddressChanged();
      apiMaxTokens.ValueChanged += (_, __) => ApiAddressChanged();
      apiTemperature.ValueChanged += (_, __) => ApiAddressChanged();
      apiUseTemperature.CheckedChanged += (_, __) => ApiAddressChanged();
      clearSecrets.Click += (_, __) => {
        if (activeApiDraft == null) return;
        CancelApiDiscovery(); activeApiDraft.ClearCredentials(); updatingApi = true;
        apiKey.Text = ""; apiHeaders.Text = "{}"; apiKeyEdited = apiHeadersEdited = false;
        updatingApi = false; apiStatus.Text = "Ключ и дополнительные заголовки очищены. Изменение вступит в силу после сохранения.";
      };
      apiRefresh.Click += async (_, __) => await QueryApiAsync(false);
      apiTest.Click += async (_, __) => await QueryApiAsync(true);
      connectionMode.SelectedIndexChanged += async (_, __) => {
        if (updatingApi) return;
        CancelModelDiscovery(); cliDiscoveryGeneration++; CancelApiDiscovery();
        cliLayout.Visible = !IsApiMode; apiLayout.Visible = IsApiMode;
        if (IsApiMode) {
          apiTimeout.Value = translationTimeout.Value; apiShowButtons.Checked = translationShowButtons.Checked;
          if (apiDrafts.Count == 0) { var profile = new ApiConnection(); apiDrafts.Add(profile); RefillApiProfiles(profile.Id); }
        }
        else { CaptureApiControls(activeApiDraft); translationTimeout.Value = apiTimeout.Value; translationShowButtons.Checked = apiShowButtons.Checked; await FindInstalledCliAsync(); }
      };
      FormClosing += (_, __) => CancelApiDiscovery();
      updatingApi = true;
      connectionMode.SelectedIndex = translationDraft.UseApi ? 1 : 0;
      updatingApi = false;
      cliLayout.Visible = !IsApiMode; apiLayout.Visible = IsApiMode;
      RefillApiProfiles(translationDraft.SelectedApiConnectionId);
    }

    private void RefillApiProfiles(string id)
    {
      updatingApi = true;
      apiConnections.Items.Clear(); apiConnections.Items.AddRange(apiDrafts.ToArray());
      apiConnections.SelectedItem = apiDrafts.FirstOrDefault(c => c.Id == id) ?? apiDrafts.FirstOrDefault();
      activeApiDraft = apiConnections.SelectedItem as ApiConnection;
      updatingApi = false; FillApiControls();
    }

    private void FillApiControls()
    {
      updatingApi = true;
      var value = activeApiDraft ?? new ApiConnection();
      apiName.Text = value.Name; apiEndpoint.Text = value.Endpoint; apiKey.Text = value.ApiKey;
      apiProtocol.SelectedItem = apiProtocol.Items.Cast<ApiChoice>().FirstOrDefault(p => p.Id == value.Protocol);
      apiPreset.SelectedIndex = 0; apiModel.Items.Clear(); apiModel.Text = value.Model; apiEffort.Text = value.ReasoningEffort;
      apiMaxTokens.Value = Math.Max(0, Math.Min(2000000, value.MaxOutputTokens));
      apiTokenParameter.SelectedItem = value.TokenLimitParameter;
      apiUseTemperature.Checked = value.Temperature.HasValue;
      var temperature = value.Temperature ?? 0;
      if (double.IsNaN(temperature) || double.IsInfinity(temperature)) { temperature = 0; apiUseTemperature.Checked = false; }
      apiTemperature.Value = (decimal)Math.Max(0, Math.Min(2, temperature));
      apiAuthHeader.Text = value.AuthHeader; apiAuthPrefix.Text = value.AuthPrefix;
      apiParameters.Text = value.AdditionalParametersJson; apiHeaders.Text = value.AdditionalHeadersJson;
      apiKeyEdited = apiHeadersEdited = false;
      apiRemove.Enabled = activeApiDraft != null;
      foreach (var control in new Control[] { apiName, apiEndpoint, apiKey, apiProtocol, apiPreset, apiModel, apiRefresh, apiTest }) control.Enabled = activeApiDraft != null;
      apiStatus.Text = apiConfigurationError ?? value.CredentialError ?? "Укажите адрес и модель. Затем запросите модели или проверьте подключение.";
      updatingApi = false;
    }

    private void CaptureApiControls(ApiConnection value)
    {
      if (value == null || apiName == null) return;
      value.Name = apiName.Text.Trim(); if (value.Name.Length == 0) value.Name = "API";
      value.Endpoint = apiEndpoint.Text.Trim(); value.Protocol = (apiProtocol.SelectedItem as ApiChoice)?.Id ?? "chat-completions";
      value.Model = apiModel.SelectedItem is CliModel model ? model.Id : apiModel.Text.Trim();
      value.ReasoningEffort = apiEffort.Text.Trim(); value.MaxOutputTokens = (int)apiMaxTokens.Value;
      value.TokenLimitParameter = (string)apiTokenParameter.SelectedItem ?? "max_tokens";
      value.Temperature = apiUseTemperature.Checked ? (double?)apiTemperature.Value : null;
      value.AuthHeader = apiAuthHeader.Text.Trim(); value.AuthPrefix = apiAuthPrefix.Text;
      value.AdditionalParametersJson = apiParameters.Text.Trim();
      if (apiKeyEdited) value.ApiKey = apiKey.Text.Trim();
      if (apiHeadersEdited) value.AdditionalHeadersJson = apiHeaders.Text.Trim();
    }

    private void ReadApiSettings(TranslationOptions options)
    {
      if (connectionMode == null) return;
      options.ConnectionMode = IsApiMode ? "api" : "cli";
      options.ApiConfigurationError = apiConfigurationError;
      options.ApiConnections = apiDrafts.Select(c => c.Copy()).ToList();
      options.SelectedApiConnectionId = activeApiDraft?.Id ?? "";
      CaptureApiControls(options.ApiConnections.FirstOrDefault(c => c.Id == options.SelectedApiConnectionId));
      if (IsApiMode) { options.TimeoutSeconds = (int)apiTimeout.Value; options.ShowButtons = apiShowButtons.Checked; }
    }

    private void ApplyApiPreset()
    {
      if (updatingApi || !(apiPreset.SelectedItem is ApiChoice preset) || preset.Id == "custom") return;
      CancelApiDiscovery(); updatingApi = true;
      activeApiDraft?.ClearCredentials();
      apiName.Text = preset.Title; apiEndpoint.Text = preset.Endpoint;
      apiProtocol.SelectedItem = apiProtocol.Items.Cast<ApiChoice>().First(p => p.Id == preset.Protocol);
      apiTokenParameter.SelectedItem = preset.TokenParameter ?? "max_tokens";
      apiKey.Text = ""; apiModel.Items.Clear(); apiModel.Text = "";
      apiEffort.Text = ""; apiHeaders.Text = "{}"; apiParameters.Text = "{}"; apiAuthHeader.Text = apiAuthPrefix.Text = "";
      apiUseTemperature.Checked = false; apiMaxTokens.Value = preset.Protocol == "anthropic" ? 8192 : 0;
      apiKeyEdited = apiHeadersEdited = true; updatingApi = false;
      apiStatus.Text = "Настройки сервиса подставлены. Укажите ключ и модель этого подключения.";
    }

    private void ApiAddressChanged()
    {
      if (updatingApi) return;
      CancelApiDiscovery(); apiStatus.Text = "Настройки подключения изменены. Повторите запрос моделей или проверку.";
    }

    private async Task QueryApiAsync(bool translate)
    {
      if (activeApiDraft == null) return;
      CancelApiDiscovery(); var generation = apiGeneration;
      var cancellation = new CancellationTokenSource(); apiCancellation = cancellation;
      var snapshot = activeApiDraft.Copy(); CaptureApiControls(snapshot);
      apiRefresh.Enabled = apiTest.Enabled = apiModel.Enabled = btnSave.Enabled = false;
      apiStatus.Text = translate ? "Проверяем перевод короткого примера…" : "Запрашиваем модели API…";
      try {
        var translator = new ApiTranslator();
        if (translate) {
          await translator.TranslateAsync("# Connection test\nThe connection is working.", snapshot, (int)apiTimeout.Value, cancellation.Token);
          if (!cancellation.IsCancellationRequested && generation == apiGeneration && !IsDisposed) apiStatus.Text = "Короткий тест перевода выполнен: модель вернула ответ.";
        }
        else {
          var catalog = await translator.LoadModelsAsync(snapshot, Math.Min(30, (int)apiTimeout.Value), cancellation.Token);
          if (cancellation.IsCancellationRequested || generation != apiGeneration || IsDisposed) return;
          FillApiModels(catalog, snapshot.Model);
          apiStatus.Text = "Моделей получено: " + catalog.Models.Count + ". Если нужной нет в списке, введите её ID.";
        }
      }
      catch (OperationCanceledException) { }
      catch (Exception error) {
        if (generation == apiGeneration && !IsDisposed && !cancellation.IsCancellationRequested) apiStatus.Text = error.Message;
      }
      finally {
        if (ReferenceEquals(apiCancellation, cancellation)) { apiCancellation = null; if (!IsDisposed) apiRefresh.Enabled = apiTest.Enabled = apiModel.Enabled = btnSave.Enabled = true; }
        cancellation.Dispose();
      }
    }

    private void FillApiModels(CliModelCatalog catalog, string selectedModel)
    {
      var wasUpdating = updatingApi;
      updatingApi = true;
      try {
        apiModel.Items.Clear();
        apiModel.Items.AddRange(catalog.Models.OrderBy(m => string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name, StringComparer.OrdinalIgnoreCase)
          .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToArray());
        apiModel.SelectedItem = catalog.Models.FirstOrDefault(m => m.Id == selectedModel);
        if (apiModel.SelectedItem == null) apiModel.Text = selectedModel;
      }
      finally { updatingApi = wasUpdating; }
    }

    private void CancelApiDiscovery()
    {
      apiGeneration++; var cancellation = apiCancellation; apiCancellation = null; cancellation?.Cancel();
      if (apiRefresh != null && !IsDisposed) { apiRefresh.Enabled = apiTest.Enabled = apiModel.Enabled = activeApiDraft != null; btnSave.Enabled = true; }
    }
  }
}
