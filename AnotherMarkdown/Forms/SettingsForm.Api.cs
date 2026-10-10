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
    private bool apiQueryIsTranslation;
    private int apiGeneration;
    private bool apiDiscoveryQueued;
    private bool openApiModelsWhenReady;
    private bool translationScrollLayoutQueued;
    private CliModelCatalog apiCatalog;
    private string apiConfigurationError;
    private ComboBox apiProxyMode, apiProxyProtocol;
    private TextBox apiProxyAddress, apiProxyUsername, apiProxyPassword;
    private CheckBox apiProxyUseDefaultCredentials;
    private TableLayoutPanel apiProxyLayout;
    private bool apiProxyUsernameEdited, apiProxyPasswordEdited;
    private bool IsApiMode => connectionMode?.SelectedIndex == 1;

    private sealed class ApiChoice
    {
      public string Id, Title, Endpoint, Protocol, TokenParameter;
      public override string ToString() => Title;
    }

    private sealed class ApiEffortChoice
    {
      public string Id, Title;
      public override string ToString() => Title;
    }

    private void InitializeApiSettings(TableLayoutPanel cliLayout)
    {
      apiDrafts = translationDraft.ApiConnections.Select(c => c.Copy()).ToList();
      foreach (var profile in apiDrafts) InitializeApiPreferences(profile, TranslationOptions);
      apiConfigurationError = translationDraft.ApiConfigurationError;
      var root = HoldInitialLayout(new SettingsLayoutPanel { Name = "translationConnections", AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, RowCount = 4 });
      var previousContentSize = Size.Empty;
      root.SizeChanged += (_, __) => {
        if (root.Size == previousContentSize) return;
        previousContentSize = root.Size;
        QueueTranslationScrollLayout();
      };
      root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      for (var i = 0; i < 4; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      var modeRow = HoldInitialLayout(new SettingsLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(8, 8, 8, 0) });
      modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SettingsLabelWidth)); modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      connectionMode = new SettingsComboBox { Name = "translationConnectionMode", AccessibleName = "Способ подключения", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = SettingsFieldMargin };
      connectionMode.Items.AddRange(new object[] { "CLI — установленная программа", "API — HTTP-эндпойнт" });
      modeRow.Controls.Add(MakeSettingsLabel("Подключение"), 0, 0); modeRow.Controls.Add(connectionMode, 1, 0);
      translationPage.Controls.Remove(cliLayout);
      var threadingGroup = CreateParallelRequestsSettings();
      root.Controls.Add(modeRow, 0, 0); root.Controls.Add(threadingGroup, 0, 1); root.Controls.Add(cliLayout, 0, 2);
      apiLayout = HoldInitialLayout(new SettingsLayoutPanel { Name = "apiLayout", AutoSize = true, Dock = DockStyle.Top, ColumnCount = 3, RowCount = 15, Padding = new Padding(8) });
      apiLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SettingsLabelWidth)); apiLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); apiLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SettingsActionWidth));
      for (var i = 0; i < 15; i++) apiLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
      root.Controls.Add(apiLayout, 0, 3); translationPage.Controls.Add(root);
      Action arrangeSections = () => {
        root.SuspendLayout();
        try {
          if (IsApiMode) {
            // Keep connection and action buttons visible before the optional
            // threading group; the group remains reachable by scrolling.
            root.SetCellPosition(cliLayout, new TableLayoutPanelCellPosition(0, 3));
            root.SetCellPosition(apiLayout, new TableLayoutPanelCellPosition(0, 1));
            root.SetCellPosition(threadingGroup, new TableLayoutPanelCellPosition(0, 2));
          }
          else {
            root.SetCellPosition(apiLayout, new TableLayoutPanelCellPosition(0, 3));
            root.SetCellPosition(cliLayout, new TableLayoutPanelCellPosition(0, 1));
            root.SetCellPosition(threadingGroup, new TableLayoutPanelCellPosition(0, 2));
          }
        }
        finally { root.ResumeLayout(true); }
      };

      apiConnections = new SettingsComboBox { Name = "apiConnections", AccessibleName = "Сохранённое подключение API", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
      var profileActions = HoldInitialLayout(new SettingsLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1 });
      profileActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); profileActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
      var add = new Button { Name = "apiAdd", Text = "+", Dock = DockStyle.Top, Height = 27, AccessibleName = "Добавить подключение API", Margin = new Padding(0, 0, 3, 0) };
      apiRemove = new Button { Name = "apiRemove", Text = "−", Dock = DockStyle.Top, Height = 27, AccessibleName = "Удалить подключение API", Margin = new Padding(3, 0, 0, 0) };
      profileActions.Controls.Add(add, 0, 0); profileActions.Controls.Add(apiRemove, 1, 0);
      AddTranslationRow(apiLayout, 0, "Сохранено", apiConnections, profileActions);
      apiName = MakeSettingsTextBox("apiName", "Название подключения API", 1);
      AddTranslationRow(apiLayout, 1, "Название", apiName, new Label());
      apiPreset = new SettingsComboBox { Name = "apiPreset", AccessibleName = "Настройки сервиса API", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
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
      apiProtocol = new SettingsComboBox { Name = "apiProtocol", AccessibleName = "Протокол API", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
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
      apiModel = new SettingsComboBox { Name = "apiModel", AccessibleName = "Модель API", DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill, DeferEmptyList = true };
      apiRefresh = new Button { Name = "apiRefreshModels", Text = "Обновить", AutoSize = true, Dock = DockStyle.Top };
      apiEffort = new SettingsComboBox { Name = "apiEffort", AccessibleName = "Уровень рассуждений API", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Enabled = false };
      apiEffort.Visible = false;
      apiEffortLabel = AddModelEffortRow(apiLayout, 6, "apiModelRow", apiModel, apiEffort, apiRefresh);
      InitializeApiProxySettings();
      var actions = HoldInitialLayout(new FlowLayoutPanel { Name = "apiActions", AutoSize = true, Dock = DockStyle.Fill, WrapContents = true });
      apiTest = new Button { Name = "apiTest", Text = "Проверить подключение", AutoSize = true };
      var advancedToggle = new CheckBox { Name = "apiAdvanced", Text = "Дополнительные параметры", AutoSize = true, Margin = new Padding(12, 8, 3, 3) };
      var clearSecrets = new Button { Name = "apiClearCredentials", Text = "Очистить секреты", AutoSize = true };
      actions.Controls.Add(apiTest); actions.Controls.Add(advancedToggle);
      actions.Controls.Add(clearSecrets);
      apiLayout.Controls.Add(actions, 0, 9); apiLayout.SetColumnSpan(actions, 3);
      apiStatus = CreateSettingsStatus("apiStatus", "");
      apiLayout.Controls.Add(apiStatus, 0, 10); apiLayout.SetColumnSpan(apiStatus, 3);

      var advanced = HoldInitialLayout(new SettingsLayoutPanel { Name = "apiAdvancedLayout", AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Visible = false, Margin = new Padding(0) });
      advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SettingsLabelWidth)); advanced.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      apiLayout.Controls.Add(advanced, 0, 11); apiLayout.SetColumnSpan(advanced, 3);
      apiMaxTokens = new NumericUpDown { Name = "apiMaxTokens", AccessibleName = "Лимит ответа: 0 — по умолчанию сервиса", Minimum = 0, Maximum = 2000000, Value = 0, Width = 150 };
      apiTokenParameter = new SettingsComboBox { Name = "apiTokenParameter", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
      apiTokenParameter.Items.AddRange(new object[] { "max_tokens", "max_completion_tokens" });
      var temperatureRow = HoldInitialLayout(new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill });
      apiUseTemperature = new CheckBox { Name = "apiUseTemperature", Text = "Передавать", AutoSize = true };
      apiTemperature = new NumericUpDown { Name = "apiTemperature", DecimalPlaces = 2, Minimum = 0, Maximum = 2, Increment = .1M, Enabled = false };
      temperatureRow.Controls.Add(apiUseTemperature); temperatureRow.Controls.Add(apiTemperature);
      apiUseTemperature.CheckedChanged += (_, __) => apiTemperature.Enabled = apiUseTemperature.Checked;
      apiAuthHeader = MakeSettingsTextBox("apiAuthHeader", "Заголовок авторизации API", 12);
      apiAuthPrefix = MakeSettingsTextBox("apiAuthPrefix", "Префикс ключа API", 13);
      apiParameters = new TextBox { Name = "apiParameters", AccessibleName = "Дополнительные параметры JSON", Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Height = 78 };
      apiHeaders = new TextBox { Name = "apiHeaders", AccessibleName = "Дополнительные HTTP-заголовки JSON", Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Height = 65 };
      var advancedControls = new Control[] { apiMaxTokens, apiTokenParameter, temperatureRow, apiAuthHeader, apiAuthPrefix, apiParameters, apiHeaders };
      var captions = new[] { "Лимит ответа, токенов", "Поле лимита (Chat)", "Temperature", "Заголовок ключа", "Префикс ключа", "Параметры JSON", "Заголовки JSON" };
      for (var i = 0; i < captions.Length; i++) { advanced.RowStyles.Add(new RowStyle(SizeType.AutoSize)); advanced.Controls.Add(MakeSettingsLabel(captions[i]), 0, i); advanced.Controls.Add(advancedControls[i], 1, i); advancedControls[i].Margin = new Padding(3, 4, 3, 6); }
      advancedToggle.CheckedChanged += (_, __) => advanced.Visible = advancedToggle.Checked;
      var advancedHelp = new Label { AutoSize = true, Dock = DockStyle.Fill, Text = "Лимит ответа 0 — по умолчанию сервиса; Anthropic требует явный лимит. Effort доступен только если API сообщает уровни выбранной модели. Значение передаётся в поле запроса, указанном в подсказке Effort. Дополнительные заголовки шифруются вместе с ключом." };
      advanced.Controls.Add(advancedHelp, 0, 7); advanced.SetColumnSpan(advancedHelp, 2);
      apiTimeout = new NumericUpDown { Name = "apiTimeout", Minimum = 10, Maximum = 3600, Value = translationDraft.TimeoutSeconds, Width = 130 };
      AddTranslationRow(apiLayout, 12, "Тайм-аут, сек.", apiTimeout, new Label());
      apiShowButtons = new CheckBox { Name = "apiShowButtons", Text = "Показывать кнопки перевода в панели", Checked = translationDraft.ShowButtons, AutoSize = true };
      apiLayout.Controls.Add(apiShowButtons, 0, 13); apiLayout.SetColumnSpan(apiShowButtons, 3);
      var help = new Label { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(3, 8, 3, 8), Text = "Укажите URL API, ключ и модель. Модели загружаются при открытии и смене подключения. Effort доступен только при наличии уровней для выбранной модели в ответе API. «Проверить» отправляет короткий запрос. Ключи защищены Windows. Перевод отображается только в предпросмотре." };
      apiLayout.Controls.Add(help, 0, 14); apiLayout.SetColumnSpan(help, 3);
      apiLayout.SizeChanged += (_, __) => { var width = Math.Max(200, apiLayout.ClientSize.Width - 24); help.MaximumSize = advancedHelp.MaximumSize = new Size(width, 0); };

      apiConnections.SelectedIndexChanged += (_, __) => {
        if (updatingApi || ReferenceEquals(activeApiDraft, apiConnections.SelectedItem)) return;
        CaptureApiControls(activeApiDraft); CancelApiDiscovery(); openApiModelsWhenReady = false;
        activeApiDraft = apiConnections.SelectedItem as ApiConnection; FillApiControls(); QueueApiDiscovery();
      };
      add.Click += (_, __) => { CaptureApiControls(activeApiDraft); CancelApiDiscovery(); var profile = new ApiConnection { Name = "Подключение " + (apiDrafts.Count + 1) }; InitializeApiPreferences(profile, ReadTranslationDraft()); apiDrafts.Add(profile); RefillApiProfiles(profile.Id); };
      apiRemove.Click += (_, __) => { if (activeApiDraft == null) return; CancelApiDiscovery(); apiDrafts.Remove(activeApiDraft); activeApiDraft = null; RefillApiProfiles(apiDrafts.FirstOrDefault()?.Id); };
      apiName.Leave += (_, __) => RefreshApiProfileName();
      apiPreset.SelectedIndexChanged += (_, __) => ApplyApiPreset();
      apiEndpoint.TextChanged += (_, __) => ApiCatalogIdentityChanged(true);
      apiEndpoint.Leave += (_, __) => QueueApiDiscovery();
      apiKey.Leave += (_, __) => QueueApiDiscovery();
      apiProtocol.SelectedIndexChanged += (_, __) => { UpdateApiTemperatureLimit(); ApiCatalogIdentityChanged(true); };
      apiKey.TextChanged += (_, __) => { if (!updatingApi) { apiKeyEdited = true; ApiCatalogIdentityChanged(); } };
      apiHeaders.TextChanged += (_, __) => { if (!updatingApi) { apiHeadersEdited = true; ApiCatalogIdentityChanged(); } };
      foreach (var text in new TextBox[] { apiAuthHeader, apiAuthPrefix }) text.TextChanged += (_, __) => ApiCatalogIdentityChanged();
      apiParameters.TextChanged += (_, __) => ApiAddressChanged();
      apiEffort.TextChanged += (_, __) => {
        if (updatingApi) return;
        ApiAddressChanged();
        if (apiCancellation == null) apiStatus.Text = "Effort передаётся как " + ApiEffortOptions.For(CurrentApiChoice()).WireField +
          "; пустое значение оставляет решение сервису.";
      };
      apiModel.TextChanged += (_, __) => { if (!updatingApi) { ApiAddressChanged(); UpdateApiEfforts(""); } };
      apiModel.SelectedIndexChanged += (_, __) => { if (!updatingApi) UpdateApiEfforts(""); };
      apiModel.DropDown += (_, __) => {
        if (apiModel.Items.Count > 0 || activeApiDraft == null) return;
        openApiModelsWhenReady = true;
        // CBN_DROPDOWN is raised before the native popup opens. Closing it in
        // this callback is overwritten by WinForms after the callback returns.
        BeginInvoke(new Action(() => {
          if (IsDisposed || Disposing) return;
          if (apiModel.Items.Count == 0) apiModel.DroppedDown = false;
          QueueApiDiscovery();
        }));
      };
      apiTokenParameter.SelectedIndexChanged += (_, __) => ApiAddressChanged();
      apiMaxTokens.ValueChanged += (_, __) => ApiAddressChanged();
      apiTemperature.ValueChanged += (_, __) => ApiAddressChanged();
      apiUseTemperature.CheckedChanged += (_, __) => ApiAddressChanged();
      clearSecrets.Click += (_, __) => {
        if (activeApiDraft == null) return;
        CancelApiDiscovery(); activeApiDraft.ClearCredentials(); updatingApi = true;
        var selectedModel = apiModel.SelectedItem is CliModel selected ? selected.Id : apiModel.Text;
        apiCatalog = null; ClearApiModelItems(); apiModel.Text = selectedModel;
        apiKey.Text = ""; apiHeaders.Text = "{}"; apiKeyEdited = apiHeadersEdited = false;
        apiProxyUsername.Text = apiProxyPassword.Text = "";
        apiProxyUsernameEdited = apiProxyPasswordEdited = false;
        UpdateApiEfforts("");
        updatingApi = false; apiStatus.Text = "Ключ, дополнительные заголовки и данные входа прокси очищены. Изменение вступит в силу после сохранения.";
      };
      apiRefresh.Click += async (_, __) => await QueryApiAsync(false, true);
      apiTest.Click += async (_, __) => await QueryApiAsync(true);
      connectionMode.SelectedIndexChanged += (_, __) => {
        if (updatingApi || displayedApiMode == IsApiMode) return;
        if (displayedApiMode) CaptureApiControls(activeApiDraft); else RememberCliSettings();
        CancelModelDiscovery(); CancelCliDiscovery(); cliDiscoveryStarted = false; CancelApiDiscovery();
        displayedApiMode = IsApiMode;
        cliLayout.Visible = !IsApiMode; apiLayout.Visible = IsApiMode;
        arrangeSections();
        if (IsApiMode) {
          if (apiDrafts.Count == 0) {
            var profile = new ApiConnection(); InitializeApiPreferences(profile, translationDraft); apiDrafts.Add(profile); RefillApiProfiles(profile.Id);
          }
          else FillApiControls();
          QueueApiDiscovery();
        }
        else { SetParallelControls(translationDraft.ParallelRequests, translationDraft.MinimumChunkCharacters); QueueCliDiscovery(); }
      };
      FormClosing += (_, __) => CancelApiDiscovery();
      updatingApi = true;
      connectionMode.SelectedIndex = translationDraft.UseApi ? 1 : 0;
      displayedApiMode = IsApiMode;
      updatingApi = false;
      cliLayout.Visible = !IsApiMode; apiLayout.Visible = IsApiMode;
      arrangeSections();
      RefillApiProfiles(translationDraft.SelectedApiConnectionId);
    }

    private static void InitializeApiPreferences(ApiConnection profile, TranslationOptions fallback)
    {
      profile.TimeoutSeconds = profile.TimeoutSeconds ?? fallback.TimeoutSeconds;
      profile.ParallelRequests = profile.ParallelRequests ?? fallback.ParallelRequests;
      profile.MinimumChunkCharacters = profile.MinimumChunkCharacters ?? fallback.MinimumChunkCharacters;
      profile.ShowButtons = profile.ShowButtons ?? fallback.ShowButtons;
    }

    private void QueueTranslationScrollLayout()
    {
      if (!IsHandleCreated || IsDisposed || Disposing || translationScrollLayoutQueued) return;
      translationScrollLayoutQueued = true;
      BeginInvoke(new Action(() => {
        translationScrollLayoutQueued = false;
        if (!IsDisposed && !Disposing) translationPage.PerformLayout();
      }));
    }

    private void InitializeApiProxySettings()
    {
      apiProxyMode = new SettingsComboBox { Name = "apiProxyMode", AccessibleName = "Режим прокси API", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
      apiProxyMode.Items.AddRange(new object[] {
        new ApiChoice { Id = "system", Title = "Системный прокси Windows" },
        new ApiChoice { Id = "direct", Title = "Без прокси — прямое соединение" },
        new ApiChoice { Id = "custom", Title = "Свой прокси" }
      });
      AddTranslationRow(apiLayout, 7, "Прокси", apiProxyMode, new Label());
      apiProxyLayout = HoldInitialLayout(new SettingsLayoutPanel { Name = "apiProxyLayout", AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Margin = new Padding(0) });
      apiProxyLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, SettingsLabelWidth));
      apiProxyLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
      apiLayout.Controls.Add(apiProxyLayout, 0, 8); apiLayout.SetColumnSpan(apiProxyLayout, 3);
      apiProxyProtocol = new SettingsComboBox { Name = "apiProxyProtocol", AccessibleName = "Тип прокси", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
      apiProxyProtocol.Items.AddRange(new object[] {
        new ApiChoice { Id = "http", Title = "HTTP" },
        new ApiChoice { Id = "socks5h", Title = "SOCKS5 — DNS через прокси" },
        new ApiChoice { Id = "socks5", Title = "SOCKS5 — локальный DNS" }
      });
      apiProxyProtocol.SelectedIndex = 0;
      apiProxyAddress = MakeSettingsTextBox("apiProxyAddress", "Адрес прокси", 0);
      apiProxyUsername = MakeSettingsTextBox("apiProxyUsername", "Логин прокси", 1);
      apiProxyPassword = MakeSettingsTextBox("apiProxyPassword", "Пароль прокси", 2);
      apiProxyPassword.UseSystemPasswordChar = true;
      var controls = new Control[] { apiProxyProtocol, apiProxyAddress, apiProxyUsername, apiProxyPassword };
      var captions = new[] { "Тип прокси", "Адрес прокси", "Логин прокси", "Пароль прокси" };
      for (var i = 0; i < controls.Length; i++) {
        apiProxyLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        apiProxyLayout.Controls.Add(MakeSettingsLabel(captions[i]), 0, i);
        apiProxyLayout.Controls.Add(controls[i], 1, i);
        controls[i].Margin = new Padding(3, 4, 3, 6);
      }
      apiProxyUseDefaultCredentials = new CheckBox { Name = "apiProxyUseDefaultCredentials", Text = "Вход в прокси с учётной записью Windows", AutoSize = true };
      apiProxyLayout.Controls.Add(apiProxyUseDefaultCredentials, 0, 4); apiProxyLayout.SetColumnSpan(apiProxyUseDefaultCredentials, 2);
      var help = new Label { Name = "apiProxyHelp", AutoSize = true, Dock = DockStyle.Fill,
        Text = "Адрес: сервер:порт или полный URL прокси. SOCKS5 использует системный curl.exe; DNS можно передать прокси. Вход Windows доступен для HTTP. HTTP-прокси для API на localhost не поддерживается — выберите прямое соединение или SOCKS5. Логин и пароль защищены Windows." };
      apiProxyLayout.Controls.Add(help, 0, 5); apiProxyLayout.SetColumnSpan(help, 2);
      apiProxyLayout.SizeChanged += (_, __) => help.MaximumSize = new Size(Math.Max(200, apiProxyLayout.ClientSize.Width - 12), 0);
      apiProxyMode.SelectedIndexChanged += (_, __) => { UpdateApiProxyControls(); ApiCatalogIdentityChanged(); };
      apiProxyUseDefaultCredentials.CheckedChanged += (_, __) => { UpdateApiProxyControls(); ApiCatalogIdentityChanged(); };
      apiProxyAddress.TextChanged += (_, __) => {
        if (!updatingApi) {
          var scheme = ProxyAddressScheme();
          var choice = apiProxyProtocol.Items.Cast<ApiChoice>().FirstOrDefault(p => p.Id == scheme);
          if (choice != null) { updatingApi = true; apiProxyProtocol.SelectedItem = choice; updatingApi = false; }
          UpdateApiProxyControls(); ApiCatalogIdentityChanged();
        }
      };
      apiProxyProtocol.SelectedIndexChanged += (_, __) => {
        if (updatingApi) return;
        var text = apiProxyAddress.Text.Trim(); var separator = text.IndexOf("://", StringComparison.Ordinal);
        if (separator >= 0) apiProxyAddress.Text = (apiProxyProtocol.SelectedItem as ApiChoice)?.Id + text.Substring(separator);
        UpdateApiProxyControls(); ApiCatalogIdentityChanged();
      };
      apiProxyUsername.TextChanged += (_, __) => { if (!updatingApi) { apiProxyUsernameEdited = true; ApiCatalogIdentityChanged(); } };
      apiProxyPassword.TextChanged += (_, __) => { if (!updatingApi) { apiProxyPasswordEdited = true; ApiCatalogIdentityChanged(); } };
    }

    private void UpdateApiProxyControls()
    {
      var mode = (apiProxyMode.SelectedItem as ApiChoice)?.Id ?? "system";
      var custom = mode == "custom";
      foreach (Control control in apiProxyLayout.Controls) {
        var row = apiProxyLayout.GetRow(control);
        if (row < 4 || row == 5) control.Visible = custom;
      }
      apiProxyLayout.Visible = mode != "direct";
      var socks = custom && ((apiProxyProtocol.SelectedItem as ApiChoice)?.Id ?? "http").StartsWith("socks5", StringComparison.Ordinal);
      if (socks) apiProxyUseDefaultCredentials.Checked = false;
      apiProxyUseDefaultCredentials.Enabled = activeApiDraft != null && mode != "direct" && !socks;
      apiProxyProtocol.Enabled = activeApiDraft != null && custom;
      apiProxyAddress.Enabled = activeApiDraft != null && custom;
      apiProxyUsername.Enabled = apiProxyPassword.Enabled = activeApiDraft != null && custom && !apiProxyUseDefaultCredentials.Checked;
    }

    private string ProxyAddressScheme()
    {
      var address = apiProxyAddress.Text.Trim(); var separator = address.IndexOf("://", StringComparison.Ordinal);
      return separator < 0 ? null : address.Substring(0, separator).ToLowerInvariant();
    }

    private void RefillApiProfiles(string id)
    {
      updatingApi = true;
      apiConnections.Items.Clear(); apiConnections.Items.AddRange(apiDrafts.ToArray());
      apiConnections.SelectedItem = apiDrafts.FirstOrDefault(c => c.Id == id) ?? apiDrafts.FirstOrDefault();
      activeApiDraft = apiConnections.SelectedItem as ApiConnection;
      updatingApi = false; FillApiControls(); QueueApiDiscovery();
    }

    private void RefreshApiProfileName()
    {
      if (updatingApi || activeApiDraft == null) return;
      CaptureApiControls(activeApiDraft);
      var index = apiConnections.SelectedIndex;
      if (index < 0) return;
      // Updating one display caption must not clear the selected model catalog
      // or overwrite controls while discovery is still in flight.
      updatingApi = true;
      try { apiConnections.Items[index] = activeApiDraft; }
      finally { updatingApi = false; }
    }

    private void ClearApiModelItems()
    {
      // An open native ComboBox can still report an old selected index after
      // Items.Clear. Close it before replacing the collection to avoid a native
      // CBN_SELCHANGE callback reading an index which no longer exists.
      apiModel.DroppedDown = false;
      apiModel.BeginUpdate();
      try { apiModel.Items.Clear(); }
      finally { apiModel.EndUpdate(); }
    }

    private void FillApiControls()
    {
      updatingApi = true;
      var value = activeApiDraft ?? new ApiConnection();
      apiName.Text = value.Name; apiEndpoint.Text = value.Endpoint; apiKey.Text = value.ApiKey;
      apiProtocol.SelectedItem = apiProtocol.Items.Cast<ApiChoice>().FirstOrDefault(p => p.Id == value.Protocol);
      UpdateApiTemperatureLimit();
      apiPreset.SelectedIndex = 0; apiCatalog = null; ClearApiModelItems(); apiModel.Text = value.Model;
      apiTimeout.Value = Math.Max(10, Math.Min(3600, value.TimeoutSeconds ?? TranslationOptions.TimeoutSeconds));
      apiShowButtons.Checked = value.ShowButtons ?? TranslationOptions.ShowButtons;
      if (displayedApiMode) SetParallelControls(value.ParallelRequests ?? TranslationOptions.ParallelRequests, value.MinimumChunkCharacters ?? TranslationOptions.MinimumChunkCharacters);
      apiMaxTokens.Value = Math.Max(0, Math.Min(2000000, value.MaxOutputTokens));
      apiTokenParameter.SelectedItem = value.TokenLimitParameter;
      apiUseTemperature.Checked = value.Temperature.HasValue;
      var temperature = value.Temperature ?? 0;
      if (double.IsNaN(temperature) || double.IsInfinity(temperature)) { temperature = 0; apiUseTemperature.Checked = false; }
      apiTemperature.Value = (decimal)Math.Max(0, Math.Min((double)apiTemperature.Maximum, temperature));
      apiAuthHeader.Text = value.AuthHeader; apiAuthPrefix.Text = value.AuthPrefix;
      apiParameters.Text = value.AdditionalParametersJson; apiHeaders.Text = value.AdditionalHeadersJson;
      apiProxyMode.SelectedItem = apiProxyMode.Items.Cast<ApiChoice>().FirstOrDefault(p => p.Id == (value.ProxyMode ?? "system"));
      // Keep an unknown persisted mode visible and reject it on save rather than silently changing the route.
      if (apiProxyMode.SelectedItem == null) {
        var unknown = new ApiChoice { Id = value.ProxyMode, Title = value.ProxyMode };
        apiProxyMode.Items.Add(unknown); apiProxyMode.SelectedItem = unknown;
      }
      apiProxyAddress.Text = value.ProxyAddress; apiProxyUsername.Text = value.ProxyUsername; apiProxyPassword.Text = value.ProxyPassword;
      apiProxyProtocol.SelectedItem = apiProxyProtocol.Items.Cast<ApiChoice>().FirstOrDefault(p => p.Id == ProxyAddressScheme()) ?? apiProxyProtocol.Items[0];
      apiProxyUseDefaultCredentials.Checked = value.ProxyUseDefaultCredentials;
      apiProxyUsernameEdited = apiProxyPasswordEdited = false;
      apiKeyEdited = apiHeadersEdited = false;
      apiRemove.Enabled = activeApiDraft != null;
      foreach (var control in new Control[] { apiName, apiEndpoint, apiKey, apiProtocol, apiPreset, apiModel, apiRefresh, apiTest }) control.Enabled = activeApiDraft != null;
      apiProxyMode.Enabled = activeApiDraft != null; UpdateApiProxyControls();
      UpdateApiEfforts(value.ReasoningEffortModel == value.Model &&
        value.ReasoningEffortCatalogKey == SettingsDiscoveryCache.ApiModelKey(value) ? value.ReasoningEffort : "");
      apiStatus.Text = apiConfigurationError ?? value.CredentialError ?? "Укажите адрес и модель. Затем запросите модели или проверьте подключение.";
      updatingApi = false;
    }

    private void CaptureApiControls(ApiConnection value)
    {
      if (value == null || apiName == null) return;
      value.Name = apiName.Text.Trim(); if (value.Name.Length == 0) value.Name = "API";
      value.Endpoint = apiEndpoint.Text.Trim(); value.Protocol = (apiProtocol.SelectedItem as ApiChoice)?.Id ?? "chat-completions";
      value.Model = apiModel.SelectedItem is CliModel model ? model.Id : apiModel.Text.Trim();
      // Until a catalog has arrived, this selector is intentionally hidden.
      // Preserve the saved choice and its proof of model/catalog identity;
      // failed or pending discovery must never erase another profile on Save.
      if (apiCatalog != null)
        value.ReasoningEffort = apiEffort.Enabled && apiEffort.SelectedItem is ApiEffortChoice effort
          ? effort.Id : "";
      value.MaxOutputTokens = (int)apiMaxTokens.Value;
      value.TimeoutSeconds = (int)apiTimeout.Value; value.ShowButtons = apiShowButtons.Checked;
      if (displayedApiMode) {
        value.ParallelRequests = (int)translationParallelRequests.Value;
        value.MinimumChunkCharacters = (int)translationMinimumChunk.Value;
      }
      value.TokenLimitParameter = (string)apiTokenParameter.SelectedItem ?? "max_tokens";
      value.Temperature = apiUseTemperature.Checked ? (double?)apiTemperature.Value : null;
      value.AuthHeader = apiAuthHeader.Text.Trim(); value.AuthPrefix = apiAuthPrefix.Text;
      value.AdditionalParametersJson = apiParameters.Text.Trim();
      if (apiKeyEdited) value.ApiKey = apiKey.Text.Trim();
      if (apiHeadersEdited) value.AdditionalHeadersJson = apiHeaders.Text.Trim();
      value.ProxyMode = (apiProxyMode.SelectedItem as ApiChoice)?.Id ?? "system";
      value.ProxyAddress = apiProxyAddress.Text.Trim();
      if (value.ProxyAddress.Length != 0 && value.ProxyAddress.IndexOf("://", StringComparison.Ordinal) < 0)
        value.ProxyAddress = ((apiProxyProtocol.SelectedItem as ApiChoice)?.Id ?? "http") + "://" + value.ProxyAddress;
      value.ProxyUseDefaultCredentials = apiProxyUseDefaultCredentials.Checked;
      if (apiProxyUsernameEdited) value.ProxyUsername = apiProxyUsername.Text;
      if (apiProxyPasswordEdited) value.ProxyPassword = apiProxyPassword.Text;
      if (apiCatalog != null) {
        value.ReasoningEffortModel = value.ReasoningEffort.Length == 0 ? "" : value.Model;
        value.ReasoningEffortCatalogKey = value.ReasoningEffort.Length == 0 ? "" : SettingsDiscoveryCache.ApiModelKey(value);
      }
    }

    private void ReadApiSettings(TranslationOptions options)
    {
      if (connectionMode == null) return;
      options.ConnectionMode = IsApiMode ? "api" : "cli";
      options.ApiConfigurationError = apiConfigurationError;
      options.ApiConnections = apiDrafts.Select(c => c.Copy()).ToList();
      if (activeApiDraft != null || apiConfigurationError == null)
        options.SelectedApiConnectionId = activeApiDraft?.Id ?? "";
      CaptureApiControls(options.ApiConnections.FirstOrDefault(c => c.Id == options.SelectedApiConnectionId));
      if (IsApiMode) {
        options.TimeoutSeconds = (int)apiTimeout.Value; options.ShowButtons = apiShowButtons.Checked;
        options.ParallelRequests = (int)translationParallelRequests.Value; options.MinimumChunkCharacters = (int)translationMinimumChunk.Value;
      }
    }

    private void ApplyApiPreset()
    {
      if (updatingApi || !(apiPreset.SelectedItem is ApiChoice preset) || preset.Id == "custom") return;
      CancelApiDiscovery(); updatingApi = true;
      activeApiDraft?.ClearCredentials(false);
      apiName.Text = preset.Title; apiEndpoint.Text = preset.Endpoint;
      apiProtocol.SelectedItem = apiProtocol.Items.Cast<ApiChoice>().First(p => p.Id == preset.Protocol);
      apiTokenParameter.SelectedItem = preset.TokenParameter ?? "max_tokens";
      apiKey.Text = ""; apiCatalog = null; ClearApiModelItems(); apiModel.Text = "";
      UpdateApiEfforts(""); apiHeaders.Text = "{}"; apiParameters.Text = "{}"; apiAuthHeader.Text = apiAuthPrefix.Text = "";
      apiUseTemperature.Checked = false; apiMaxTokens.Value = preset.Protocol == "anthropic" ? 8192 : 0;
      apiKeyEdited = apiHeadersEdited = true; updatingApi = false;
      apiStatus.Text = "Настройки сервиса подставлены. Загружаем доступные модели…";
      QueueApiDiscovery();
    }

    private void ApiAddressChanged()
    {
      if (updatingApi) return;
      // Model/effort/output settings affect a translation test, not GET /models.
      // Let catalog discovery complete while the user edits these controls.
      if (apiQueryIsTranslation) CancelApiDiscovery();
      if (apiCancellation == null) apiStatus.Text = "Параметры перевода изменены. Можно проверить подключение.";
    }

    private void ApiCatalogIdentityChanged(bool resetEffort = false)
    {
      if (updatingApi) return;
      CancelApiDiscovery();
      var selected = apiModel?.SelectedItem is CliModel model ? model.Id : apiModel?.Text;
      updatingApi = true;
      try { apiCatalog = null; if (apiModel != null) { ClearApiModelItems(); apiModel.Text = selected ?? ""; } }
      finally { updatingApi = false; }
      UpdateApiEfforts(resetEffort ? "" : null);
      apiStatus.Text = "Настройки подключения изменены. Обновите список моделей или проверьте подключение.";
    }

    private void UpdateApiTemperatureLimit()
    {
      apiTemperature.Maximum = (apiProtocol.SelectedItem as ApiChoice)?.Id == "anthropic" ? 1 : 2;
    }

    private async Task QueryApiAsync(bool translate, bool refresh = false)
    {
      if (activeApiDraft == null) return;
      var reopenModels = openApiModelsWhenReady;
      CancelApiDiscovery(); openApiModelsWhenReady = reopenModels; var generation = apiGeneration;
      var cancellation = new CancellationTokenSource(); apiCancellation = cancellation; apiQueryIsTranslation = translate;
      var snapshot = activeApiDraft.Copy(); CaptureApiControls(snapshot);
      apiRefresh.Enabled = apiTest.Enabled = false;
      apiStatus.Text = translate ? "Проверяем перевод короткого примера…" : "Запрашиваем модели API…";
      try {
        var translator = new ApiTranslator();
        if (translate) {
          await translator.TranslateAsync("# Connection test\nThe connection is working.", snapshot, (int)apiTimeout.Value, cancellation.Token);
          if (!cancellation.IsCancellationRequested && generation == apiGeneration && !IsDisposed) apiStatus.Text = "Короткий тест перевода выполнен: модель вернула ответ.";
        }
        else {
          var timeout = Math.Min(30, (int)apiTimeout.Value);
          var catalog = await Task.Run(() => settingsDiscovery.LoadApiModelsAsync(snapshot, timeout, refresh, cancellation.Token), cancellation.Token);
          if (cancellation.IsCancellationRequested || generation != apiGeneration || IsDisposed) return;
          // The model field stays editable during discovery. Restore its current
          // value, rather than reverting a late edit to the request snapshot.
          var selectedModel = apiModel.SelectedItem is CliModel model ? model.Id : apiModel.Text.Trim();
          FillApiModels(catalog, selectedModel);
          apiStatus.Text = "Моделей получено: " + catalog.Models.Count + ". Если нужной нет в списке, введите её ID.";
          if (openApiModelsWhenReady && catalog.Models.Count > 0 && apiModel.ContainsFocus) apiModel.DroppedDown = true;
          openApiModelsWhenReady = false;
        }
      }
      catch (OperationCanceledException) { }
      catch (Exception error) {
        if (generation == apiGeneration && !IsDisposed && !cancellation.IsCancellationRequested) apiStatus.Text = error.Message;
        openApiModelsWhenReady = false;
      }
      finally {
        if (ReferenceEquals(apiCancellation, cancellation)) { apiCancellation = null; apiQueryIsTranslation = false; if (!IsDisposed) apiRefresh.Enabled = apiTest.Enabled = true; }
        cancellation.Dispose();
      }
    }

    private void FillApiModels(CliModelCatalog catalog, string selectedModel)
    {
      var wasUpdating = updatingApi;
      updatingApi = true;
      try {
        apiCatalog = catalog;
        openApiModelsWhenReady |= apiModel.DroppedDown;
        ClearApiModelItems();
        apiModel.BeginUpdate();
        try {
        apiModel.Items.AddRange(catalog.Models.OrderBy(m => CliModel.CleanDisplayName(string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name), StringComparer.OrdinalIgnoreCase)
          .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToArray());
        apiModel.SelectedItem = catalog.Models.FirstOrDefault(m => m.Id == selectedModel);
        if (apiModel.SelectedItem == null) apiModel.Text = selectedModel;
        }
        finally { apiModel.EndUpdate(); }
        var selectedEffort = apiEffort.Enabled && apiEffort.SelectedItem is ApiEffortChoice selected ? selected.Id
          : activeApiDraft?.ReasoningEffortModel == selectedModel &&
            activeApiDraft?.ReasoningEffortCatalogKey == SettingsDiscoveryCache.ApiModelKey(activeApiDraft) ? activeApiDraft?.ReasoningEffort : "";
        UpdateApiEfforts(selectedEffort);
      }
      finally { updatingApi = wasUpdating; }
    }

    private ApiConnection CurrentApiChoice()
    {
      var choice = activeApiDraft?.Copy() ?? new ApiConnection();
      choice.Endpoint = apiEndpoint?.Text ?? choice.Endpoint;
      choice.Protocol = (apiProtocol?.SelectedItem as ApiChoice)?.Id ?? choice.Protocol;
      choice.Model = apiModel?.SelectedItem is CliModel model ? model.Id : apiModel?.Text?.Trim() ?? choice.Model;
      return choice;
    }

    private void UpdateApiEfforts(string preferred = null)
    {
      if (apiEffort == null || apiModel == null) return;
      var connection = CurrentApiChoice();
      var model = apiModel.SelectedItem as CliModel ?? apiCatalog?.Models.FirstOrDefault(item => item.Id == connection.Model);
      var choices = ApiEffortOptions.For(connection, model);
      var selected = preferred ?? (apiEffort.SelectedItem is ApiEffortChoice old ? old.Id : "");
      var wasUpdating = updatingApi;
      updatingApi = true; apiEffort.BeginUpdate();
      try {
        apiEffort.Items.Clear();
        var fallback = new ApiEffortChoice { Id = "", Title = "По умолчанию API" };
        apiEffort.Items.Add(fallback);
        foreach (var level in choices.Values) apiEffort.Items.Add(new ApiEffortChoice { Id = level, Title = level == "xhigh" ? "Extra High (xhigh)" : level });
        var known = apiEffort.Items.Cast<ApiEffortChoice>().FirstOrDefault(item => item.Id == selected);
        apiEffort.SelectedItem = known ?? fallback;
        var available = activeApiDraft != null && choices.Values.Length > 0;
        apiEffort.Enabled = available;
        apiEffort.Visible = available;
        apiEffortLabel.Visible = available;
        ReflowModelOptions(apiEffort);
      }
      finally { apiEffort.EndUpdate(); updatingApi = wasUpdating; }
      var note = "Если effort не указан, поле " + choices.WireField + " не отправляется. " + choices.Note;
      if (!string.IsNullOrEmpty(selected) && !choices.Values.Contains(selected, StringComparer.OrdinalIgnoreCase))
        note += " Сохранённое значение «" + selected + "» не подтверждено и не будет использовано после сохранения.";
      apiEffort.AccessibleDescription = note;
      settingsToolTips.SetToolTip(apiEffort, note);
      if (!updatingApi && apiStatus != null && apiCancellation == null) apiStatus.Text = note;
    }

    private void CancelApiDiscovery()
    {
      apiGeneration++; apiDiscoveryQueued = false; openApiModelsWhenReady = false; apiQueryIsTranslation = false;
      var cancellation = apiCancellation; apiCancellation = null; cancellation?.Cancel();
      if (apiRefresh != null && !IsDisposed) { apiRefresh.Enabled = apiTest.Enabled = activeApiDraft != null; }
    }

    private void QueueApiDiscovery()
    {
      if (!settingsShown || IsDisposed || Disposing || !IsApiMode ||
          settingsTabs.SelectedTab != translationPage || activeApiDraft == null ||
          string.IsNullOrWhiteSpace(apiEndpoint.Text) || apiDiscoveryQueued || apiCancellation != null) return;
      apiDiscoveryQueued = true;
      BeginInvoke(new Action(async () => {
        if (!apiDiscoveryQueued) return;
        apiDiscoveryQueued = false;
        if (!IsDisposed && !Disposing && IsApiMode && settingsTabs.SelectedTab == translationPage && activeApiDraft != null)
          await QueryApiAsync(false);
      }));
    }
  }
}
