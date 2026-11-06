using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using AnotherMarkdown.Entities;
using Kbg.NppPluginNET.PluginInfrastructure;
using Webview2Viewer;
using PanelCommon;

namespace AnotherMarkdown.Forms
{
  public partial class MarkdownPreviewForm : Form
  {
    public EventDispatcher OnEvent { get; set; }

    public EventHandler DockClosed { get; set; }

    public static MarkdownPreviewForm Create(Settings settings) { 
      return new MarkdownPreviewForm(settings);
    }

    private MarkdownPreviewForm(Settings settings) : this(settings, null) { }

    internal MarkdownPreviewForm(Settings settings, Func<string, string, bool, Task> renderContent)
    {
      OnEvent = new EventDispatcher();
      InitializeComponent();
      _focusGuard = new DockingFocusGuard(this);
      Text = PluginBranding.Name;
      Icon = PluginIcon.ApplicationIcon();
      InitializeTranslation(settings);
      InitializeEditorToolbar();

      if (renderContent != null) {
        _renderContent = renderContent;
        return;
      }

      var webView = new Webview2WebbrowserControl();
      webView.EditorRequestHandler = HandleEditorRequest;
      webView.EditorUiChanged = UpdateEditorToolbar;
      webView.EditorNavigationChanged = state => NavigationChanged?.Invoke(state);
      webView.Initialize(new ProxySettings(settings, () => IsTranslationPreview), OnEvent);

      panel1.Controls.Clear();
      webView.AddToHost(panel1);
      panel1.Visible = true;

      _webView = webView;
      _renderContent = webView.SetContentAsync;
    }

    public void UpdateSettings(Settings settings)
    {
      UpdateTranslationSettings(settings);
      var isDarkModeEnabled = settings.IsDarkModeEnabled;
      if (isDarkModeEnabled) {
        tbPreview.BackColor = Color.Black;
      }
      else {
        tbPreview.BackColor = SystemColors.Control;
      }

      tbPreview.ForeColor = isDarkModeEnabled ? Color.White : SystemColors.ControlText;
      if (editorUi != null) UpdateEditorToolbar(editorUi);
      tbPreview.Visible = settings.ShowToolbar || settings.Translation.ShowButtons;
    }

    public async Task RenderMarkdown(string currentText, string filepath, bool force = false)
    {
      if (force) forceRender = true;
      await UpdateSourceAsync(currentText, filepath);
    }

    public Action<EditorNavigation> NavigationChanged { get; set; }
    public Task SyncNavigationAsync(EditorNavigation state) => _webView?.RunNavigationAsync(state) ?? Task.CompletedTask;

    public async Task ScrollToElementWithLineNo(int lineNo)
    {
      if (_webView != null && !IsTranslationPreview) {
        await _webView.ScrollToElementWithLineNo(lineNo);
      }
    }

    protected override void WndProc(ref Message m)
    {
      if (m.Msg == Win32.WM_NOTIFY) {
        var nmdr = (Win32.NMHDR) Marshal.PtrToStructure(m.LParam, typeof(Win32.NMHDR));
        if (nmdr.hwndFrom == PluginBase.nppData._nppHandle) {
          switch ((DockMgrMsg) (nmdr.code & 0xFFFFU)) {
            case DockMgrMsg.DMN_DOCK: {
              _focusGuard.Enabled = false;
              break;
            }
            case DockMgrMsg.DMN_FLOAT: {
              _focusGuard.Enabled = true;
              break;
            }
            case DockMgrMsg.DMN_CLOSE: {
              DockClosed?.Invoke(this, EventArgs.Empty);
              break;
            }
          }
        }
      }
      base.WndProc(ref m);
    }

    /// <summary>
    /// Clean up any resources being used.
    /// </summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing)
    {
      if (disposing) {
        _disposed = true;
        _focusGuard.Dispose();
        CancelTranslation();
        if (_webView != null) {
          _webView.Dispose();
          _webView = null;
        }
        if (components != null) {
          components.Dispose();
          components = null;
        }
      }
      base.Dispose(disposing);
    }
    private Webview2WebbrowserControl _webView;
    private readonly DockingFocusGuard _focusGuard;
    private readonly Func<string, string, bool, Task> _renderContent;
    private bool _disposed;
  }
}
