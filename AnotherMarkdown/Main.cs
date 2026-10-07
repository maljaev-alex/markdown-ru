using System;
using Kbg.NppPluginNET.PluginInfrastructure;

namespace AnotherMarkdown
{
  class Main
  {
    public static void OnNotification(ScNotification notification)
    {
      try {
        mdpanel?.OnNotification(notification);
      }
      catch (Exception) {
      }
    }

    internal static void CommandMenuInit()
    {
      // setInfo must supply Notepad++'s handles before the controller reads its config path.
      // A beforefieldinit static field initializer may run before that assignment.
      if (mdpanel == null) mdpanel = new MarkdownPanelController();
      mdpanel.InitCommandMenu();
    }

    internal static void SetToolBarIcon()
    {
      mdpanel?.SetToolBarIcon();
    }

    internal static void PluginCleanUp()
    {
      mdpanel?.PluginCleanUp();
    }

    // PluginName is used as npp plugin's menu entry
    public const string PluginName = PluginBranding.Name;
    // Modulename is used as config name (ini-file) and as _nppTbData.pszModuleName
    public const string ModuleName = "AnotherMarkdown";
    public const string PluginTitle = PluginBranding.Name;

    private static MarkdownPanelController mdpanel;
  }
}
