using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using AnotherMarkdown.Entities;
using AnotherMarkdown.Forms;
using AnotherMarkdown.Translation;
using AnotherMarkdown.Editing;
using Kbg.NppPluginNET.PluginInfrastructure;
using PanelCommon;

namespace AnotherMarkdown
{
  public partial class MarkdownPanelController : IDisposable
  {
    private MarkdownPreviewForm PreviewForm
    {
      get {
        if (_previewForm == null) {
          lock (_lock) {
            if (_previewForm == null) {
              _previewForm = MarkdownPreviewForm.Create(_settings);
              _previewForm.EditSource = HandleSourceEdit;
              _previewForm.LeaveSourceEditor = FinishSourceFormatting;
              _previewForm.NavigationChanged = HandleEditorNavigation;
              _previewForm.OnEvent.DocumentChanged += (_, e) => DocumentChanged(e);
              _previewForm.OnEvent.FirstLineChanged += (_, e) => FirstLineChanged(e);
              _previewForm.OnEvent.PasteImage += (_, e) => PasteImage(e);
              _previewForm.OnEvent.Navigate += (_, e) => OpenFile(e);
              _previewForm.DockClosed += (_, e) => { _panelAutoHidden = false; SetPanelVisibleState(false); RememberPreviewEnabled(false); };
              _previewForm.TranslationSettingsRequested += (_, e) => EditSettings();
            }
          }
        }
        return _previewForm;
      }
    }
    public MarkdownPanelController()
    {
      _renderQueue = new PreviewRenderQueue(RenderCurrentBufferAsync);
      AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
      scintillaGatewayFactory = PluginBase.GetGatewayFactory();
      _nppGateway = new NotepadPPGateway();
      SetIniFilePath();
      _settings = LoadSettingsFromIni();
    }

    private Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
    {
      var di = new DirectoryInfo(Path.Combine(PluginUtils.GetPluginDirectory(), "lib"));

      var modulename = args.Name.Split(',')[0];

      var module = di.GetFiles().FirstOrDefault(i => i.Name == modulename + ".dll");
      if (module != null) {
        return Assembly.LoadFrom(module.FullName);
      }
      return null;
    }

    private Settings LoadSettingsFromIni()
    {
      Settings settings = new Settings();
      _previewEnabled = Win32.GetPrivateProfileInt("Options", "PreviewEnabled", 0, _iniFilePath) != 0;
      // New independent switches replace the old, mutually exclusive line-follow modes.
      settings.SyncViewWithCaretPosition = (Win32.GetPrivateProfileInt("Options", "SyncCaretAndSelection", 1, _iniFilePath) != 0);
      settings.SyncViewWithFirstVisibleLine = (Win32.GetPrivateProfileInt("Options", "SyncScrollBothWays", 1, _iniFilePath) != 0);

      settings.EnabledMarkdownPlugins = Win32.ReadIniValue("Options", "EnabledMarkdownPlugins", _iniFilePath, "tasks-list;attrs;qrcode;pano360")
        .Split(';')
        .Select(li => li.Trim())
        .ToArray();

      settings.PreProcessorCommandFilename = Win32.ReadIniValue("Options", "PreProcessorExe", _iniFilePath, "");
      settings.PreProcessorArguments = Win32.ReadIniValue("Options", "PreProcessorArguments", _iniFilePath, "");
      settings.PostProcessorCommandFilename = Win32.ReadIniValue("Options", "PostProcessorExe", _iniFilePath, "");
      settings.PostProcessorArguments = Win32.ReadIniValue("Options", "PostProcessorArguments", _iniFilePath, "");
      settings.AssetsPath = Win32.ReadIniValue("Options", "AssetsPath", _iniFilePath, "");
      settings.CssFileName = Win32.ReadIniValue("Options", "CssFileName", _iniFilePath, "style.css");
      settings.CssDarkModeFileName = Win32.ReadIniValue("Options", "CssDarkModeFileName", _iniFilePath, "style-dark.css");
      settings.ZoomLevel = Win32.GetPrivateProfileInt("Options", "ZoomLevel", Settings.DefaultZoomLevel, _iniFilePath);
      settings.ShowToolbar = PluginUtils.ReadIniBool("Options", "ShowToolbar", _iniFilePath);
      settings.ShowStatusbar = PluginUtils.ReadIniBool("Options", "ShowStatusbar", _iniFilePath);
      settings.PreserveEditorLineBreaks = Win32.GetPrivateProfileInt("Options", "PreserveEditorLineBreaks", 0, _iniFilePath) != 0;
      settings.Theme = Win32.ReadIniValue("Options", "Theme", _iniFilePath, "auto");
      settings.IsDarkModeEnabled = settings.ResolveDarkMode(IsDarkModeEnabled());
      settings.Translation.Executable = Win32.ReadIniValue("Translation", "Executable", _iniFilePath, settings.Translation.Executable);
      settings.Translation.Model = Win32.ReadIniValue("Translation", "Model", _iniFilePath, settings.Translation.Model);
      settings.Translation.ReasoningEffort = Win32.ReadIniValue("Translation", "ReasoningEffort", _iniFilePath, "");
      settings.Translation.Arguments = Win32.ReadIniValue("Translation", "Arguments", _iniFilePath, settings.Translation.Arguments);
      settings.Translation.ProviderId = Win32.ReadIniValue("Translation", "ProviderId", _iniFilePath, CliProfiles.Identify(settings.Translation.Executable));
      settings.Translation.UseDefaultModel = Win32.ReadIniValue("Translation", "UseDefaultModel", _iniFilePath, string.IsNullOrWhiteSpace(settings.Translation.Model).ToString()).Equals("True", StringComparison.OrdinalIgnoreCase);
      settings.Translation.UseManualModel = Win32.ReadIniValue("Translation", "UseManualModel", _iniFilePath, "False").Equals("True", StringComparison.OrdinalIgnoreCase);
      settings.Translation.UseCustomArguments = Win32.ReadIniValue("Translation", "UseCustomArguments", _iniFilePath, (settings.Translation.Arguments != TranslationOptions.DefaultArguments).ToString()).Equals("True", StringComparison.OrdinalIgnoreCase);
      settings.Translation.OutputFormat = Win32.ReadIniValue("Translation", "OutputFormat", _iniFilePath, CliProfiles.Get(settings.Translation.ProviderId).OutputFormat);
      settings.Translation.TimeoutSeconds = Math.Max(10, Math.Min(3600, Win32.GetPrivateProfileInt("Translation", "TimeoutSeconds", 300, _iniFilePath)));
      settings.Translation.ParallelRequests = Math.Max(1, Math.Min(8, Win32.GetPrivateProfileInt("Translation", "ParallelRequests", 1, _iniFilePath)));
      var minimumChunkText = Win32.ReadIniValue("Translation", "MinimumChunkCharacters", _iniFilePath, "2000");
      settings.Translation.MinimumChunkCharacters = int.TryParse(minimumChunkText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var minimumChunk)
        ? Math.Max(0, Math.Min(1000000, minimumChunk)) : 2000;
      settings.Translation.ShowButtons = Win32.ReadIniValue("Translation", "ShowButtons", _iniFilePath, "True").Equals("True", StringComparison.OrdinalIgnoreCase);
      settings.Translation.ConnectionMode = Win32.ReadIniValue("Translation", "ConnectionMode", _iniFilePath, "cli");
      settings.Translation.SelectedApiConnectionId = Win32.ReadIniValue("Translation", "ApiConnectionId", _iniFilePath, "");
      settings.Translation.LoadApiConnections(_iniFilePath + ".api.json");
      settings.Translation.LoadCliConnections(_iniFilePath + ".cli.json");
      // CLI-mode INI values remain authoritative for the active legacy selection.
      // API-mode common controls must not overwrite an already saved CLI draft.
      if (!settings.Translation.UseApi || !settings.Translation.CliConnections.Any(c => CliConnectionSettings.SameIdentity(
        c.ProviderId, c.Executable, settings.Translation.ProviderId, settings.Translation.Executable)))
        CliConnectionSettings.Upsert(settings.Translation.CliConnections, CliConnectionSettings.Capture(settings.Translation));
      return settings;
    }

    public void OnNotification(ScNotification notification)
    {
      try {
        NotificationHandler(notification);
      }
      catch (Exception) { }
    }

    private void NotificationHandler(ScNotification notification)
    {
      if (_disposedValue) {
        return;
      }

      switch (notification.Header.Code) {
        case (uint) SciMsg.SCN_UPDATEUI: {
          if (!_applyingNavigation && _isPanelVisible && (_settings.SyncViewWithCaretPosition || _settings.SyncViewWithFirstVisibleLine)) {
            lock (_syncViewLock) {
              _syncViewPending = true;
              if (_syncViewTask == null || (_syncViewTask.IsCompleted || _syncViewTask.IsFaulted)) {
                _syncViewTask = SyncViewTask();
              }
            }
          }
          break;
        }
        case (uint) NppMsg.NPPN_FILEBEFORECLOSE: {
          if (editingBuffer == notification.Header.IdFrom) FinishSourceFormatting();
          break;
        }
        case (uint) NppMsg.NPPN_FILECLOSED: {
          if (editingBuffer != IntPtr.Zero && editingBuffer == notification.Header.IdFrom) DetachFormatView();
          editorSources.Remove(notification.Header.IdFrom);
          _fileVisits.Remove(notification.Header.IdFrom);
          UpdateHistoryButtons();
          break;
        }
        case (uint) NppMsg.NPPN_FILESAVED:
        case (uint) NppMsg.NPPN_FILERENAMED: {
          if (notification.Header.Code == (uint)NppMsg.NPPN_FILESAVED) RecordSourceSave(notification.Header.IdFrom);
          UpdateAutomaticPanel();
          RenderMarkdown(force: true);
          break;
        }
        case (uint) NppMsg.NPPN_READONLYCHANGED:
        case (uint) SciMsg.SCN_SAVEPOINTLEFT:
        case (uint) SciMsg.SCN_SAVEPOINTREACHED: {
          RenderMarkdown();
          break;
        }
        case (uint) NppMsg.NPPN_BEFORESHUTDOWN: {
          FinishSourceFormatting();
          _shuttingDown = true;
          break;
        }
        case (uint) NppMsg.NPPN_CANCELSHUTDOWN: {
          _shuttingDown = false;
          if (_previewEnabled && !_isPanelVisible) { _panelAutoHidden = true; UpdateAutomaticPanel(); }
          break;
        }
        case (uint) NppMsg.NPPN_READY: {
          _startupReady = true;
          _panelAutoHidden = _previewEnabled;
          UpdateAutomaticPanel();
          _fileVisits.Record(CurrentBufferId());
          UpdateHistoryButtons();
          break;
        }
        case (uint) NppMsg.NPPN_BUFFERACTIVATED: {
          var buffer = CurrentBufferId();
          if (editingBuffer != IntPtr.Zero && editingBuffer != buffer) FinishSourceFormatting();
          if (editorSources.TryGetValue(buffer, out var session)) session.Activated(scintillaGatewayFactory());
          ClearSourceCaret();
          _fileVisits.Record(buffer);
          UpdateAutomaticPanel();
          UpdateHistoryButtons();
          RenderMarkdown(force: true);
          break;
        }
        case (uint) (NppMsg.NPPN_FIRST + 27): {
          _settings.IsDarkModeEnabled = _settings.ResolveDarkMode(IsDarkModeEnabled());
          if (_isPanelVisible) {
            PreviewForm.UpdateSettings(_settings);
            RenderMarkdown(force: true);
          }
          break;
        }
        case (uint) SciMsg.SCN_MODIFIED: {
          const int textChanged = (int)SciMsg.SC_MOD_INSERTTEXT | (int)SciMsg.SC_MOD_DELETETEXT;
          if ((notification.ModificationType & textChanged) != 0) {
            StopViewportScroll();
            RenderMarkdown();
          }
          break;
        }
      }
    }

    private async Task SyncViewTask()
    {
      while(_isPanelVisible) {
        await Task.Delay(50);
        if (_disposedValue || !_isPanelVisible) {
          return;
        }
        if ((_settings.SyncViewWithFirstVisibleLine || _settings.SyncViewWithCaretPosition) == false) {
          return;
        }

        lock (_syncViewLock) {
          if (!_syncViewPending) {
            return;
          }
          _syncViewPending = false;
        }

        await PublishNavigationAsync(false);
      }
    }

    public void InitCommandMenu()
    {
      PluginBase.SetCommand(0, "Показать / скрыть &Markdown", TogglePanelVisible);
      PluginBase.SetCommand(1, "---", null);
      PluginBase.SetCommand(2, "Синхронизировать &курсор и выделение", SyncViewWithCaretClicked, _settings.SyncViewWithCaretPosition);
      PluginBase.SetCommand(3, "Синхронизировать &прокрутку", SyncViewWithFirstVisibleLineClicked, _settings.SyncViewWithFirstVisibleLine);
      PluginBase.SetCommand(4, "---", null);
      PluginBase.SetCommand(5, "&Настройки", () => EditSettings());
      PluginBase.SetCommand(6, "&Справка", ShowHelp);
      PluginBase.SetCommand(7, "&О плагине", ShowAboutDialog);
      PluginBase.SetCommand(8, "Назад по файлам", () => NavigateFileHistory(-1));
      PluginBase.SetCommand(9, "Вперёд по файлам", () => NavigateFileHistory(1));
      _myDlgId = 0;
    }

    private void EditSettings(bool translationTab = false)
    {
      using (var settingsForm = new SettingsForm(_settings)) {
        if (translationTab) settingsForm.SelectTranslationTab();
        if (settingsForm.ShowDialog(new PluginWindowOwner(PluginBase.nppData._nppHandle)) != DialogResult.OK) return;
        _settings.AssetsPath = settingsForm.AssetsPath;
        _settings.CssFileName = settingsForm.CssFileName;
        _settings.CssDarkModeFileName = settingsForm.CssDarkModeFileName;
        _settings.Theme = settingsForm.Theme;
        _settings.PreserveEditorLineBreaks = settingsForm.PreserveEditorLineBreaks;
        _settings.ZoomLevel = settingsForm.ZoomLevel;
        _settings.ShowToolbar = settingsForm.ShowToolbar;
        _settings.ShowStatusbar = settingsForm.ShowStatusbar;
        _settings.EnabledMarkdownPlugins = settingsForm.AllowedMarkdownPlugins;
        _settings.Translation = settingsForm.TranslationOptions;

        _settings.IsDarkModeEnabled = _settings.ResolveDarkMode(IsDarkModeEnabled());
        SaveSettings(true);
        //Update Preview
        if (_isPanelVisible) {
          PreviewForm.UpdateSettings(_settings);
          RenderMarkdown(force: true);
        }
      }
    }

    private sealed class PluginWindowOwner : IWin32Window
    {
      public PluginWindowOwner(IntPtr handle) { Handle = handle; }
      public IntPtr Handle { get; }
    }

    private Timer _linkRevealTimer;

    private void OpenFile(NavigateToEvent args)
    {
      _linkRevealTimer?.Dispose(); _linkRevealTimer = null;
      if (!File.Exists(args.Filename)) {
        return;
      }
      var opened = Win32.SendMessage(PluginBase.nppData._nppHandle, (uint) NppMsg.NPPM_DOOPEN, 0, args.Filename);
      if (opened == IntPtr.Zero || args.Line <= 0 || !string.Equals(Path.GetFullPath(_nppGateway.GetCurrentFilePath()),
          Path.GetFullPath(args.Filename), StringComparison.OrdinalIgnoreCase)) return;
      var gateway = scintillaGatewayFactory();
      var line = Math.Max(0, Math.Min(args.Line - 1, gateway.GetLineCount() - 1));
      gateway.EnsureVisible(line);
      gateway.GotoPos(gateway.FindColumn(line, Math.Max(0, args.Column - 1)));
      // After a dock hides or a long line enables the horizontal scrollbar,
      // ScrollCaret can leave the destination just outside the resized viewport.
      gateway.ScrollCaret();
      gateway.VerticalCentreCaret();
      if (!IsMarkdownFile(args.Filename)) gateway.GrabFocus();
      var buffer = CurrentBufferId();
      var position = gateway.GetCurrentPos();
      // Opening a buffer and hiding the dock can queue wrapping/layout work.
      // Reveal once more after that work, without moving a user's changed caret.
      var reveal = new Timer { Interval = 60 };
      _linkRevealTimer = reveal;
      reveal.Tick += (_, __) => {
        reveal.Stop(); reveal.Dispose();
        if (ReferenceEquals(_linkRevealTimer, reveal)) _linkRevealTimer = null;
        if (_disposedValue || CurrentBufferId() != buffer) return;
        var current = scintillaGatewayFactory();
        if (current.GetCurrentPos() == position && current.GetAnchor() == position) {
          current.ScrollCaret(); current.VerticalCentreCaret();
        }
      };
      reveal.Start();
    }

    private void PasteImage(PasteImageEvent args)
    {
      if (_previewForm?.IsTranslationPreview == true) return;
      var path = _nppGateway.GetCurrentFilePath();
      var rootDir = Path.GetDirectoryName(path);
      var targetDir = Path.Combine(rootDir, Path.GetDirectoryName(args.Filename));

      if (!Directory.Exists(targetDir)) {
        Directory.CreateDirectory(targetDir);
      }

      var extension = Path.GetExtension(args.Filename).ToLower().Substring(1);
      var sameFiles = Directory.GetFiles(targetDir, $"*.{extension}", SearchOption.TopDirectoryOnly);
      string filename = null;

      if (sameFiles.Length != 0) {
        using (var md5 = MD5.Create()) {
          var hash2 = string.Join("", md5.ComputeHash(args.Content).Select(li => $"{li}:X2"));
          foreach (var file in sameFiles) {
            var hash1 = string.Join("", md5.ComputeHash(File.ReadAllBytes(file)).Select(li => $"{li}:X2"));
            if (hash1 == hash2) {
              filename = file;
              break;
            }
          }
        }
      }

      if (filename == null) {
        var index = 10;
        while (true) {
          var fname = $"{index:D3}";
          if (Directory.GetFiles(targetDir, $"{fname}.*", SearchOption.TopDirectoryOnly).Length == 0) {
            break;
          }
          index += 5;
        }

        filename = (targetDir + $"/{index:D3}.{extension}").Replace("\\", "/");
        File.WriteAllBytes(filename, args.Content);
      }

      Uri rootUri = new Uri(rootDir + Path.DirectorySeparatorChar);
      Uri fileUri = new Uri(filename);
      var relativePath = rootUri.MakeRelativeUri(fileUri).ToString();

      var scintillaGateway = scintillaGatewayFactory();
      var pos = scintillaGateway.GetCurrentPos();
      scintillaGateway.InsertText(pos, $"![](./{relativePath})\r\n");
    }

    private void FirstLineChanged(FirstLineChangedEvent args)
    {
      if (!_settings.SyncViewWithFirstVisibleLine || _previewForm?.IsTranslationPreview == true) return;
      var scintillaGateway = scintillaGatewayFactory();
      var visibleLine = scintillaGateway.GetFirstVisibleLine();
      var docLine = scintillaGateway.DocLineFromVisible(visibleLine);
      var newVisibleLine = scintillaGateway.VisibleFromDocLine(args.Line);

      if (visibleLine != newVisibleLine) {
        scintillaGateway.LineScroll(0, newVisibleLine - visibleLine);
      }
    }

    private void DocumentChanged(DocumentChangedEvent args)
    {
      if (_previewForm?.IsTranslationPreview == true || args.Content == null ||
          !string.Equals(args.DocumentPath, _nppGateway.GetCurrentFilePath(), StringComparison.OrdinalIgnoreCase)) return;
      var gateway = scintillaGatewayFactory();
      if (gateway.GetReadOnly()) return;
      var current = gateway.GetText(gateway.GetLength() + 1);
      var edit = SourceTextEdit.Between(current, args.Content, SourceTextEdit.EncodingFor(gateway.GetCodePage()));
      edit?.Apply(gateway);
    }

    private void ShowHelp()
    {
      var currentPluginPath = PluginUtils.GetPluginDirectory();
      var helpFile = Path.Combine(currentPluginPath, "README.md");
      Win32.SendMessage(PluginBase.nppData._nppHandle, (uint) NppMsg.NPPM_DOOPEN, 0, helpFile);
      if (!_isPanelVisible) {
        TogglePanelVisible();
      }
      RenderMarkdown(force: true);
    }

    private void SetIniFilePath()
    {
      StringBuilder sbIniFilePath = new StringBuilder(Win32.MAX_PATH);
      Win32.SendMessage(PluginBase.nppData._nppHandle, (uint) NppMsg.NPPM_GETPLUGINSCONFIGDIR, Win32.MAX_PATH, sbIniFilePath);
      _iniFilePath = sbIniFilePath.ToString();
      if (!Directory.Exists(_iniFilePath)) {
        Directory.CreateDirectory(_iniFilePath);
      }

      _iniFilePath = Path.Combine(_iniFilePath, Main.ModuleName + ".ini");
      SettingsDiscoveryCache.Shared.ConfigureModelPersistence(_iniFilePath + ".models.json");
    }

    private void SyncViewWithCaretClicked()
    {
      SetSyncViewWithCaretPosition(!_settings.SyncViewWithCaretPosition);
      RenderMarkdown(force: true);
    }

    private void SyncViewWithFirstVisibleLineClicked()
    {
      SetSyncViewWithFirstVisibleLine(!_settings.SyncViewWithFirstVisibleLine);
      RenderMarkdown(force: true);
    }

    private void SetSyncViewWithCaretPosition(bool enabled)
    {
      if (_settings.SyncViewWithCaretPosition == enabled) {
        return;
      }
      _settings.SyncViewWithCaretPosition = enabled;
      _sourceCaret?.Refresh();

      Win32.CheckMenuItem(Win32.GetMenu(PluginBase.nppData._nppHandle), PluginBase._funcItems.Items[2]._cmdID, Win32.MF_BYCOMMAND
        | (enabled ? Win32.MF_CHECKED : Win32.MF_UNCHECKED));
    }

    private void SetSyncViewWithFirstVisibleLine(bool enabled)
    {
      if (_settings.SyncViewWithFirstVisibleLine == enabled) {
        return;
      }
      _settings.SyncViewWithFirstVisibleLine = enabled;
      if (!enabled) StopViewportScroll();
      Win32.CheckMenuItem(Win32.GetMenu(PluginBase.nppData._nppHandle), PluginBase._funcItems.Items[3]._cmdID, Win32.MF_BYCOMMAND
        | (enabled ? Win32.MF_CHECKED : Win32.MF_UNCHECKED));
    }

    public void SetToolBarIcon()
    {
      var size = PluginIcon.ScaleForWindow(32, PluginBase.nppData._nppHandle);
      if (_toolbarIcon == null) {
        _toolbarIcon = PluginIcon.Create(size);
        _toolbarDarkIcon = PluginIcon.Create(size, true);
        using (var image = PluginIcon.Render(PluginIcon.ScaleForWindow(16, PluginBase.nppData._nppHandle))) _toolbarBitmap = image.GetHbitmap();
      }
      var icons = new toolbarIcons { hToolbarBmp = _toolbarBitmap, hToolbarIcon = _toolbarIcon.Handle, hToolbarIconDarkMode = _toolbarDarkIcon.Handle };
      var pointer = Marshal.AllocHGlobal(Marshal.SizeOf(icons));
      try {
        Marshal.StructureToPtr(icons, pointer, false);
        var result = Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_ADDTOOLBARICON_FORDARKMODE, PluginBase._funcItems.Items[_myDlgId]._cmdID, pointer);
        if (result == IntPtr.Zero) Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_ADDTOOLBARICON, PluginBase._funcItems.Items[_myDlgId]._cmdID, pointer);
      }
      finally { Marshal.FreeHGlobal(pointer); }
      RegisterHistoryButtons();
    }

    private void SetPanelVisibleState(bool visible)
    {
      _isPanelVisible = visible;
      if (!visible) {
        FinishSourceFormatting();
        _previewForm?.FinishTranslationEdit();
        _renderQueue.Clear();
        ClearSourceCaret();
      }
      Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_SETMENUITEMCHECK,
        PluginBase._funcItems.Items[_myDlgId]._cmdID, visible ? 1 : 0);
    }

    public void PluginCleanUp()
    {
      ClearSourceCaret();
      SaveSettings();
    }

    private bool SaveSettings(bool reportErrors = false)
    {
      try { SaveSettingsCore(reportErrors); return true; }
      catch (IOException error) {
        if (reportErrors) MessageBox.Show(new PluginWindowOwner(PluginBase.nppData._nppHandle), "Не удалось полностью сохранить настройки.\r\n\r\n" + error.Message + "\r\n\r\nПроверьте права на файл и каталог, а также атрибут «Только чтение». Новые значения применены в памяти; для сохранения после перезапуска повторите запись, когда доступ будет восстановлен.", "Настройки", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
      }
    }

    private void SaveSettingsCore(bool reportRecovery)
    {
      try { _settings.Translation.ValidateArgumentStorage(); }
      catch (ArgumentException error) { throw new IOException(error.Message); }
      var apiPath = _iniFilePath + ".api.json";
      var cliPath = _iniFilePath + ".cli.json";
      IOException apiError = null;
      IOException cliError = null;
      if (_settings.Translation.ApiConnections.Count > 0 || (_settings.Translation.ApiConfigurationError == null && File.Exists(apiPath))) {
        try {
          string backupWarning;
          var recovery = ApiConnectionStore.Save(apiPath, _settings.Translation.ApiConnections, _settings.Translation.ApiConfigurationError != null, out backupWarning);
          _settings.Translation.ApiConfigurationError = null;
          if (reportRecovery && recovery != null) MessageBox.Show(new PluginWindowOwner(PluginBase.nppData._nppHandle), "Предыдущий файл подключений сохранён для восстановления:\r\n" + recovery, "Настройки API", MessageBoxButtons.OK, MessageBoxIcon.Information);
          if (reportRecovery && backupWarning != null) MessageBox.Show(new PluginWindowOwner(PluginBase.nppData._nppHandle), backupWarning, "Настройки API", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (IOException) { apiError = new IOException("Не удалось сохранить подключения API в файл: " + apiPath + "."); }
      }
      if (_settings.Translation.CliConnections == null) _settings.Translation.CliConnections = new System.Collections.Generic.List<CliConnectionSettings>();
      if (!_settings.Translation.UseApi)
        CliConnectionSettings.Upsert(_settings.Translation.CliConnections, CliConnectionSettings.Capture(_settings.Translation));
      if (_settings.Translation.CliConfigurationError != null)
        cliError = new IOException("Не удалось прочитать прежние настройки CLI. Файл сохранён без изменений: " + cliPath + ".");
      else if (_settings.Translation.CliConnections.Count > 0 || File.Exists(cliPath)) {
        try { CliConnectionStore.Save(cliPath, _settings.Translation.CliConnections); }
        catch (IOException) { cliError = new IOException("Не удалось сохранить настройки CLI в файл: " + cliPath + "."); }
      }
      Win32.WriteIniValue("Options", "PreviewEnabled", _previewEnabled ? "1" : "0", _iniFilePath);
      Win32.WriteIniValue("Options", "SyncCaretAndSelection", _settings.SyncViewWithCaretPosition ? "1" : "0", _iniFilePath);
      Win32.WriteIniValue("Options", "SyncScrollBothWays", _settings.SyncViewWithFirstVisibleLine ? "1" : "0", _iniFilePath);
      Win32.WriteIniValue("Options", "EnabledMarkdownPlugins", string.Join(";", _settings.EnabledMarkdownPlugins), _iniFilePath);

      Win32.WriteIniValue("Options", "AssetsPath", _settings.AssetsPath, _iniFilePath);
      Win32.WriteIniValue("Options", "CssFileName", _settings.CssFileName, _iniFilePath);
      Win32.WriteIniValue("Options", "CssDarkModeFileName", _settings.CssDarkModeFileName, _iniFilePath);
      Win32.WriteIniValue("Options", "Theme", _settings.Theme, _iniFilePath);
      Win32.WriteIniValue("Options", "PreserveEditorLineBreaks", _settings.PreserveEditorLineBreaks ? "1" : "0", _iniFilePath);
      Win32.WriteIniValue("Options", "ZoomLevel", _settings.ZoomLevel.ToString(), _iniFilePath);
      Win32.WriteIniValue("Options", "ShowToolbar", _settings.ShowToolbar.ToString(), _iniFilePath);
      Win32.WriteIniValue("Options", "ShowStatusbar", _settings.ShowStatusbar.ToString(), _iniFilePath);
      if (cliError == null) {
        Win32.WriteIniValue("Translation", "Executable", _settings.Translation.Executable, _iniFilePath);
        Win32.WriteIniValue("Translation", "Model", _settings.Translation.Model, _iniFilePath);
        Win32.WriteIniValue("Translation", "ReasoningEffort", _settings.Translation.ReasoningEffort ?? "", _iniFilePath);
        // Profile APIs remove one surrounding quote pair. Supply that pair ourselves
        // so independent quotes belonging to the command line survive a save/reload.
        Win32.WriteIniValue("Translation", "Arguments", "\"" + (_settings.Translation.Arguments ?? "") + "\"", _iniFilePath);
        Win32.WriteIniValue("Translation", "ProviderId", _settings.Translation.ProviderId, _iniFilePath);
        Win32.WriteIniValue("Translation", "UseDefaultModel", _settings.Translation.UseDefaultModel.ToString(), _iniFilePath);
        Win32.WriteIniValue("Translation", "UseManualModel", _settings.Translation.UseManualModel.ToString(), _iniFilePath);
        Win32.WriteIniValue("Translation", "UseCustomArguments", _settings.Translation.UseCustomArguments.ToString(), _iniFilePath);
        Win32.WriteIniValue("Translation", "OutputFormat", _settings.Translation.OutputFormat, _iniFilePath);
      }
      if (cliError == null || _settings.Translation.UseApi) {
        Win32.WriteIniValue("Translation", "TimeoutSeconds", _settings.Translation.TimeoutSeconds.ToString(), _iniFilePath);
        Win32.WriteIniValue("Translation", "ParallelRequests", _settings.Translation.ParallelRequests.ToString(), _iniFilePath);
        Win32.WriteIniValue("Translation", "MinimumChunkCharacters", _settings.Translation.MinimumChunkCharacters.ToString(), _iniFilePath);
        Win32.WriteIniValue("Translation", "ShowButtons", _settings.Translation.ShowButtons.ToString(), _iniFilePath);
      }
      if (apiError == null && _settings.Translation.ApiConfigurationError == null && (_settings.Translation.UseApi || cliError == null)) {
        Win32.WriteIniValue("Translation", "ConnectionMode", _settings.Translation.ConnectionMode, _iniFilePath);
        Win32.WriteIniValue("Translation", "ApiConnectionId", _settings.Translation.SelectedApiConnectionId ?? "", _iniFilePath);
      }
      else if (!_settings.Translation.UseApi && cliError == null)
        Win32.WriteIniValue("Translation", "ConnectionMode", "cli", _iniFilePath);
      if (apiError != null) throw apiError;
      if (cliError != null) throw cliError;
    }

    private void ShowAboutDialog()
    {
      using (var aboutDialog = new AboutForm()) aboutDialog.ShowDialog(new PluginWindowOwner(PluginBase.nppData._nppHandle));
    }

    private void TogglePanelVisible()
    {
      if (!IsMarkdownFile(_nppGateway.GetCurrentFilePath())) { _panelAutoHidden = false; RememberPreviewEnabled(false); return; }
      _panelAutoHidden = false;
      try {
        var preview = PreviewForm;
        if (!_ptrNppTbData.HasValue) {
          var tbData = new NppTbData();
          tbData.hClient = preview.Handle;
          tbData.pszName = Main.PluginTitle;
          tbData.dlgID = _myDlgId;
          tbData.uMask = NppTbMsg.DWS_DF_CONT_RIGHT | NppTbMsg.DWS_ICONTAB | NppTbMsg.DWS_ICONBAR;
          if (_icon == null) _icon = PluginIcon.Create(PluginIcon.ScaleForWindow(16, PluginBase.nppData._nppHandle));
          tbData.hIconTab = _icon.Handle;
          tbData.pszModuleName = $"{Main.ModuleName}.dll";

          var dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf(tbData));
          try {
            Marshal.StructureToPtr(tbData, dataPtr, false);
            Win32.SendMessage(PluginBase.nppData._nppHandle, (uint) NppMsg.NPPM_DMMREGASDCKDLG, 0, dataPtr);
            if (!IsPanelRegistered(preview.Handle, tbData)) {
              // Notepad++ can retain a floating container number after losing its
              // FloatingWindow record. Its first registration then creates no widget.
              // A nonempty fallback rectangle makes it create the missing container.
              tbData.rcFloat = VisibleFloatRect();
              tbData.hIconTab = IntPtr.Zero; // the first call already handed the icon to Notepad++
              Marshal.StructureToPtr(tbData, dataPtr, true);
              Win32.SendMessage(PluginBase.nppData._nppHandle, (uint) NppMsg.NPPM_DMMREGASDCKDLG, 0, dataPtr);
            }
            if (!IsPanelRegistered(preview.Handle, tbData))
              throw new InvalidOperationException("Notepad++ не зарегистрировал панель AnotherMarkdown.");
            _ptrNppTbData = dataPtr;
            dataPtr = IntPtr.Zero;
          }
          finally {
            if (dataPtr != IntPtr.Zero) {
              Marshal.DestroyStructure(dataPtr, typeof(NppTbData));
              Marshal.FreeHGlobal(dataPtr);
            }
          }
          _isPanelVisible = true;
        }
        else {
          _isPanelVisible = !_isPanelVisible;
        }

        var flag = _isPanelVisible ? NppMsg.NPPM_DMMSHOW : NppMsg.NPPM_DMMHIDE;
        Win32.SendMessage(PluginBase.nppData._nppHandle, (uint) flag, 0, preview.Handle);
        SetPanelVisibleState(_isPanelVisible);
        RememberPreviewEnabled(_isPanelVisible);
        if (_isPanelVisible) {
          BringFloatingPanelOnScreen(preview.Handle);
          preview.UpdateSettings(_settings);
          RenderMarkdown(force: true);
        }
      }
      catch (Exception ex) {
        MessageBox.Show(new PluginWindowOwner(PluginBase.nppData._nppHandle),
          "Не удалось открыть панель AnotherMarkdown.\r\n\r\n" + ex.Message,
          Main.PluginTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
      }
    }

    private static bool IsPanelRegistered(IntPtr panelHandle, NppTbData data)
    {
      var name = Marshal.StringToHGlobalUni(data.pszName);
      var module = Marshal.StringToHGlobalUni(data.pszModuleName);
      try {
        return Win32.SendMessage(PluginBase.nppData._nppHandle,
          (uint) NppMsg.NPPM_DMMGETPLUGINHWNDBYNAME, name, module) == panelHandle;
      }
      finally {
        Marshal.FreeHGlobal(module);
        Marshal.FreeHGlobal(name);
      }
    }

    private static RECT VisibleFloatRect()
    {
      var area = Screen.FromHandle(PluginBase.nppData._nppHandle).WorkingArea;
      var width = Math.Min(900, Math.Max(1, area.Width - 40));
      var height = Math.Min(650, Math.Max(1, area.Height - 40));
      var left = area.Left + (area.Width - width) / 2;
      var top = area.Top + (area.Height - height) / 2;
      return new RECT(left, top, left + width, top + height);
    }

    private static void BringFloatingPanelOnScreen(IntPtr panelHandle)
    {
      var window = Win32.GetAncestor(panelHandle, 2); // GA_ROOT
      if (window == IntPtr.Zero || window == PluginBase.nppData._nppHandle) return;
      RECT bounds;
      if (!Win32.GetWindowRect(window, out bounds)) return;
      var rectangle = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
      if (rectangle.Width >= 240 && rectangle.Height >= 160 && Screen.AllScreens.Any(screen => {
        var visible = Rectangle.Intersect(rectangle, screen.WorkingArea);
        return visible.Width >= 160 && visible.Height >= 120;
      })) return;

      var safe = VisibleFloatRect();
      Win32.SetWindowPos(window, IntPtr.Zero, safe.Left, safe.Top,
        safe.Right - safe.Left, safe.Bottom - safe.Top,
        0x0004 | 0x0010); // SWP_NOZORDER | SWP_NOACTIVATE
    }

    public void RenderMarkdown(bool force = false)
    {
      if (_isPanelVisible && !_disposedValue) _renderQueue.Request(force);
    }

    private Task RenderCurrentBufferAsync(bool force)
    {
      var currentFile = _nppGateway.GetCurrentFilePath();
      if (!IsMarkdownFile(currentFile)) {
        UpdateAutomaticPanel();
        return Task.CompletedTask;
      }
      var gateway = scintillaGatewayFactory();
      var text = gateway.GetText(gateway.GetLength() + 1);
      var modified = gateway.GetModify();
      var form = PreviewForm;
      var session = GetEditorSession(currentFile);
      form.SetSourceState(session.Id, gateway.GetReadOnly(), modified, session.VisualEditing, gateway.GetCaretPeriod());
      if (_sourceCaret == null) UpdateSourceCaret(gateway);
      session.Observe(text, modified, gateway.GetCodePage());
      return form.RenderMarkdown(text, currentFile, force);
    }

    private bool IsDarkModeEnabled()
    {
      // NPPM_ISDARKMODEENABLED (NPPMSG + 107)
      IntPtr ret = Win32.SendMessage(PluginBase.nppData._nppHandle, (uint) (Constants.NPPMSG + 107), UNUSED, UNUSED);
      return ret.ToInt32() == 1;
    }

    protected virtual void Dispose(bool disposing)
    {
      if (!disposing) {
        return;
      }
      if (_disposedValue) {
        return;
      }

      _disposedValue = true;
      _renderQueue.Dispose();
      AppDomain.CurrentDomain.AssemblyResolve -= CurrentDomain_AssemblyResolve;
      _linkRevealTimer?.Dispose(); _linkRevealTimer = null;
      ClearSourceCaret();
      DisposeHistoryIcons();

      _icon?.Dispose();
      _icon = null;
      _toolbarIcon?.Dispose(); _toolbarDarkIcon?.Dispose();
      if (_toolbarBitmap != IntPtr.Zero) DeleteObject(_toolbarBitmap);
      _toolbarIcon = _toolbarDarkIcon = null; _toolbarBitmap = IntPtr.Zero;

      if (_ptrNppTbData.HasValue) {
        Marshal.DestroyStructure(_ptrNppTbData.Value, typeof(NppTbData));
        Marshal.FreeHGlobal(_ptrNppTbData.Value);
        _ptrNppTbData = null;
      }
      _previewForm?.Dispose();
      _previewForm = null;
    }

    public void Dispose()
    {
      // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
      Dispose(disposing: true);
      GC.SuppressFinalize(this);
    }

    private const int UNUSED = 0;

    private bool _isPanelVisible;
    private MarkdownPreviewForm _previewForm;
    private object _lock = new object();
    private int _myDlgId = -1;
    private readonly Func<IScintillaGateway> scintillaGatewayFactory;
    private readonly INotepadPPGateway _nppGateway;
    private string _iniFilePath;
    private Settings _settings;
    private IntPtr? _ptrNppTbData;
    private Icon _icon;
    private Icon _toolbarIcon, _toolbarDarkIcon;
    private IntPtr _toolbarBitmap;
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    private bool _disposedValue;

    private object _syncViewLock = new object();
    private bool _syncViewPending = false;
    private Task _syncViewTask;

    private readonly PreviewRenderQueue _renderQueue;
  }
}
