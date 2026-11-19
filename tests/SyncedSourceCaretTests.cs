using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Windows.Forms;
using AnotherMarkdown;
using Kbg.NppPluginNET.PluginInfrastructure;

internal static class SyncedSourceCaretTests
{
  private static int checks;
  private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
  private sealed class Host : Form
  {
    protected override bool ShowWithoutActivation => true;
  }
  private sealed class Source : RealProxy
  {
    public int X = 120, Y = 30, Position = 17, Period = 3000, Width = 1;
    public bool Focused, LineVisible = true;
    public Colour Colour = new Colour(0x123456);
    public Source() : base(typeof(IScintillaGateway)) { }
    public override IMessage Invoke(IMessage message)
    {
      var call = (IMethodCallMessage)message; object result;
      switch (call.MethodName) {
        case "GetFocus": result = Focused; break;
        case "GetCurrentPos": result = Position; break;
        case "GetCaretPeriod": result = Period; break;
        case "SetCaretPeriod": Period = (int)call.Args[0]; result = null; break;
        case "LineFromPosition": result = 2; break;
        case "GetLineVisible": result = LineVisible; break;
        case "GetMarginLeft": result = 2; break;
        case "GetMarginRight": result = 2; break;
        case "GetMargins": result = 2; break;
        case "GetMarginWidthN": result = 20; break;
        case "PointXFromPosition": result = X; break;
        case "PointYFromPosition": result = Y; break;
        case "GetCaretWidth": result = Width; break;
        case "SetCaretWidth": Width = (int)call.Args[0]; result = null; break;
        case "TextHeight": result = 18; break;
        case "GetCaretFore": result = Colour; break;
        default: throw new Exception("Passive caret must not call " + call.MethodName);
      }
      return new ReturnMessage(result, null, 0, call.LogicalCallContext, call);
    }
  }
  [STAThread]
  private static void Main()
  {
    var type = typeof(MarkdownPanelController).Assembly.GetType("AnotherMarkdown.Forms.SyncedSourceCaret", true);
    using (var host = new Host { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ClientSize = new Size(400, 250) }) {
      host.Show();
      var focus = GetFocus();
      var source = new Source();
      bool enabled = true, mirror = true;
      var constructor = type.GetConstructor(new[] { typeof(IntPtr), typeof(IScintillaGateway), typeof(Func<bool>), typeof(Func<bool>) });
      var caret = constructor.Invoke(new object[] { host.Handle, source.GetTransparentProxy(), new Func<bool>(() => enabled), new Func<bool>(() => mirror) });
      var marker = (Control)type.GetField("marker", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(caret);
      Action refresh = () => { type.GetMethod("Refresh").Invoke(caret, null); Application.DoEvents(); };
      refresh();
      var window = marker.Handle;
      Check(IsWindowVisible(window) && source.Width == 2 && marker.Bounds == new Rectangle(120, 30, 2, 18),
        "native source and passive source both use a two-pixel insertion point");
      Check(GetLayeredWindowAttributes(window, out _, out var alpha, out var flags) && alpha == 255 && (flags & 2) != 0,
        "the caret has its own fully opaque compositor surface");
      Check(GetFocus() == focus && !source.Focused, "drawing does not change native or Scintilla keyboard focus");
      Check(!marker.Enabled && !marker.TabStop && SendMessage(window, 0x84, IntPtr.Zero, IntPtr.Zero).ToInt32() == -1,
        "marker cannot intercept clicks or tab navigation");
      source.X = 190; source.Y = 55; refresh();
      Check(marker.Bounds == new Rectangle(190, 55, 2, 18), "marker moves with the synchronized insertion point");
      source.Focused = true; refresh();
      Check(IsWindowVisible(window), "a stale logical Scintilla focus flag cannot hide the caret while keyboard focus is elsewhere");
      source.Focused = false; refresh();
      Check(IsWindowVisible(window), "the insertion point stays visible across logical focus changes");
      source.X = 20; refresh();
      Check(!IsWindowVisible(window), "horizontal scrolling never paints a caret over the line-number margin");
      source.X = 190; source.Y = -8; refresh();
      Check(marker.Bounds == new Rectangle(190, 0, 2, 10), "partially visible wrapped line is clipped to the viewport");
      source.LineVisible = false; refresh();
      Check(!IsWindowVisible(window), "folded source line has no stray caret");
      source.LineVisible = true; source.Y = 55; source.Colour = new Colour(0xEEDDCC); refresh();
      Check(marker.BackColor == Color.FromArgb(0xCC, 0xDD, 0xEE), "caret follows the source theme colour");
      enabled = false; refresh();
      Check(!IsWindowVisible(window) && source.Width == 1, "leaving the Markdown panel hides the marker and restores the original width");
      enabled = true; refresh();
      Check(IsWindowVisible(window), "marker returns when its source is active again");
      mirror = false; refresh();
      Check(!IsWindowVisible(window) && source.Width == 2, "disabling synchronization keeps the local caret at two pixels");
      mirror = true; refresh();
      source.Period = 140; source.Position++; refresh();
      Check(WaitForVisibility(window, false), "the passive source caret enters its off phase while idle");
      source.Position++; refresh();
      Check(IsWindowVisible(window), "moving during the off phase immediately shows the new insertion point");
      Check(WaitForVisibility(window, false) && WaitForVisibility(window, true) && WaitForVisibility(window, false),
        "the source keeps blinking through repeated timer refreshes");
      source.Period = 0; refresh();
      Check(source.Period == 500, "a source with no native blink period receives a temporary half-second interval");
      enabled = false; refresh();
      Check(source.Period == 0, "leaving the Markdown view restores its original native period");
      enabled = true; refresh(); source.Period = 375; source.Width = 3; refresh();
      ((IDisposable)caret).Dispose();
      Check(source.Period == 375 && source.Width == 3, "subsequent native preference changes survive disposal");
      Check(!IsWindow(window) && GetFocus() == focus, "disposing the marker destroys its window without changing focus");
      host.Close();
    }
    Console.WriteLine("PASS source caret visibility: " + checks + " assertions");
  }
  private static bool WaitForVisibility(IntPtr marker, bool visible)
  {
    var wait = Stopwatch.StartNew();
    do {
      Application.DoEvents();
      if (IsWindowVisible(marker) == visible) return true;
      System.Threading.Thread.Sleep(5);
    } while (wait.ElapsedMilliseconds < 1500);
    return false;
  }
  [DllImport("user32.dll")] private static extern IntPtr GetFocus();
  [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
  [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr window, out uint colour, out byte alpha, out uint flags);
  [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
  [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
