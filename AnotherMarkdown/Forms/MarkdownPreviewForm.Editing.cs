using System;
using System.IO;
using System.Drawing;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;
using AnotherMarkdown.Editing;
using PanelCommon;

namespace AnotherMarkdown.Forms
{
  public partial class MarkdownPreviewForm
  {
    public Func<EditorRequest, EditorReply> EditSource { get; set; }
    private ToolStripButton editButton, previewButton, saveButton, conflictButton, retryButton;
    private ToolStripLabel editorStatus;
    private EditorUiState editorUi;

    private void InitializeEditorToolbar()
    {
      editButton = EditorButton("Редактирование", "edit");
      previewButton = EditorButton("Просмотр", "preview");
      saveButton = EditorButton("Сохранить как…", "save");
      conflictButton = EditorButton("Разобрать конфликт", "conflicts");
      retryButton = EditorButton("Повторить", "retry");
      saveButton.Visible = conflictButton.Visible = retryButton.Visible = false;
      editorStatus = new ToolStripLabel { Name = "editorStatus", Alignment = ToolStripItemAlignment.Right,
        TextAlign = ContentAlignment.MiddleRight, Overflow = ToolStripItemOverflow.Never };
      tbPreview.Items.AddRange(new ToolStripItem[] {
        new ToolStripSeparator(), previewButton, editButton, saveButton, conflictButton, retryButton, editorStatus
      });
    }

    private ToolStripButton EditorButton(string label, string command)
    {
      var button = new ToolStripButton(label) { Name = "editor_" + command, DisplayStyle = ToolStripItemDisplayStyle.Text, Enabled = false };
      button.Click += async (_, __) => {
        var session = GetEditorSnapshot().Session;
        if (_webView != null && editorUi?.Session == session) await _webView.RunEditorCommandAsync(command, session);
      };
      return button;
    }

    private void UpdateEditorToolbar(EditorUiState state)
    {
      if (_disposed || state?.Session != GetEditorSnapshot().Session) return;
      editorUi = state;
      editButton.Enabled = !state.Editing && !state.ReadOnly;
      previewButton.Enabled = state.Editing;
      saveButton.Enabled = !state.ReadOnly && !state.HasConflict;
      saveButton.Visible = state.Translated;
      conflictButton.Visible = state.HasConflict;
      retryButton.Visible = state.CanRetry;
      conflictButton.Enabled = retryButton.Enabled = true;
      editorStatus.Text = state.Status;
      editorStatus.ToolTipText = state.Status;
      editorStatus.ForeColor = state.Error ? Color.OrangeRed : tbPreview.ForeColor;
    }

    private void ResetEditorToolbar(string session)
    {
      if (editorUi?.Session == session) return;
      editorUi = null;
      editButton.Enabled = previewButton.Enabled = saveButton.Enabled = false;
      saveButton.Visible = conflictButton.Visible = retryButton.Visible = false;
      editorStatus.Text = "";
    }

    private string sourceIdentity, translationIdentity;
    private long editorRevision;
    private bool sourceReadOnly, sourceModified;
    private bool managedSourceIdentity;
    private string translatedSourcePath;
    private sealed class TranslationDraft
    {
      public string Text, Identity, SourcePath;
      public bool Visible;
    }
    private readonly Dictionary<string, TranslationDraft> translationDrafts = new Dictionary<string, TranslationDraft>();

    public void SetSourceState(string identity, bool readOnly, bool modified)
    {
      if (sourceIdentity != identity) {
        if (sourceIdentity != null && translatedText != null)
          translationDrafts[sourceIdentity] = new TranslationDraft { Text = translatedText, Identity = translationIdentity, Visible = IsTranslationPreview, SourcePath = translatedSourcePath };
        sourceIdentity = identity;
        CancelTranslation();
        translationDrafts.TryGetValue(identity, out var draft);
        IsTranslationPreview = draft?.Visible == true;
        translatedText = draft?.Text;
        translationIdentity = draft?.Identity;
        translatedSourcePath = draft?.SourcePath;
        editorRevision++;
        forceRender = true;
      }
      if (sourceReadOnly != readOnly || sourceModified != modified) forceRender = true;
      sourceReadOnly = readOnly;
      sourceModified = modified;
      managedSourceIdentity = true;
    }

    public EditorSnapshot GetEditorSnapshot() => new EditorSnapshot {
      Session = IsTranslationPreview ? translationIdentity : sourceIdentity,
      Path = sourcePath, Text = (IsTranslationPreview ? translatedText : sourceText) ?? "",
      AssetPath = IsTranslationPreview ? translatedSourcePath ?? sourcePath : sourcePath,
      Revision = editorRevision, Translated = IsTranslationPreview,
      ReadOnly = !IsTranslationPreview && sourceReadOnly, Modified = sourceModified
    };

    public void AcceptSourceText(string text, bool modified)
    {
      if (sourceText != text) { sourceText = text; editorRevision++; }
      sourceModified = modified;
    }

    private EditorReply HandleEditorRequest(EditorRequest request)
    {
      var snapshot = GetEditorSnapshot();
      if (request.Action == "state") return new EditorReply { Status = "state", Snapshot = snapshot };
      if (request.Session != snapshot.Session) return new EditorReply { Status = "stale", Snapshot = snapshot };
      if (IsTranslationPreview) {
        if (request.Action == "image") {
          ImageAsset.Decode(request.Data, out var _);
          return new EditorReply { Status = "image", Url = request.Data, Snapshot = snapshot };
        }
        if (request.Action != "apply" && request.Action != "saveAs")
          return new EditorReply { Status = "error", Message = "Перевод сохраняется только через «Сохранить как…»." };
        if (request.BaseText != translatedText)
          return new EditorReply { Status = "conflict", Snapshot = snapshot };
        if (request.Content != translatedText) { translatedText = request.Content; editorRevision++; }
        if (request.Action == "saveAs") return SaveTranslationAs();
        return new EditorReply { Status = "applied", Snapshot = GetEditorSnapshot() };
      }
      return EditSource?.Invoke(request) ?? new EditorReply { Status = "unavailable" };
    }

    private EditorReply SaveTranslationAs()
    {
      using (var dialog = new SaveFileDialog {
        Title = "Сохранить перевод как…", Filter = "Markdown (*.md)|*.md|Markdown MDC (*.mdc)|*.mdc",
        FileName = Path.GetFileNameWithoutExtension(sourcePath) + ".ru.md",
        InitialDirectory = Path.GetDirectoryName(sourcePath), DefaultExt = "md", AddExtension = true,
        OverwritePrompt = true
      }) {
        if (dialog.ShowDialog(this) != DialogResult.OK) return new EditorReply { Status = "cancelled", Snapshot = GetEditorSnapshot() };
        if (string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
          return new EditorReply { Status = "error", Message = "Выбери другой файл: перевод не должен перезаписывать оригинал.", Snapshot = GetEditorSnapshot() };
        TranslationExport.Save(translatedSourcePath ?? sourcePath, dialog.FileName, translatedText);
        return new EditorReply { Status = "saved", Message = dialog.FileName, Snapshot = GetEditorSnapshot() };
      }
    }
  }
}
