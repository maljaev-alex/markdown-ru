using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

namespace MarkdownRu.Forms
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

Проект
{2}

Просмотр и визуальное редактирование Markdown в Notepad++.
Перевод через CLI или API сохраняется отдельным файлом.

В основе предпросмотра — AnotherMarkdown (Evgeny Zyuzin, 2025).
{3}
Лицензия исходной части — MIT. CKEditor 5 используется по GPL-2.0-or-later.

Компоненты и ресурсы
CKEditor 5 — https://github.com/ckeditor/ckeditor5
Markdig — https://github.com/xoofx/markdig
node-diff3 — https://github.com/bhollis/node-diff3
NotepadPlusPlusPluginPack.Net — https://github.com/kbilsted/NotepadPlusPlusPluginPack.Net
WebView2 (Microsoft) — https://developer.microsoft.com/microsoft-edge/webview2/
markdown-it — https://github.com/markdown-it/markdown-it
github-markdown-css — https://github.com/sindresorhus/github-markdown-css
Newtonsoft.Json — https://github.com/JamesNK/Newtonsoft.Json
DiffPlex (в ранних версиях) — https://github.com/mmanela/diffplex
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
