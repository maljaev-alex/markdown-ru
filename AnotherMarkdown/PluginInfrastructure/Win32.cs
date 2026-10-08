// NPP plugin platform for .Net v0.94.00 by Kasper B. Graversen etc.
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace Kbg.NppPluginNET.PluginInfrastructure
{
  public class Win32
  {
    /// <summary>
    /// You should try to avoid calling this method in your plugin code. Rather use one of the gateways such as  <see
    /// cref="ScintillaGateway"/> or <see cref="NotepadPPGateway"/>.   If gateways are missing or incomplete, please
    /// help extend them and send your code to the project  at https://github.com/kbilsted/NotepadPlusPlusPluginPack.Net
    ///
    /// </summary>
    [DllImport("user32")]
    public static extern IntPtr SendMessage(IntPtr hWnd, UInt32 Msg, IntPtr wParam, [MarshalAs(UnmanagedType.LPWStr)] string lParam);

    /// <summary>
    /// You should try to avoid calling this method in your plugin code. Rather use one of the gateways such as  <see
    /// cref="ScintillaGateway"/> or <see cref="NotepadPPGateway"/>.   If gateways are missing or incomplete, please
    /// help extend them and send your code to the project  at https://github.com/kbilsted/NotepadPlusPlusPluginPack.Net
    ///
    /// </summary>
    [DllImport("user32")]
    public static extern IntPtr SendMessage(IntPtr hWnd, UInt32 Msg, IntPtr wParam, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder lParam);

    /// <summary>
    /// You should try to avoid calling this method in your plugin code. Rather use one of the gateways such as  <see
    /// cref="ScintillaGateway"/> or <see cref="NotepadPPGateway"/>.   If gateways are missing or incomplete, please
    /// help extend them and send your code to the project  at https://github.com/kbilsted/NotepadPlusPlusPluginPack.Net
    ///
    /// </summary>
    [DllImport("user32")]
    public static extern IntPtr SendMessage(IntPtr hWnd, UInt32 Msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// You should try to avoid calling this method in your plugin code. Rather use one of the gateways such as  <see
    /// cref="ScintillaGateway"/> or <see cref="NotepadPPGateway"/>.   If gateways are missing or incomplete, please
    /// help extend them and send your code to the project  at https://github.com/kbilsted/NotepadPlusPlusPluginPack.Net
    ///
    /// </summary>
    [DllImport("user32")]
    public static extern IntPtr SendMessage(IntPtr hWnd, UInt32 Msg, IntPtr wParam, out IntPtr lParam);

    /// <summary>
    /// You should try to avoid calling this method in your plugin code. Rather use one of the gateways such as  <see
    /// cref="ScintillaGateway"/> or <see cref="NotepadPPGateway"/>.   If gateways are missing or incomplete, please
    /// help extend them and send your code to the project  at https://github.com/kbilsted/NotepadPlusPlusPluginPack.Net
    ///
    /// </summary>
    public static IntPtr SendMessage(IntPtr hWnd, uint Msg, int wParam, NppMenuCmd lParam)
    {
      return SendMessage(hWnd, (UInt32)Msg, new IntPtr(wParam), new IntPtr((uint)lParam));
    }

    /// <summary>
    /// You should try to avoid calling this method in your plugin code. Rather use one of the gateways such as  <see
    /// cref="ScintillaGateway"/> or <see cref="NotepadPPGateway"/>.   If gateways are missing or incomplete, please
    /// help extend them and send your code to the project  at https://github.com/kbilsted/NotepadPlusPlusPluginPack.Net
    ///
    /// </summary>
    public static IntPtr SendMessage(IntPtr hWnd, uint Msg, int wParam, IntPtr lParam)
    {
      return SendMessage(hWnd, (UInt32)Msg, new IntPtr(wParam), lParam);
    }

    /// <summary>
    /// You should try to avoid calling this method in your plugin code. Rather use one of the gateways such as  <see
    /// cref="ScintillaGateway"/> or <see cref="NotepadPPGateway"/>.   If gateways are missing or incomplete, please
    /// help extend them and send your code to the project  at https://github.com/kbilsted/NotepadPlusPlusPluginPack.Net
    ///
    /// </summary>
    public static IntPtr SendMessage(IntPtr hWnd, uint Msg, int wParam, int lParam)
    {
      return SendMessage(hWnd, (UInt32)Msg, new IntPtr(wParam), new IntPtr(lParam));
    }

    /// <summary>
    /// You should try to avoid calling this method in your plugin code. Rather use one of the gateways such as  <see
    /// cref="ScintillaGateway"/> or <see cref="NotepadPPGateway"/>.   If gateways are missing or incomplete, please
    /// help extend them and send your code to the project  at https://github.com/kbilsted/NotepadPlusPlusPluginPack.Net
    ///
    /// </summary>
    public static IntPtr SendMessage(IntPtr hWnd, uint Msg, int wParam, out int lParam)
    {
      IntPtr outVal;
      IntPtr retval = SendMessage(hWnd, (UInt32)Msg, new IntPtr(wParam), out outVal);
      lParam = outVal.ToInt32();
      return retval;
    }

    /// <summary>
    /// You should try to avoid calling this method in your plugin code. Rather use one of the gateways such as  <see
    /// cref="ScintillaGateway"/> or <see cref="NotepadPPGateway"/>.   If gateways are missing or incomplete, please
    /// help extend them and send your code to the project  at https://github.com/kbilsted/NotepadPlusPlusPluginPack.Net
    ///
    /// </summary>
    public static IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, int lParam)
    {
      return SendMessage(hWnd, (UInt32)Msg, wParam, new IntPtr(lParam));
    }

    /// <summary>
    /// You should try to avoid calling this method in your plugin code. Rather use one of the gateways such as  <see
    /// cref="ScintillaGateway"/> or <see cref="NotepadPPGateway"/>.   If gateways are missing or incomplete, please
    /// help extend them and send your code to the project  at https://github.com/kbilsted/NotepadPlusPlusPluginPack.Net
    ///
    /// </summary>
    public static IntPtr SendMessage(IntPtr hWnd, uint Msg, int wParam, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder lParam)
    {
      return SendMessage(hWnd, (UInt32)Msg, new IntPtr(wParam), lParam);
    }

    /// <summary>
    /// You should try to avoid calling this method in your plugin code. Rather use one of the gateways such as  <see
    /// cref="ScintillaGateway"/> or <see cref="NotepadPPGateway"/>.   If gateways are missing or incomplete, please
    /// help extend them and send your code to the project  at https://github.com/kbilsted/NotepadPlusPlusPluginPack.Net
    ///
    /// </summary>
    public static IntPtr SendMessage(IntPtr hWnd, uint Msg, int wParam, [MarshalAs(UnmanagedType.LPWStr)] string lParam)
    {
      return SendMessage(hWnd, (UInt32)Msg, new IntPtr(wParam), lParam);
    }

    /// <summary>
    /// You should try to avoid calling this method in your plugin code. Rather use one of the gateways such as  <see
    /// cref="ScintillaGateway"/> or <see cref="NotepadPPGateway"/>.   If gateways are missing or incomplete, please
    /// help extend them and send your code to the project  at https://github.com/kbilsted/NotepadPlusPlusPluginPack.Net
    ///
    /// </summary>
    public static IntPtr SendMessage(IntPtr hWnd, SciMsg Msg, IntPtr wParam, int lParam)
    {
      return SendMessage(hWnd, (UInt32)Msg, wParam, new IntPtr(lParam));
    }

    /// <summary>
    /// You should try to avoid calling this method in your plugin code. Rather use one of the gateways such as  <see
    /// cref="ScintillaGateway"/> or <see cref="NotepadPPGateway"/>.   If gateways are missing or incomplete, please
    /// help extend them and send your code to the project  at https://github.com/kbilsted/NotepadPlusPlusPluginPack.Net
    ///
    /// </summary>
    public static IntPtr SendMessage(IntPtr hWnd, SciMsg Msg, int wParam, IntPtr lParam)
    {
      return SendMessage(hWnd, (UInt32)Msg, new IntPtr(wParam), lParam);
    }

    /// <summary>
    /// You should try to avoid calling this method in your plugin code. Rather use one of the gateways such as  <see
    /// cref="ScintillaGateway"/> or <see cref="NotepadPPGateway"/>.   If gateways are missing or incomplete, please
    /// help extend them and send your code to the project  at https://github.com/kbilsted/NotepadPlusPlusPluginPack.Net
    ///
    /// </summary>
    public static IntPtr SendMessage(IntPtr hWnd, SciMsg Msg, int wParam, string lParam)
    {
      return SendMessage(hWnd, (UInt32)Msg, new IntPtr(wParam), lParam);
    }

    /// <summary>
    /// You should try to avoid calling this method in your plugin code. Rather use one of the gateways such as  <see
    /// cref="ScintillaGateway"/> or <see cref="NotepadPPGateway"/>.   If gateways are missing or incomplete, please
    /// help extend them and send your code to the project  at https://github.com/kbilsted/NotepadPlusPlusPluginPack.Net
    ///
    /// </summary>
    public static IntPtr SendMessage(IntPtr hWnd, SciMsg Msg, int wParam, [MarshalAs(UnmanagedType.LPStr)] StringBuilder lParam)
    {
      return SendMessage(hWnd, (UInt32)Msg, new IntPtr(wParam), lParam);
    }

    /// <summary>
    /// You should try to avoid calling this method in your plugin code. Rather use one of the gateways such as  <see
    /// cref="ScintillaGateway"/> or <see cref="NotepadPPGateway"/>.   If gateways are missing or incomplete, please
    /// help extend them and send your code to the project  at https://github.com/kbilsted/NotepadPlusPlusPluginPack.Net
    ///
    /// </summary>
    public static IntPtr SendMessage(IntPtr hWnd, SciMsg Msg, int wParam, int lParam)
    {
      return SendMessage(hWnd, (UInt32)Msg, new IntPtr(wParam), new IntPtr(lParam));
    }

    /// <summary>
    /// You should try to avoid calling this method in your plugin code. Rather use one of the gateways such as  <see
    /// cref="ScintillaGateway"/> or <see cref="NotepadPPGateway"/>.   If gateways are missing or incomplete, please
    /// help extend them and send your code to the project  at https://github.com/kbilsted/NotepadPlusPlusPluginPack.Net
    ///
    /// </summary>
    public static IntPtr SendMessage(IntPtr hWnd, SciMsg Msg, IntPtr wParam, IntPtr lParam)
    {
      return SendMessage(hWnd, (UInt32)Msg, wParam, lParam);
    }

    /// <summary>
    /// You should try to avoid calling this method in your plugin code. Rather use one of the gateways such as <see
    /// cref="ScintillaGateway"/> or <see cref="NotepadPPGateway"/>. If gateways are missing or incomplete, please help
    /// extend them and send your code to the project at https://github.com/kbilsted/NotepadPlusPlusPluginPack.Net
    /// </summary>
    public static IntPtr SendMessage(IntPtr hWnd, uint Msg, int wParam, ref LangType lParam)
    {
      IntPtr outVal;
      IntPtr retval = SendMessage(hWnd, (UInt32)Msg, new IntPtr(wParam), out outVal);
      lParam = (LangType)outVal;
      return retval;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    public static extern int GetPrivateProfileInt(string lpAppName, string lpKeyName, int nDefault, string lpFileName);

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    public static extern int GetPrivateProfileString(
      string lpAppName,
      string lpKeyName,
      string lpDefault,
      StringBuilder lpReturnedString,
      int nSize,
      string lpFileName);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool WritePrivateProfileString(string lpAppName, string lpKeyName, string lpString, string lpFileName);

    [DllImport("user32")]
    public static extern IntPtr GetMenu(IntPtr hWnd);

    [DllImport("user32")]
    public static extern int CheckMenuItem(IntPtr hMenu, int uIDCheckItem, int uCheck);

    [DllImport("user32")]
    public static extern bool EnableMenuItem(IntPtr hMenu, int uIDEnableItem, int uEnable);

    [DllImport("user32")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32")]
    public static extern IntPtr GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32")]
    public static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32")]
    public static extern bool ClientToScreen(IntPtr hWnd, ref Point lpPoint);

    [DllImport("kernel32")]
    public static extern void OutputDebugString(string lpOutputString);

    /// <summary>
    /// @see https://msdn.microsoft.com/en-us/library/windows/desktop/bb787583(v=vs.85).aspx
    /// </summary>
    /// <param name="hwnd"></param>
    /// <param name="nBar"></param>
    /// <param name="scrollInfo"></param>
    /// <returns></returns>
    [DllImport("user32")]
    public static extern int GetScrollInfo(IntPtr hwnd, int nBar, ref ScrollInfo scrollInfo);

    public static string ReadIniValue(string section, string key, string iniFileName, string defaultValue = "")
    {
      // CLI argument templates and long paths exceed the old 254-character limit.
      // A WinForms TextBox can save 32767 characters; allow an extra probe
      // beyond that boundary so a full value is distinguishable from truncation.
      for (var capacity = 512; capacity <= 65536; capacity *= 2) {
        var temp = new StringBuilder(capacity);
        var length = GetPrivateProfileString(section, key, defaultValue, temp, capacity, iniFileName);
        if (length < capacity - 1) return temp.ToString();
      }
      throw new InvalidOperationException("INI value is longer than 65534 characters: " + section + "/" + key);
    }

    public static void WriteIniValue(string section, string key, string value, string iniFileName)
    {
      try { PrepareIniValueEncoding(section, key, value, iniFileName); }
      catch (UnauthorizedAccessException error) { throw new System.IO.IOException("Cannot access the settings file: " + iniFileName, error); }
      if (!WritePrivateProfileString(section, key, value, iniFileName)) {
        var error = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        throw new System.IO.IOException("Не удалось записать настройки в файл: " + iniFileName + ".\r\nWindows (" + error.NativeErrorCode + "): " + error.Message, error);
      }
    }

    private static void PrepareIniValueEncoding(string section, string key, string value, string iniFileName)
    {
      // New profiles use UTF-16; existing profiles retain their original encoding.
      // The W API still converts values to ACP in legacy ANSI files, silently
      // replacing unsupported characters. Reject that conversion before writing.
      if (!System.IO.File.Exists(iniFileName) && System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(iniFileName)))) {
        try {
          using (var stream = new System.IO.FileStream(iniFileName, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write)) {
            var preamble = Encoding.Unicode.GetPreamble();
            stream.Write(preamble, 0, preamble.Length);
          }
        }
        catch (System.IO.IOException) when (System.IO.File.Exists(iniFileName)) { }
      }
      if (value != null && value != Encoding.Default.GetString(Encoding.Default.GetBytes(value)) && System.IO.File.Exists(iniFileName)) {
        bool unicode;
        using (var stream = new System.IO.FileStream(iniFileName, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
          unicode = stream.ReadByte() == 0xff && stream.ReadByte() == 0xfe;
        if (!unicode) throw new System.IO.IOException("The existing ANSI settings file cannot preserve Unicode characters in " + section + "/" + key + ". Save the INI as UTF-16 LE with BOM before retrying: " + iniFileName);
      }
    }

    /// <summary>
    /// Get the scroll information of a scroll bar or window with scroll bar @see https://msdn.microsoft.com/en-
    /// us/library/windows/desktop/bb787537(v=vs.85).aspx
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ScrollInfo
    {
      /// <summary>
      /// Specifies the size, in bytes, of this structure. The caller must set this to sizeof(SCROLLINFO).
      /// </summary>
      public uint cbSize;
      /// <summary>
      /// Specifies the scroll bar parameters to set or retrieve. @see ScrollInfoMask
      /// </summary>
      public uint fMask;
      /// <summary>
      /// Specifies the minimum scrolling position.
      /// </summary>
      public int nMin;
      /// <summary>
      /// Specifies the maximum scrolling position.
      /// </summary>
      public int nMax;
      /// <summary>
      /// Specifies the page size, in device units. A scroll bar uses this value to determine the appropriate size of
      /// the proportional scroll box.
      /// </summary>
      public uint nPage;
      /// <summary>
      /// Specifies the position of the scroll box.
      /// </summary>
      public int nPos;
      /// <summary>
      /// Specifies the immediate position of a scroll box that the user is dragging.  An application can retrieve this
      /// value while processing the SB_THUMBTRACK request code.  An application cannot set the immediate scroll
      /// position; the SetScrollInfo function ignores this member.
      /// </summary>
      public int nTrackPos;
    }

    /// <summary>
    /// Used for the ScrollInfo fMask SIF_ALL             => Combination of SIF_PAGE, SIF_POS, SIF_RANGE, and
    /// SIF_TRACKPOS. SIF_DISABLENOSCROLL => This value is used only when setting a scroll bar's parameters. If the
    /// scroll bar's new parameters make the scroll bar unnecessary, disable the scroll bar instead of removing it.
    /// SIF_PAGE            => The nPage member contains the page size for a proportional scroll bar. SIF_POS           
    ///  => The nPos member contains the scroll box position, which is not updated while the user drags the scroll box.
    /// SIF_RANGE           => The nMin and nMax members contain the minimum and maximum values for the scrolling range.
    /// SIF_TRACKPOS        => The nTrackPos member contains the current position of the scroll box while the user is
    /// dragging it.
    /// </summary>
    public enum ScrollInfoMask
    {
      SIF_RANGE = 0x1,
      SIF_PAGE = 0x2,
      SIF_POS = 0x4,
      SIF_DISABLENOSCROLL = 0x8,
      SIF_TRACKPOS = 0x10,
      SIF_ALL = SIF_RANGE + SIF_PAGE + SIF_POS + SIF_TRACKPOS
    }

    /// <summary>
    /// Used for the GetScrollInfo() nBar parameter
    /// </summary>
    public enum ScrollInfoBar
    {
      SB_HORZ = 0,
      SB_VERT = 1,
      SB_CTL = 2,
      SB_BOTH = 3
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NMHDR
    {
      public IntPtr hwndFrom;
      public IntPtr idFrom;
      public int code;
    }

    public const int MAX_PATH = 260;
    public const int MF_BYCOMMAND = 0;
    public const int MF_CHECKED = 8;
    public const int MF_UNCHECKED = 0;
    public const int MF_ENABLED = 0;
    public const int MF_GRAYED = 1;
    public const int MF_DISABLED = 2;
    public const int WM_CREATE = 1;
    public const int WM_NOTIFY = 0x004E;

    public const int GWL_EXSTYLE = -20;
    public const int GWLP_HINSTANCE = -6;
    public const int GWLP_HWNDPARENT = -8;
    public const int GWLP_ID = -12;
    public const int GWL_STYLE = -16;
    public const int GWLP_USERDATA = -21;
    public const int GWLP_WNDPROC = -4;
    public const long WS_EX_ACCEPTFILES = 0x00000010L;
    public const long WS_EX_APPWINDOW = 0x00040000L;
    public const long WS_EX_CLIENTEDGE = 0x00000200L;
    public const long WS_EX_COMPOSITED = 0x02000000L;
    public const long WS_EX_CONTEXTHELP = 0x00000400L;
    public const long WS_EX_CONTROLPARENT = 0x00010000L;
    public const long WS_EX_DLGMODALFRAME = 0x00000001L;
    public const long WS_EX_LAYERED = 0x00080000L;
    public const long WS_EX_LAYOUTRTL = 0x00400000L;
    public const long WS_EX_LEFT = 0x00000000L;
    public const long WS_EX_LEFTSCROLLBAR = 0x00004000L;
    public const long WS_EX_LTRREADING = 0x00000000L;
    public const long WS_EX_MDICHILD = 0x00000040L;
    public const long WS_EX_NOACTIVATE = 0x08000000L;
    public const long WS_EX_NOINHERITLAYOUT = 0x00100000L;
    public const long WS_EX_NOPARENTNOTIFY = 0x00000004L;
    public const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000L;
    public const long WS_EX_OVERLAPPEDWINDOW = (WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE);
    public const long WS_EX_PALETTEWINDOW = (WS_EX_WINDOWEDGE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST);
    public const long WS_EX_RIGHT = 0x00001000L;
    public const long WS_EX_RIGHTSCROLLBAR = 0x00000000L;
    public const long WS_EX_RTLREADING = 0x00002000L;
    public const long WS_EX_STATICEDGE = 0x00020000L;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_TOPMOST = 0x00000008L;
    public const long WS_EX_TRANSPARENT = 0x00000020L;
    public const long WS_EX_WINDOWEDGE = 0x00000100L;
  }
}
