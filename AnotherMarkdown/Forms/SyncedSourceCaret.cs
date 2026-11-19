using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Kbg.NppPluginNET.PluginInfrastructure;

namespace AnotherMarkdown.Forms
{
  // Scintilla hides its caret when WebView2 owns keyboard focus. This passive
  // child draws the same insertion point without changing either focus flag.
  internal sealed class SyncedSourceCaret : IDisposable
  {
    internal const int DefaultBlinkPeriod = 500;
    internal const int CaretWidth = 2;
    private readonly IntPtr sourceWindow;
    private readonly IScintillaGateway source;
    private readonly Func<bool> enabled;
    private readonly Func<bool> mirrorSelection;
    private readonly Marker marker;
    private readonly Timer timer;
    private readonly Stopwatch blink = new Stopwatch();
    private Rectangle bounds;
    private int lastPosition;
    private int? overriddenPeriod;
    private int? originalWidth;
    private bool disposed;

    public IntPtr SourceWindow => sourceWindow;

    internal static bool HasKeyboardFocus(IntPtr window)
    {
      var focus = GetFocus();
      return focus != IntPtr.Zero && (focus == window || IsChild(window, focus));
    }

    public SyncedSourceCaret(IntPtr sourceWindow, IScintillaGateway source, Func<bool> enabled, Func<bool> mirrorSelection)
    {
      this.sourceWindow = sourceWindow;
      this.source = source;
      this.enabled = enabled;
      this.mirrorSelection = mirrorSelection;
      marker = new Marker(sourceWindow);
      // Also follow native scrolling, zoom, theme and focus changes that do not
      // produce a navigation message from the rendered document.
      timer = new Timer { Interval = 100 };
      timer.Tick += (_, __) => Refresh();
      timer.Start();
      Refresh();
    }

    internal static int TextLeft(IScintillaGateway source)
    {
      var left = source.GetMarginLeft();
      var count = source.GetMargins();
      for (var margin = 0; margin < count; margin++) left += source.GetMarginWidthN(margin);
      return left;
    }

    internal static Rectangle TextBounds(IntPtr window, IScintillaGateway source)
    {
      return window != IntPtr.Zero && GetClientRect(window, out var client)
        ? Rectangle.FromLTRB(TextLeft(source), 0, client.Right - source.GetMarginRight(), client.Bottom)
        : Rectangle.Empty;
    }

    public void Refresh()
    {
      if (disposed) return;
      if (!enabled() || !IsWindowVisible(sourceWindow)) { RestoreNativeCaret(); Hide(); return; }
      int width = source.GetCaretWidth();
      if (width != CaretWidth) {
        originalWidth = width;
        source.SetCaretWidth(CaretWidth);
      }
      int period = source.GetCaretPeriod();
      if (period <= 0) {
        // A steady native caret has no interval to mirror. Use a local interval
        // while this Markdown view is active and restore the original on exit.
        overriddenPeriod = period;
        period = DefaultBlinkPeriod;
        source.SetCaretPeriod(period);
      }
      else if (period != DefaultBlinkPeriod) overriddenPeriod = null;
      if (!mirrorSelection() || HasKeyboardFocus(sourceWindow)) { Hide(); return; }
      var position = source.GetCurrentPos();
      var line = source.LineFromPosition(position);
      if (!source.GetLineVisible(line)) { Hide(); return; }
      var next = Rectangle.Intersect(TextBounds(sourceWindow, source),
        new Rectangle(source.PointXFromPosition(position), source.PointYFromPosition(position),
          CaretWidth, source.TextHeight(line)));
      if (next.Width <= 0 || next.Height <= 0) { Hide(); return; }
      if (!blink.IsRunning || lastPosition != position) {
        blink.Restart();
        lastPosition = position;
      }
      // Refresh also runs on scroll and repaint. Only a new insertion point
      // or a newly visible caret starts another visible half-cycle.
      if (timer.Interval != Math.Min(100, period)) timer.Interval = Math.Min(100, period);
      if ((blink.ElapsedMilliseconds / period) % 2 != 0) { HideMarker(); return; }
      var colour = source.GetCaretFore();
      var foreground = Color.FromArgb(colour.Red, colour.Green, colour.Blue);
      if (next == bounds && marker.BackColor == foreground && IsWindowVisible(marker.Handle)) return;
      bounds = next;
      marker.BackColor = foreground;
      // HWND_TOP affects only this source window's children. No activation,
      // selection, text, undo history or Scintilla focus notification is changed.
      SetWindowPos(marker.Handle, IntPtr.Zero, next.X, next.Y, next.Width, next.Height, 0x0010 | 0x0040);
      RedrawWindow(marker.Handle, IntPtr.Zero, IntPtr.Zero, 0x0101); // RDW_INVALIDATE | RDW_UPDATENOW
    }

    private void Hide()
    {
      blink.Reset();
      bounds = Rectangle.Empty;
      HideMarker();
    }

    private void HideMarker()
    {
      if (marker.IsHandleCreated && IsWindowVisible(marker.Handle)) ShowWindow(marker.Handle, 0);
    }

    private void RestoreNativeCaret()
    {
      if (originalWidth.HasValue && source.GetCaretWidth() == CaretWidth) source.SetCaretWidth(originalWidth.Value);
      originalWidth = null;
      if (overriddenPeriod.HasValue && source.GetCaretPeriod() == DefaultBlinkPeriod) source.SetCaretPeriod(overriddenPeriod.Value);
      overriddenPeriod = null;
    }

    public void Dispose()
    {
      if (disposed) return;
      disposed = true;
      timer.Stop(); timer.Dispose();
      RestoreNativeCaret();
      marker.Dispose();
    }

    private sealed class Marker : Control
    {
      private readonly IntPtr sourceWindow;
      public Marker(IntPtr sourceWindow)
      {
        this.sourceWindow = sourceWindow;
        SetStyle(ControlStyles.Selectable, false);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
        TabStop = false; Enabled = false; Visible = false;
        AccessibleRole = AccessibleRole.None;
      }
      protected override CreateParams CreateParams {
        get {
          var value = base.CreateParams;
          value.Parent = sourceWindow;
          value.Style |= 0x40000000 | 0x08000000; // WS_CHILD | WS_DISABLED
          value.ExStyle |= 0x08000000 | 0x00000020; // WS_EX_NOACTIVATE | WS_EX_TRANSPARENT
          return value;
        }
      }
      protected override void OnHandleCreated(EventArgs e)
      {
        base.OnHandleCreated(e);
        // A redirected child surface survives a repaint of the DirectWrite parent.
        // Some legacy host manifests only allow ordinary child windows.
        var style = Win32.GetWindowLong(Handle, Win32.GWL_EXSTYLE);
        Win32.SetWindowLong(Handle, Win32.GWL_EXSTYLE, new IntPtr(style.ToInt64() | Win32.WS_EX_LAYERED));
        if (!SetLayeredWindowAttributes(Handle, 0, 255, 2)) // LWA_ALPHA, fully opaque
          Win32.SetWindowLong(Handle, Win32.GWL_EXSTYLE, style);
      }
      protected override void OnPaint(PaintEventArgs e) => e.Graphics.Clear(BackColor);
      protected override void WndProc(ref Message message)
      {
        if (message.Msg == 0x0084) { message.Result = new IntPtr(-1); return; } // HTTRANSPARENT
        if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; } // MA_NOACTIVATE
        base.WndProc(ref message);
      }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr window, uint colourKey, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr window, IntPtr rect, IntPtr region, uint flags);
  }
}
