using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Kbg.NppPluginNET.PluginInfrastructure;

namespace AnotherMarkdown {
  public partial class MarkdownPanelController {
    private readonly FileVisitHistory _fileVisits = new FileVisitHistory();
    private bool _panelAutoHidden;
    private bool _previewEnabled, _startupReady, _shuttingDown;
    private void RememberPreviewEnabled(bool enabled) {
      if (!_startupReady || _shuttingDown) return;
      _previewEnabled = enabled;
      try { Win32.WriteIniValue("Options", "PreviewEnabled", enabled ? "1" : "0", _iniFilePath); }
      catch (System.IO.IOException) { /* Retain the choice in memory; retry on shutdown. */ }
    }
    private readonly List<Icon> _historyIcons = new List<Icon>();
    private readonly List<IntPtr> _historyBitmaps = new List<IntPtr>();
    private static bool IsMarkdownFile(string path) => path != null &&
      (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mdc", StringComparison.OrdinalIgnoreCase));
    private void UpdateAutomaticPanel() {
      if (!_startupReady || _shuttingDown) return;
      if (!IsMarkdownFile(_nppGateway.GetCurrentFilePath())) {
        if (!_isPanelVisible || _previewForm == null) return;
        Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_DMMHIDE, 0, _previewForm.Handle);
        SetPanelVisibleState(false); _panelAutoHidden = true;
      } else if (_panelAutoHidden) { TogglePanelVisible(); }
    }
    private int BufferPosition(IntPtr id) => Win32.SendMessage(PluginBase.nppData._nppHandle,
      (uint)NppMsg.NPPM_GETPOSFROMBUFFERID, id, IntPtr.Zero).ToInt32();
    private void NavigateFileHistory(int direction) {
      _fileVisits.Record(CurrentBufferId());
      var target = _fileVisits.Move(direction, id => BufferPosition(id) >= 0);
      if (target == IntPtr.Zero) return;
      var position = BufferPosition(target);
      if (position >= 0) Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_ACTIVATEDOC,
        position >> 30, position & 0x3fffffff);
      UpdateHistoryButtons();
    }
    private void UpdateHistoryButtons() {
      if (PluginBase._funcItems.Items.Count < 10) return;
      var toolbar = HistoryFindWindow(PluginBase.nppData._nppHandle, IntPtr.Zero, "ToolbarWindow32", null);
      if (toolbar == IntPtr.Zero) {
        var rebar = HistoryFindWindow(PluginBase.nppData._nppHandle, IntPtr.Zero, "ReBarWindow32", null);
        if (rebar != IntPtr.Zero) toolbar = HistoryFindWindow(rebar, IntPtr.Zero, "ToolbarWindow32", null);
      }
      for (var i = 0; i < 2; i++) {
        var enabled = _fileVisits.CanMove(i == 0 ? -1 : 1, id => BufferPosition(id) >= 0);
        var command = PluginBase._funcItems.Items[8 + i]._cmdID;
        HistoryEnableMenu(Win32.GetMenu(PluginBase.nppData._nppHandle), (uint)command, enabled ? 0u : 1u);
        if (toolbar != IntPtr.Zero) Win32.SendMessage(toolbar, 0x0401, command, enabled ? 1 : 0);
      }
    }
    private static Bitmap HistoryArrow(int size, bool forward, bool dark) {
      var image = new Bitmap(size, size);
      using (var g = Graphics.FromImage(image)) {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(forward ? size : 0, 0); g.ScaleTransform(forward ? -1 : 1, 1);
        using (var pen = new Pen(dark ? Color.White : Color.FromArgb(40, 85, 150), size / 8f)) {
          pen.StartCap = pen.EndCap = LineCap.Round;
          g.DrawLines(pen, new[] { new PointF(size*.48f,size*.2f), new PointF(size*.18f,size*.5f), new PointF(size*.48f,size*.8f) });
          g.DrawLine(pen, size*.2f,size*.5f,size*.85f,size*.5f);
        }
      }
      return image;
    }
    private void RegisterHistoryButtons() {
      if (_historyIcons.Count == 0) for (var i = 0; i < 2; i++) {
        foreach (var dark in new[] { false, true }) using (var bmp = HistoryArrow(PluginIcon.ScaleForWindow(32, PluginBase.nppData._nppHandle), i == 1, dark)) {
          var handle = bmp.GetHicon();
          try { using (var icon = Icon.FromHandle(handle)) _historyIcons.Add((Icon)icon.Clone()); }
          finally { HistoryDestroyIcon(handle); }
        }
        using (var bmp = HistoryArrow(PluginIcon.ScaleForWindow(16, PluginBase.nppData._nppHandle), i == 1, false)) _historyBitmaps.Add(bmp.GetHbitmap());
      }
      for (var i = 0; i < 2; i++) {
        var icons = new toolbarIcons { hToolbarBmp = _historyBitmaps[i], hToolbarIcon = _historyIcons[i*2].Handle, hToolbarIconDarkMode = _historyIcons[i*2+1].Handle };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(icons));
        try {
          Marshal.StructureToPtr(icons, ptr, false);
          var result = Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_ADDTOOLBARICON_FORDARKMODE, PluginBase._funcItems.Items[8+i]._cmdID, ptr);
          if (result == IntPtr.Zero) Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_ADDTOOLBARICON, PluginBase._funcItems.Items[8+i]._cmdID, ptr);
        } finally { Marshal.FreeHGlobal(ptr); }
      }
      _fileVisits.Record(CurrentBufferId()); UpdateHistoryButtons();
    }
    private void DisposeHistoryIcons() {
      foreach (var icon in _historyIcons) icon.Dispose(); _historyIcons.Clear();
      foreach (var bitmap in _historyBitmaps) DeleteObject(bitmap); _historyBitmaps.Clear();
    }
    [DllImport("user32.dll", EntryPoint="FindWindowExW", CharSet=CharSet.Unicode)] private static extern IntPtr HistoryFindWindow(IntPtr parent, IntPtr after, string name, string title);
    [DllImport("user32.dll", EntryPoint="EnableMenuItem")] private static extern uint HistoryEnableMenu(IntPtr menu, uint item, uint flags);
    [DllImport("user32.dll", EntryPoint="DestroyIcon")] private static extern bool HistoryDestroyIcon(IntPtr icon);
  }
}
