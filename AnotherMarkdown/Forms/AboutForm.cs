using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

namespace AnotherMarkdown.Forms
{
  public partial class AboutForm : Form
  {
    public AboutForm()
    {
      InitializeComponent();
      var versionString = "";
      try {
        var assembly = Assembly.GetExecutingAssembly();
        versionString = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version.ToString();
      }
      catch (Exception) { }
      tbAbout.Text = string.Format(AboutDialogText, PluginBranding.Name, versionString, PluginBranding.Repository, PluginBranding.Upstream)
        .Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
      btnOk.Focus();
      ActiveControl = btnOk;
    }

    private const string AboutDialogText =
      @"{0}
Версия {1}

Форк с переводом Markdown на русский язык:
{2}

Предпросмотр .md и .mdc в Notepad++, перевод через CLI или API.
Автопоиск CLI, список моделей и effort; сохранённые API-подключения.
Перевод отображается в предпросмотре. Исходный документ не изменяется.

Оригинальный AnotherMarkdown: Evgeny Zyuzin, 2025.
Исходный проект: {3}
Лицензия проекта: MIT. Благодарности авторам исходного проекта и библиотек.

Компоненты и ресурсы:
NotepadPlusPlusPluginPack.Net — https://github.com/kbilsted/NotepadPlusPlusPluginPack.Net
WebView2 (Microsoft) — https://developer.microsoft.com/microsoft-edge/webview2/
markdown-it — https://github.com/markdown-it/markdown-it
github-markdown-css — https://github.com/sindresorhus/github-markdown-css
Newtonsoft.Json — https://github.com/JamesNK/Newtonsoft.Json
DiffPlex — https://github.com/mmanela/diffplex
EdgeViewer — https://github.com/rg-software/wlx-edge-viewer
Markdown mark (исходные ресурсы) — https://github.com/dcurtis/markdown-mark
Части кода NppMarkdownPanel — https://github.com/mohzy83/NppMarkdownPanel
Части кода MarkdownViewerPlusPlus — https://github.com/nea/MarkdownViewerPlusPlus";

    private static void OpenRepository(object sender, LinkLabelLinkClickedEventArgs e)
    {
      Process.Start(new ProcessStartInfo(PluginBranding.Repository) { UseShellExecute = true });
    }
  }
}
