using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Kbg.NppPluginNET.PluginInfrastructure;

namespace AnotherMarkdown.Forms
{
  // A native Notepad++ floating dialog cannot traverse nested WinForms
  // control parents reliably. WebView2 creates its controls asynchronously,
  // after DMN_FLOAT, and WinForms can restore the flag on handle recreation.
  internal sealed class DockingFocusGuard : IDisposable
  {
    private readonly Control root;
    private readonly Dictionary<Control, StyleWindow> tracked = new Dictionary<Control, StyleWindow>();
    private bool enabled;

    public DockingFocusGuard(Control root)
    {
      this.root = root;
      Track(root);
    }

    public bool Enabled
    {
      set {
        enabled = value;
        if (enabled) ClearTree(root);
      }
    }

    private void Track(Control control)
    {
      if (tracked.ContainsKey(control)) return;
      var window = new StyleWindow(this);
      tracked.Add(control, window);
      control.HandleCreated += ControlChanged;
      control.HandleDestroyed += HandleDestroyed;
      control.StyleChanged += ControlChanged;
      control.ControlAdded += ControlAdded;
      control.ControlRemoved += ControlRemoved;
      if (control.IsHandleCreated) window.AssignHandle(control.Handle);
      foreach (Control child in control.Controls) Track(child);
      Clear(control);
    }

    private void Untrack(Control control)
    {
      if (!tracked.TryGetValue(control, out var window)) return;
      tracked.Remove(control);
      control.HandleCreated -= ControlChanged;
      control.HandleDestroyed -= HandleDestroyed;
      control.StyleChanged -= ControlChanged;
      control.ControlAdded -= ControlAdded;
      control.ControlRemoved -= ControlRemoved;
      window.ReleaseHandle();
      foreach (Control child in control.Controls) Untrack(child);
    }

    private void ControlChanged(object sender, EventArgs args)
    {
      var control = (Control)sender;
      if (control.IsHandleCreated && tracked.TryGetValue(control, out var window) && window.Handle == IntPtr.Zero)
        window.AssignHandle(control.Handle);
      Clear(control);
    }

    private void HandleDestroyed(object sender, EventArgs args)
    {
      if (tracked.TryGetValue((Control)sender, out var window)) window.ReleaseHandle();
    }

    private void ControlAdded(object sender, ControlEventArgs args)
    {
      Track(args.Control);
      Clear((Control)sender);
    }

    private void ControlRemoved(object sender, ControlEventArgs args) => Untrack(args.Control);

    private void ClearTree(Control control)
    {
      Clear(control);
      foreach (Control child in control.Controls) ClearTree(child);
    }

    private void Clear(Control control)
    {
      // Do not create handles as a side effect, including hidden toolbar panels.
      // Leaf controls can also carry WS_EX_CONTROLPARENT without managed children.
      if (!enabled || !control.IsHandleCreated) return;
      var style = Environment.Is64BitProcess
        ? Win32.GetWindowLongPtr(control.Handle, Win32.GWL_EXSTYLE).ToInt64()
        : Win32.GetWindowLong(control.Handle, Win32.GWL_EXSTYLE).ToInt64();
      if ((style & Win32.WS_EX_CONTROLPARENT) == 0) return;
      var safeStyle = new IntPtr(style & ~Win32.WS_EX_CONTROLPARENT);
      if (Environment.Is64BitProcess) Win32.SetWindowLongPtr(control.Handle, Win32.GWL_EXSTYLE, safeStyle);
      else Win32.SetWindowLong(control.Handle, Win32.GWL_EXSTYLE, safeStyle);
    }

    public void Dispose()
    {
      enabled = false;
      Untrack(root);
    }

    private sealed class StyleWindow : NativeWindow
    {
      private readonly DockingFocusGuard owner;
      public StyleWindow(DockingFocusGuard owner) { this.owner = owner; }

      protected override void WndProc(ref Message message)
      {
        const int WM_STYLECHANGING = 0x7c;
        if (owner.enabled && message.Msg == WM_STYLECHANGING
          && message.WParam.ToInt64() == Win32.GWL_EXSTYLE && message.LParam != IntPtr.Zero) {
          // STYLESTRUCT contains two DWORDs on both x86 and x64.
          var style = Marshal.ReadInt32(message.LParam, 4);
          Marshal.WriteInt32(message.LParam, 4, style & ~(int)Win32.WS_EX_CONTROLPARENT);
        }
        base.WndProc(ref message);
      }
    }
  }
}
