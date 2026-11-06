using System;
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
    private readonly IntPtr sourceWindow;
    private readonly IScintillaGateway source;
    private readonly Func<bool> enabled;
    private readonly Marker marker;
    private readonly Timer timer;
    private Rectangle bounds;
    private bool disposed;

    public IntPtr SourceWindow => sourceWindow;

    public SyncedSourceCaret(IntPtr sourceWindow, IScintillaGateway source, Func<bool> enabled)
    {
      this.sourceWindow = sourceWindow;
      this.source = source;
      this.enabled = enabled;
      marker = new Marker(sourceWindow);
      // Also follow native scrolling, zoom, theme and focus changes that do not
      // produce a navigation message from the rendered document.
      timer = new Timer { Interval = 100 };
      timer.Tick += (_, __) => Refresh();
      timer.Start();
      Refresh();
    }

    public void Refresh()
    {
      if (disposed) return;
      if (!enabled() || !IsWindowVisible(sourceWindow) || source.GetFocus()) { Hide(); return; }
      var position = source.GetCurrentPos();
      var line = source.LineFromPosition(position);
      if (!source.GetLineVisible(line) || !GetClientRect(sourceWindow, out var client)) { Hide(); return; }
      var left = source.GetMarginLeft();
      for (var margin = 0; margin < source.GetMargins(); margin++) left += source.GetMarginWidthN(margin);
      var next = Rectangle.Intersect(Rectangle.FromLTRB(left, 0, client.Right, client.Bottom),
        new Rectangle(source.PointXFromPosition(position), source.PointYFromPosition(position),
          Math.Max(2, source.GetCaretWidth()), source.TextHeight(line)));
      if (next.Width <= 0 || next.Height <= 0) { Hide(); return; }
      var colour = source.GetCaretFore();
      var foreground = Color.FromArgb(colour.Red, colour.Green, colour.Blue);
      if (next == bounds && marker.BackColor == foreground && IsWindowVisible(marker.Handle)) return;
      bounds = next;
      marker.BackColor = foreground;
      // HWND_TOP affects only this source window's children. No activation,
      // selection, text, undo history or Scintilla focus notification is changed.
      SetWindowPos(marker.Handle, IntPtr.Zero, next.X, next.Y, next.Width, next.Height, 0x0010 | 0x0040);
    }

    private void Hide()
    {
      bounds = Rectangle.Empty;
      if (marker.IsHandleCreated && IsWindowVisible(marker.Handle)) ShowWindow(marker.Handle, 0);
    }

    public void Dispose()
    {
      if (disposed) return;
      disposed = true;
      timer.Stop(); timer.Dispose();
      marker.Dispose();
    }

    private sealed class Marker : Control
    {
      private readonly IntPtr sourceWindow;
      public Marker(IntPtr sourceWindow)
      {
        this.sourceWindow = sourceWindow;
        SetStyle(ControlStyles.Selectable, false);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
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
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
  }
}
