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
      if (PluginBase.nppData._nppHandle != IntPtr.Zero)
        _activationWindow = new EditorActivationWindow(this, PluginBase.nppData._nppHandle);
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
        _activationWindow?.ReleaseHandle();
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
    private readonly EditorActivationWindow _activationWindow;
    private readonly DockingFocusGuard _focusGuard;
    private readonly Func<string, string, bool, Task> _renderContent;
    private bool _disposed;

    // Notepad++ focuses its source control on keyboard reactivation. Restore a
    // previously focused visual editor after the host finishes that operation.
    private sealed class EditorActivationWindow : NativeWindow
    {
      private readonly MarkdownPreviewForm panel;
      private readonly uint processId;
      private string resumeSession;
      private int activation;

      public EditorActivationWindow(MarkdownPreviewForm panel, IntPtr host)
      {
        this.panel = panel;
        GetWindowThreadProcessId(host, out processId);
        AssignHandle(host);
      }

      protected override void WndProc(ref Message message)
      {
        if (message.Msg != 0x0006) { base.WndProc(ref message); return; } // WM_ACTIVATE
        int kind = (int)(message.WParam.ToInt64() & 0xffff), current = ++activation;
        var session = resumeSession;
        resumeSession = null;
        if (kind == 0 && panel.Visible && panel.editorUi?.Editing == true && panel._webView.ContainsKeyboardFocus) {
          GetWindowThreadProcessId(message.LParam, out var nextProcess);
          if (nextProcess != processId) resumeSession = panel.editorUi.Session;
        }
        base.WndProc(ref message);
        // WA_CLICKACTIVE honours the control the user explicitly clicked.
        if (kind != 1 || session == null || panel._disposed || !panel.IsHandleCreated) return;
        panel.BeginInvoke(new Action(async () => {
          if (current != activation || panel._disposed || !panel.Visible || GetActiveWindow() != Handle ||
              panel.editorUi?.Session != session || !panel.editorUi.Editing ||
              !SyncedSourceCaret.HasKeyboardFocus(PluginBase.GetCurrentScintilla())) return;
          await panel._webView.RestoreEditorFocusAsync(session);
        }));
      }

      [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
      [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }
  }
}
