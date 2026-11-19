using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using AnotherMarkdown.Forms;
using Kbg.NppPluginNET.PluginInfrastructure;
using PanelCommon;

namespace AnotherMarkdown
{
  public partial class MarkdownPanelController
  {
    private EditorNavigation _lastNavigation;
    private int _lastDisplayLine, _lastXOffset;
    private Wrap _navigationWrap;
    private bool _navigationCaretVisible;
    private bool _applyingNavigation;
    private SyncedSourceCaret _sourceCaret;
    private Timer _viewportTimer;
    private int _viewportTarget;

    private void ClearSourceCaret()
    {
      StopViewportScroll();
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
        _sourceCaret = new SyncedSourceCaret(window, gateway,
          () => !_disposedValue && _isPanelVisible && _previewForm != null && CurrentBufferId() == buffer && PluginBase.GetCurrentScintilla() == window,
          () => _settings.SyncViewWithCaretPosition && !_previewForm.IsTranslationPreview);
      }
      _sourceCaret.Refresh();
    }

    private EditorNavigation ReadNavigation(IScintillaGateway gateway, EditorSnapshot snapshot)
    {
      var displayLine = gateway.GetFirstVisibleLine();
      var firstLine = Math.Max(0, gateway.DocLineFromVisible(displayLine));
      var top = gateway.PositionFromLine(firstLine);
      if (displayLine != gateway.VisibleFromDocLine(firstLine) || gateway.GetXOffset() > 0) {
        // A wrapped continuation and a horizontally panned line can have the
        // same document line number but start at different text positions.
        var hit = gateway.PositionFromPoint(SyncedSourceCaret.TextLeft(gateway), Math.Max(1, gateway.TextHeight(firstLine) / 2));
        if (hit >= top && gateway.LineFromPosition(hit) == firstLine) top = hit;
      }
      var focus = gateway.GetCurrentPos();
      _navigationWrap = gateway.GetWrapMode();
      var area = SyncedSourceCaret.TextBounds(SourceWindow(), gateway);
      _navigationCaretVisible = area.Contains(gateway.PointXFromPosition(focus), gateway.PointYFromPosition(focus));
      return new EditorNavigation {
        Session = snapshot.Session, Revision = snapshot.Revision,
        Anchor = gateway.CountCodeUnits(0, gateway.GetAnchor()),
        Focus = gateway.CountCodeUnits(0, focus),
        Top = gateway.CountCodeUnits(0, top)
      };
    }

    private async Task PublishNavigationAsync(bool requested)
    {
      if (_disposedValue || !_isPanelVisible || _previewForm == null || _previewForm.IsTranslationPreview || _applyingNavigation) return;
      var snapshot = _previewForm.GetEditorSnapshot();
      if (snapshot.Session != GetEditorSession(_nppGateway.GetCurrentFilePath()).Id) return;
      var gateway = scintillaGatewayFactory();
      var previous = _lastNavigation;
      var wrapChanged = previous?.Session == snapshot.Session && _navigationWrap != gateway.GetWrapMode();
      var revealAfterWrap = wrapChanged && _settings.SyncViewWithCaretPosition && _navigationCaretVisible;
      if (revealAfterWrap) RevealSourceSelection(gateway);
      var state = ReadNavigation(gateway, snapshot);
      var fresh = previous == null || previous.Session != state.Session || previous.Revision != state.Revision;
      var window = SourceWindow();
      var sourceFocused = window != IntPtr.Zero ? SyncedSourceCaret.HasKeyboardFocus(window) : gateway.GetFocus();
      state.Selection = _settings.SyncViewWithCaretPosition && (requested || revealAfterWrap || sourceFocused) &&
        (requested || revealAfterWrap || fresh || previous.Anchor != state.Anchor || previous.Focus != state.Focus);
      state.Scroll = _settings.SyncViewWithFirstVisibleLine && (requested || wrapChanged || fresh || gateway.GetFirstVisibleLine() != _lastDisplayLine);
      int x = gateway.GetXOffset();
      if (_settings.SyncViewWithFirstVisibleLine && _navigationWrap == Wrap.NONE && (x != _lastXOffset || ((fresh || requested) && x > 0)))
        state.Left = state.Top;
      if (!state.Selection && !state.Scroll && !state.Left.HasValue) return;
      // Only read the buffer when offsets will actually be sent. Its revision must match the preview.
      if (gateway.GetText(gateway.GetLength() + 1) != snapshot.Text) return;
      StopViewportScroll();
      state.Reveal = state.Selection && !requested;
      RememberNavigation(gateway, state);
      await _previewForm.SyncNavigationAsync(state);
    }

    private async void HandleEditorNavigation(EditorNavigation state)
    {
      try {
        if (_disposedValue || state == null || !_isPanelVisible || _previewForm == null || _previewForm.IsTranslationPreview) return;
        var snapshot = _previewForm.GetEditorSnapshot();
        if (state.Session != snapshot.Session || state.Revision != snapshot.Revision ||
            state.Session != GetEditorSession(_nppGateway.GetCurrentFilePath()).Id) return;
        if (state.Request) { await PublishNavigationAsync(true); return; }
        var gateway = scintillaGatewayFactory();
        if (gateway.GetText(gateway.GetLength() + 1) != snapshot.Text) return;
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

    private static IntPtr SourceWindow() => PluginBase.nppData._nppHandle == IntPtr.Zero ? IntPtr.Zero : PluginBase.GetCurrentScintilla();

    private void RememberNavigation(IScintillaGateway gateway, EditorNavigation state)
    {
      _lastNavigation = state;
      _lastDisplayLine = gateway.GetFirstVisibleLine();
      _lastXOffset = gateway.GetXOffset();
    }

    private static int FirstDisplayLine(IScintillaGateway gateway, string text, EditorNavigation state)
    {
      int position = gateway.PositionRelativeCodeUnits(0, NavigationOffset(text, state.Top));
      int line = gateway.LineFromPosition(position), first = gateway.VisibleFromDocLine(line);
      if (state.NextTop.HasValue) {
        if (!(state.TopFraction > 0) || state.NextTop.Value <= state.Top) return first;
        int next = NavigationOffset(text, state.NextTop.Value);
        int endLine = gateway.LineFromPosition(gateway.PositionRelativeCodeUnits(0, next));
        int end = gateway.VisibleFromDocLine(endLine);
        if (next == text.Length && next > 0 && text[next - 1] != '\n' && text[next - 1] != '\r')
          end += gateway.WrapCount(endLine);
        return first + (int)(Math.Max(0, end - first) * Math.Min(1, state.TopFraction));
      }
      // An independently scrolled code block can identify its exact text row.
      if (gateway.GetWrapMode() == Wrap.NONE) return first;
      int lineY = gateway.PointYFromPosition(gateway.PositionFromLine(line));
      int subLine = (gateway.PointYFromPosition(position) - lineY) / Math.Max(1, gateway.TextHeight(line));
      return first + Math.Max(0, Math.Min(subLine, gateway.WrapCount(line) - 1));
    }

    private void StopViewportScroll()
    {
      _viewportTimer?.Dispose();
      _viewportTimer = null;
    }

    private void ScrollSourceVertically(IScintillaGateway gateway, int target, bool smooth)
    {
      int current = gateway.GetFirstVisibleLine(), distance = target - current;
      if (smooth && _viewportTimer != null && target == _viewportTarget && current == _lastDisplayLine) return;
      StopViewportScroll();
      if (distance == 0) return;
      // Track fine input immediately. A discrete wheel notch can cover many
      // wrapped source lines; distribute that movement over a few UI frames.
      if (!smooth || Math.Abs(distance) < 3 || Math.Abs(distance) > gateway.LinesOnScreen() || SourceWindow() == IntPtr.Zero) {
        gateway.SetFirstVisibleLine(target);
        return;
      }
      var buffer = CurrentBufferId();
      var window = SourceWindow();
      var wrap = gateway.GetWrapMode();
      int caret = gateway.GetCurrentPos(), anchor = gateway.GetAnchor();
      // Use up to eight small steps. A delayed UI tick must not skip several
      // steps at once when the Markdown row is particularly wide.
      int step = (Math.Abs(distance) + 7) / 8;
      gateway.SetFirstVisibleLine(current + Math.Sign(distance) * step);
      int applied = gateway.GetFirstVisibleLine();
      if (applied == current || applied == target) return; // Already at the scroll boundary.
      var timer = new Timer { Interval = 15 };
      _viewportTimer = timer; _viewportTarget = target;
      timer.Tick += (_, __) => {
        if (_viewportTimer != timer) return;
        if (_disposedValue || !_isPanelVisible || !_settings.SyncViewWithFirstVisibleLine || _previewForm?.IsTranslationPreview != false ||
            CurrentBufferId() != buffer || SourceWindow() != window || gateway.GetWrapMode() != wrap ||
            gateway.GetFirstVisibleLine() != applied || gateway.GetCurrentPos() != caret || gateway.GetAnchor() != anchor) {
          StopViewportScroll();
          return;
        }
        int next = distance > 0 ? Math.Min(target, applied + step) : Math.Max(target, applied - step);
        _applyingNavigation = true;
        try {
          gateway.SetFirstVisibleLine(next);
          applied = _lastDisplayLine = gateway.GetFirstVisibleLine();
          _sourceCaret?.Refresh();
        }
        finally { _applyingNavigation = false; }
        if (applied == target || applied != next) StopViewportScroll();
      };
      timer.Start();
    }

    private static void RevealSourceSelection(IScintillaGateway gateway)
    {
      var focus = gateway.GetCurrentPos();
      var line = gateway.LineFromPosition(focus);
      gateway.EnsureVisible(line);
      gateway.ScrollRange(gateway.GetAnchor(), focus);
      if (gateway.GetWrapMode() != Wrap.NONE) return;
      var box = SyncedSourceCaret.TextBounds(SourceWindow(), gateway);
      if (box.Width <= 0) return;
      var x = gateway.PointXFromPosition(focus);
      var margin = Math.Min(box.Width / 4, Math.Max(8, gateway.TextHeight(line) / 2));
      var target = Math.Max(box.Left + margin, Math.Min(x, box.Right - margin));
      if (x != target) gateway.SetXOffset(Math.Max(0, gateway.GetXOffset() + x - target));
    }

    private void ApplyNavigation(IScintillaGateway gateway, EditorSnapshot snapshot, EditorNavigation state)
    {
      _applyingNavigation = true;
      try {
        var select = state.Selection && _settings.SyncViewWithCaretPosition;
        var reveal = select && state.Reveal;
        if (select) {
          StopViewportScroll();
          var anchor = gateway.PositionRelativeCodeUnits(0, NavigationOffset(snapshot.Text, state.Anchor));
          var focus = gateway.PositionRelativeCodeUnits(0, NavigationOffset(snapshot.Text, state.Focus));
          // Neither operation gives focus to Scintilla or creates an undo entry.
          gateway.SetAnchor(anchor); gateway.SetCurrentPos(focus);
        }
        if (state.Scroll && _settings.SyncViewWithFirstVisibleLine) {
          int displayLine = FirstDisplayLine(gateway, snapshot.Text, state);
          ScrollSourceVertically(gateway, displayLine, state.SmoothScroll && !select);
        }
        if (state.Left.HasValue && _settings.SyncViewWithFirstVisibleLine && gateway.GetWrapMode() == Wrap.NONE) {
          int point = gateway.PositionRelativeCodeUnits(0, NavigationOffset(snapshot.Text, state.Left.Value));
          int currentX = gateway.GetXOffset();
          int targetX = Math.Max(0, currentX + gateway.PointXFromPosition(point) - SyncedSourceCaret.TextLeft(gateway));
          if (targetX != currentX) gateway.SetXOffset(targetX);
        }
        if (reveal) RevealSourceSelection(gateway);
        RememberNavigation(gateway, ReadNavigation(gateway, snapshot));
        if (state.Selection && _settings.SyncViewWithCaretPosition) UpdateSourceCaret(gateway);
        else _sourceCaret?.Refresh();
      }
      finally { _applyingNavigation = false; }
    }
  }
}
