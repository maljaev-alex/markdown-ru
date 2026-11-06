using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using AnotherMarkdown.Entities;
using AnotherMarkdown.Forms;
using Kbg.NppPluginNET.PluginInfrastructure;

internal static class DockingFocusTests
{
  private static int checks;
  private const int ControlParent = 0x10000;

  [STAThread]
  private static void Main()
  {
    var assembly = typeof(MarkdownPreviewForm).Assembly;
    var constructor = typeof(MarkdownPreviewForm).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
      null, new[] { typeof(Settings), typeof(Func<string, string, bool, Task>) }, null);
    Func<string, string, bool, Task> render = (_, __, ___) => Task.CompletedTask;
    using (var preview = (MarkdownPreviewForm)constructor.Invoke(new object[] { new Settings(), render })) {
      var pluginBase = assembly.GetType("Kbg.NppPluginNET.PluginInfrastructure.PluginBase", true);
      pluginBase.GetField("nppData", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
        .SetValue(null, new NppData { _nppHandle = new IntPtr(12345) });
      Notify(preview, 0x41d); // DMN_FLOAT, before WebView2 finishes initializing.
      Check(!preview.IsHandleCreated, "floating notification does not force early native handles");
      var host = new Panel();
      var late = new LeafContainer();
      host.Controls.Add(late);
      preview.Controls.Add(host);
      var handle = late.Handle;
      Check(!late.HasChildren && (Style(late) & ControlParent) == 0,
        "late-created leaf container loses the traversal flag after the float notification");
      var nested = new LeafContainer();
      host.Controls.Add(nested);
      handle = nested.Handle;
      Check((Style(nested) & ControlParent) == 0, "nested controls added after initialization are protected");
      late.Recreate();
      Check((Style(late) & ControlParent) == 0, "recreated handles cannot restore the floating focus loop");
      SetStyle(late, Style(late) | ControlParent | 0x20);
      Check((Style(late) & ControlParent) == 0 && (Style(late) & 0x20) != 0,
        "native style changes remove only the unsafe traversal flag");
      Notify(preview, 0x41c); // DMN_DOCK
      late.Recreate();
      Check((Style(late) & ControlParent) != 0, "docked controls retain normal WinForms traversal styles");
      Notify(preview, 0x41d | (4 << 16));
      Check((Style(late) & ControlParent) == 0, "refloating clears existing styles with the container number packed in the notification");
      host.Controls.Remove(late);
      late.Recreate();
      Check((Style(late) & ControlParent) != 0, "detached controls are no longer guarded");
      host.Controls.Add(late);
      Check((Style(late) & ControlParent) == 0, "reattached controls are guarded again");
      var closes = 0;
      preview.DockClosed += (_, __) => closes++;
      Notify(preview, 0x41b);
      Check(closes == 1 && !preview.IsDisposed, "close notification leaves the preview reusable");
      preview.Dispose();
      Check(preview.IsDisposed, "guard subscriptions allow normal preview disposal");
    }
    Console.WriteLine("PASS docking focus: " + checks + " assertions");
  }

  private static void Notify(MarkdownPreviewForm preview, int code)
  {
    var pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Win32.NMHDR)));
    try {
      Marshal.StructureToPtr(new Win32.NMHDR { hwndFrom = new IntPtr(12345), code = code }, pointer, false);
      var message = Message.Create(IntPtr.Zero, Win32.WM_NOTIFY, IntPtr.Zero, pointer);
      typeof(MarkdownPreviewForm).GetMethod("WndProc", BindingFlags.NonPublic | BindingFlags.Instance)
        .Invoke(preview, new object[] { message });
    }
    finally { Marshal.FreeHGlobal(pointer); }
  }

  private static long Style(Control control) => Win32.GetWindowLongPtr(control.Handle, Win32.GWL_EXSTYLE).ToInt64();
  private static void SetStyle(Control control, long value) => Win32.SetWindowLongPtr(control.Handle, Win32.GWL_EXSTYLE, new IntPtr(value));
  private static void Check(bool condition, string label)
  {
    if (!condition) throw new Exception("FAIL " + label);
    checks++;
    Console.WriteLine("PASS " + label);
  }

  private sealed class LeafContainer : Control
  {
    protected override CreateParams CreateParams {
      get { var value = base.CreateParams; value.ExStyle |= ControlParent; return value; }
    }
    public void Recreate() => RecreateHandle();
  }
}
