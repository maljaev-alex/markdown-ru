using System;
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
    public int X = 120, Y = 30;
    public bool Focused, LineVisible = true;
    public Colour Colour = new Colour(0x123456);
    public Source() : base(typeof(IScintillaGateway)) { }
    public override IMessage Invoke(IMessage message)
    {
      var call = (IMethodCallMessage)message; object result;
      switch (call.MethodName) {
        case "GetFocus": result = Focused; break;
        case "GetCurrentPos": result = 17; break;
        case "LineFromPosition": result = 2; break;
        case "GetLineVisible": result = LineVisible; break;
        case "GetMarginLeft": result = 2; break;
        case "GetMargins": result = 2; break;
        case "GetMarginWidthN": result = 20; break;
        case "PointXFromPosition": result = X; break;
        case "PointYFromPosition": result = Y; break;
        case "GetCaretWidth": result = 1; break;
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
      bool enabled = true;
      var constructor = type.GetConstructor(new[] { typeof(IntPtr), typeof(IScintillaGateway), typeof(Func<bool>) });
      var caret = constructor.Invoke(new object[] { host.Handle, source.GetTransparentProxy(), new Func<bool>(() => enabled) });
      var marker = (Control)type.GetField("marker", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(caret);
      Action refresh = () => { type.GetMethod("Refresh").Invoke(caret, null); Application.DoEvents(); };
      refresh();
      var window = marker.Handle;
      Check(IsWindowVisible(window) && marker.Bounds == new Rectangle(120, 30, 2, 18), "inactive source has a visible vertical caret at the actual text position");
      Check(GetFocus() == focus && !source.Focused, "drawing does not change native or Scintilla keyboard focus");
      Check(!marker.Enabled && !marker.TabStop && SendMessage(window, 0x84, IntPtr.Zero, IntPtr.Zero).ToInt32() == -1,
        "marker cannot intercept clicks or tab navigation");
      source.X = 190; source.Y = 55; refresh();
      Check(marker.Bounds == new Rectangle(190, 55, 2, 18), "marker moves with the synchronized insertion point");
      source.Focused = true; refresh();
      Check(!IsWindowVisible(window), "native focus removes the passive caret");
      source.Focused = false; refresh();
      Check(IsWindowVisible(window), "returning focus to the render restores the passive caret");
      source.X = 20; refresh();
      Check(!IsWindowVisible(window), "horizontal scrolling never paints a caret over the line-number margin");
      source.X = 190; source.Y = -8; refresh();
      Check(marker.Bounds == new Rectangle(190, 0, 2, 10), "partially visible wrapped line is clipped to the viewport");
      source.LineVisible = false; refresh();
      Check(!IsWindowVisible(window), "folded source line has no stray caret");
      source.LineVisible = true; source.Y = 55; source.Colour = new Colour(0xEEDDCC); refresh();
      Check(marker.BackColor == Color.FromArgb(0xCC, 0xDD, 0xEE), "caret follows the source theme colour");
      enabled = false; refresh();
      Check(!IsWindowVisible(window), "hidden panel, translation or changed source disables the marker");
      enabled = true; refresh();
      Check(IsWindowVisible(window), "marker returns when its source is active again");
      ((IDisposable)caret).Dispose();
      Check(!IsWindow(window) && GetFocus() == focus, "disposing the marker destroys its window without changing focus");
      host.Close();
    }
    Console.WriteLine("PASS source caret visibility: " + checks + " assertions");
  }
  [DllImport("user32.dll")] private static extern IntPtr GetFocus();
  [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
  [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
  [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
