using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using AnotherMarkdown.Entities;
using AnotherMarkdown.Forms;
using PanelCommon;

internal static class EditorDraftTests
{
  private static int checks;
  private static void Check(bool value, string label) { if (!value) throw new Exception(label); checks++; }
  private static void Set(MarkdownPreviewForm form, string name, object value) =>
    typeof(MarkdownPreviewForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(form, value);
  private static object Call(MarkdownPreviewForm form, string name, params object[] args) =>
    typeof(MarkdownPreviewForm).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(form, args);
  [STAThread] private static int Main()
  {
    try {
      var settings = new Settings();
      var constructor = typeof(MarkdownPreviewForm).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
        null, new[] { typeof(Settings), typeof(Func<string, string, bool, Task>) }, null);
      Func<string, string, bool, Task> renderer = (text, path, translated) => Task.CompletedTask;
      using (var form = (MarkdownPreviewForm)constructor.Invoke(new object[] { settings, renderer })) {
        foreach (var showAddresses in new[] { false, true }) {
          settings.ShowStatusbar = showAddresses;
          form.UpdateSettings(settings); form.PerformLayout();
          Check(form.Controls.Find("statusStrip2", true).Length == 0, "link-address preference never restores a separate preview footer");
          var content = form.Controls.Find("panelPreview", true)[0];
          Check(content.Bottom == form.ClientSize.Height, "preview content uses the full client height");
          Check(new ProxySettings(settings).ShowLinkAddresses == showAddresses, "existing link-address preference reaches the browser settings");
        }
        var toolbar = (ToolStrip)typeof(MarkdownPreviewForm).GetField("tbPreview", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
        var edit = (ToolStripButton)toolbar.Items["editor_edit"];
        var preview = (ToolStripButton)toolbar.Items["editor_preview"];
        var save = (ToolStripButton)toolbar.Items["editor_save"];
        var status = (ToolStripLabel)toolbar.Items["editorStatus"];
        Check(edit.Owner == toolbar && preview.Owner == toolbar && save.Owner == toolbar && status.Owner == toolbar, "editor actions share the native translation toolbar");
        var sourceCalls = 0;
        form.EditSource = request => { sourceCalls++; return new EditorReply { Status = "applied" }; };
        form.SetSourceState("first", false, false);
        form.RenderMarkdown("Original.\n", @"D:\notes\first.md").GetAwaiter().GetResult();
        Call(form, "UpdateEditorToolbar", new EditorUiState { Session = "first", Status = "Synchronized" });
        Check(edit.Enabled && !preview.Enabled && !save.Available && status.Text == "Synchronized", "source preview exposes edit and hides redundant Save");
        Check(status.Alignment == ToolStripItemAlignment.Right, "synchronization status aligns to the right edge");
        Call(form, "UpdateEditorToolbar", new EditorUiState { Session = "stale", Editing = true, Status = "Wrong" });
        Check(edit.Enabled && status.Text == "Synchronized", "stale UI state cannot replace active toolbar state");
        Call(form, "UpdateEditorToolbar", new EditorUiState { Session = "first", Editing = true });
        Check(!edit.Enabled && preview.Enabled, "edit mode exposes preview action");
        Set(form, "translatedText", "Translated.\n"); Set(form, "translationIdentity", "translated-first");
        Set(form, "translatedSourcePath", @"D:\notes\first.md");
        ((Task)Call(form, "TranslateCurrentAsync")).GetAwaiter().GetResult();
        Call(form, "UpdateEditorToolbar", new EditorUiState { Session = "translated-first", Translated = true });
        Check(save.Available, "translation retains its separate Save As action");
        var reply = (EditorReply)Call(form, "HandleEditorRequest", new EditorRequest {
          Action = "apply", Session = "translated-first", BaseText = "Translated.\n", Content = "Edited translation.\n"
        });
        Check(reply.Status == "applied" && sourceCalls == 0, "translation edit never reaches source writer");
        reply = (EditorReply)Call(form, "HandleEditorRequest", new EditorRequest { Action = "save", Session = "translated-first" });
        Check(reply.Status == "error" && sourceCalls == 0, "translation refuses ordinary Save");
        form.RenderMarkdown("Original changed.\n", @"D:\notes\first.md").GetAwaiter().GetResult();
        Check(form.IsTranslationPreview && form.GetEditorSnapshot().Text == "Edited translation.\n", "source changes preserve edited translation");
        form.SetSourceState("second", false, false);
        form.RenderMarkdown("Second file.\n", @"D:\notes\second.md").GetAwaiter().GetResult();
        Check(!form.IsTranslationPreview && form.GetEditorSnapshot().Text == "Second file.\n", "another file gets its own source state");
        reply = (EditorReply)Call(form, "HandleEditorRequest", new EditorRequest { Action = "apply", Session = "translated-first", Content = "Late edit" });
        Check(reply.Status == "stale" && sourceCalls == 0, "late translation cannot overwrite another file");
        form.SetSourceState("first", true, true);
        form.RenderMarkdown("Original changed.\n", @"D:\notes\first.md").GetAwaiter().GetResult();
        var snapshot = form.GetEditorSnapshot();
        Check(snapshot.Translated && snapshot.Session == "translated-first" && snapshot.Text == "Edited translation.\n", "returning to a tab restores its edited draft");
        Check(!snapshot.ReadOnly, "translation remains editable when source is read-only");
        form.RenderMarkdown("Original changed.\n", @"D:\moved\first.md").GetAwaiter().GetResult();
        Check(form.GetEditorSnapshot().AssetPath == @"D:\notes\first.md", "source rename preserves translated image base directory");
        var changedSettings = new Settings(); changedSettings.Translation.ShowButtons = false;
        Call(form, "UpdateTranslationSettings", changedSettings);
        Check(!form.IsTranslationPreview && form.GetEditorSnapshot().Text == "Original changed.\n", "hiding translation controls restores current original");
        ((Task)Call(form, "TranslateCurrentAsync")).GetAwaiter().GetResult();
        Check(form.GetEditorSnapshot().Text == "Edited translation.\n", "settings changes retain the edited translation");
      }
      Console.WriteLine("PASS editor drafts: " + checks + " assertions"); return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
  }
}
