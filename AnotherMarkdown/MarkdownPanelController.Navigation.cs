using System;
using System.Threading.Tasks;
using AnotherMarkdown.Forms;
using Kbg.NppPluginNET.PluginInfrastructure;
using PanelCommon;

namespace AnotherMarkdown
{
  public partial class MarkdownPanelController
  {
    private EditorNavigation _lastNavigation;
    private bool _applyingNavigation;
    private SyncedSourceCaret _sourceCaret;

    private void ClearSourceCaret()
    {
      _sourceCaret?.Dispose();
      _sourceCaret = null;
    }

    private void UpdateSourceCaret(IScintillaGateway gateway)
    {
      if (_previewForm == null || PluginBase.nppData._nppHandle == IntPtr.Zero) return;
      var window = PluginBase.GetCurrentScintilla();
      if (_sourceCaret?.SourceWindow != window) {
        ClearSourceCaret();
        var buffer = CurrentBufferId();
        _sourceCaret = new SyncedSourceCaret(window, gateway, () => !_disposedValue && _isPanelVisible &&
          _settings.SyncViewWithCaretPosition && _previewForm != null && !_previewForm.IsTranslationPreview &&
          CurrentBufferId() == buffer && PluginBase.GetCurrentScintilla() == window);
      }
      _sourceCaret.Refresh();
    }

    private EditorNavigation ReadNavigation(IScintillaGateway gateway, EditorSnapshot snapshot)
    {
      var firstLine = Math.Max(0, gateway.DocLineFromVisible(gateway.GetFirstVisibleLine()));
      return new EditorNavigation {
        Session = snapshot.Session, Revision = snapshot.Revision,
        Anchor = gateway.CountCodeUnits(0, gateway.GetAnchor()),
        Focus = gateway.CountCodeUnits(0, gateway.GetCurrentPos()),
        Top = gateway.CountCodeUnits(0, gateway.PositionFromLine(firstLine))
      };
    }

    private async Task PublishNavigationAsync(bool requested)
    {
      if (_disposedValue || !_isPanelVisible || _previewForm == null || _previewForm.IsTranslationPreview || _applyingNavigation) return;
      var snapshot = _previewForm.GetEditorSnapshot();
      if (snapshot.Session != GetEditorSession(_nppGateway.GetCurrentFilePath())) return;
      var gateway = scintillaGatewayFactory();
      // Rendering is asynchronous. Wait for the matching text revision before sending offsets.
      if (gateway.GetText(gateway.GetLength() + 1) != snapshot.Text) return;
      var state = ReadNavigation(gateway, snapshot);
      var previous = _lastNavigation;
      var fresh = previous == null || previous.Session != state.Session || previous.Revision != state.Revision;
      state.Selection = _settings.SyncViewWithCaretPosition && (requested || gateway.GetFocus()) &&
        (requested || fresh || previous.Anchor != state.Anchor || previous.Focus != state.Focus);
      state.Scroll = _settings.SyncViewWithFirstVisibleLine && (requested || fresh || previous.Top != state.Top);
      state.Reveal = state.Selection && !state.Scroll && !requested;
      _lastNavigation = state;
      if (state.Selection || state.Scroll) await _previewForm.SyncNavigationAsync(state);
    }

    private async void HandleEditorNavigation(EditorNavigation state)
    {
      try {
        if (_disposedValue || state == null || !_isPanelVisible || _previewForm == null || _previewForm.IsTranslationPreview) return;
        var snapshot = _previewForm.GetEditorSnapshot();
        if (state.Session != snapshot.Session || state.Revision != snapshot.Revision ||
            state.Session != GetEditorSession(_nppGateway.GetCurrentFilePath())) return;
        var gateway = scintillaGatewayFactory();
        if (gateway.GetText(gateway.GetLength() + 1) != snapshot.Text) return;
        if (state.Request) { await PublishNavigationAsync(true); return; }
        ApplyNavigation(gateway, snapshot, state);
      }
      catch (ObjectDisposedException) { /* The dock may close while a message is queued. */ }
    }

    private static int NavigationOffset(string text, int offset)
    {
      offset = Math.Max(0, Math.Min(text.Length, offset));
      if (offset > 0 && offset < text.Length && char.IsLowSurrogate(text[offset]) && char.IsHighSurrogate(text[offset - 1])) offset--;
      return offset;
    }

    private void ApplyNavigation(IScintillaGateway gateway, EditorSnapshot snapshot, EditorNavigation state)
    {
      _applyingNavigation = true;
      try {
        if (state.Selection && _settings.SyncViewWithCaretPosition) {
          var anchor = gateway.PositionRelativeCodeUnits(0, NavigationOffset(snapshot.Text, state.Anchor));
          var focus = gateway.PositionRelativeCodeUnits(0, NavigationOffset(snapshot.Text, state.Focus));
          // Neither operation gives focus to Scintilla or creates an undo entry.
          gateway.SetAnchor(anchor); gateway.SetCurrentPos(focus);
          if (state.Reveal) {
            gateway.EnsureVisible(gateway.LineFromPosition(focus));
            gateway.ScrollCaret();
          }
        }
        if (state.Scroll && _settings.SyncViewWithFirstVisibleLine) {
          var position = gateway.PositionRelativeCodeUnits(0, NavigationOffset(snapshot.Text, state.Top));
          var line = gateway.LineFromPosition(position);
          gateway.EnsureVisible(line);
          gateway.SetFirstVisibleLine(gateway.VisibleFromDocLine(line));
        }
        _lastNavigation = ReadNavigation(gateway, snapshot);
        if (state.Selection && _settings.SyncViewWithCaretPosition) UpdateSourceCaret(gateway);
        else _sourceCaret?.Refresh();
      }
      finally { _applyingNavigation = false; }
    }
  }
}
