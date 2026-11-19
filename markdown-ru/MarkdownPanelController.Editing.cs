using System;
using System.Collections.Generic;
using MarkdownRu.Editing;
using Kbg.NppPluginNET.PluginInfrastructure;
using PanelCommon;

namespace MarkdownRu
{
  public partial class MarkdownPanelController
  {
    private readonly Dictionary<IntPtr, EditorSourceSession> editorSources = new Dictionary<IntPtr, EditorSourceSession>();
    private IntPtr CurrentBufferId() => Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_GETCURRENTBUFFERID, 0, 0);
    private IntPtr editingBuffer;
    private IScintillaGateway formatView;

    private void AttachFormatView(IScintillaGateway gateway)
    {
      editingBuffer = CurrentBufferId();
      if (formatView == null) {
        var handle = Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_CREATESCINTILLAHANDLE, IntPtr.Zero, _previewForm.Handle);
        if (handle != IntPtr.Zero) formatView = new ScintillaGateway(handle);
      }
      // The auxiliary view holds its own document reference across a native tab switch.
      formatView?.SetDocPointer(gateway.GetDocPointer());
    }

    private void DetachFormatView()
    {
      editingBuffer = IntPtr.Zero;
      formatView?.SetDocPointer(IntPtr.Zero);
    }

    private void FinishSourceFormatting()
    {
      if (editingBuffer == IntPtr.Zero || !editorSources.TryGetValue(editingBuffer, out var session)) return;
      var active = editingBuffer == CurrentBufferId();
      var gateway = active ? scintillaGatewayFactory() : formatView;
      if (gateway == null) return;
      var current = gateway.GetText(gateway.GetLength() + 1);
      var draft = _previewForm.GetEditorDraft(session.Id);
      if (draft != null && draft.Content != current) {
        // A pending conflict remains available when the user returns to the tab.
        if (draft.BaseText != current || gateway.GetReadOnly() || draft.Content == null) return;
        SourceTextEdit.Replace(gateway, current, draft.Content);
        current = draft.Content;
      }
      if (gateway.GetReadOnly() && session.NeedsRestore(current)) return;
      current = session.EndEdit(gateway, current, !active);
      _previewForm.AcknowledgeEditorDraft(draft);
      DetachFormatView();
      if (active) _previewForm.AcceptSourceText(current, gateway.GetModify(), false);
    }

    private EditorSourceSession GetEditorSession(string path)
    {
      var buffer = CurrentBufferId();
      if (!editorSources.TryGetValue(buffer, out var session)) editorSources[buffer] = session = new EditorSourceSession();
      session.TrackPath(path);
      return session;
    }
    private void RecordSourceSave(IntPtr buffer)
    {
      if (editorSources.TryGetValue(buffer, out var session))
        session.FileSaved(buffer == CurrentBufferId() ? _nppGateway.GetCurrentFilePath() : session.Path);
    }
    private EditorReply HandleSourceEdit(EditorRequest request)
    {
      var path = _nppGateway.GetCurrentFilePath();
      var session = GetEditorSession(path);
      if (request.Session != session.Id || _previewForm.IsTranslationPreview) {
        RenderMarkdown(force: true);
        return new EditorReply { Status = "stale" };
      }
      var gateway = scintillaGatewayFactory();
      var current = gateway.GetText(gateway.GetLength() + 1);
      _previewForm.AcceptSourceText(current, gateway.GetModify());
      var snapshot = _previewForm.GetEditorSnapshot();
      snapshot.ReadOnly = gateway.GetReadOnly();
      if (request.Action == "endEdit") {
        current = session.EndEdit(gateway, current);
        if (editingBuffer == CurrentBufferId()) DetachFormatView();
        _previewForm.AcceptSourceText(current, gateway.GetModify(), false);
        if (request.Navigation?.Session == session.Id && current == request.Content)
          ApplyNavigation(gateway, _previewForm.GetEditorSnapshot(), request.Navigation);
        RenderMarkdown(force: true);
        return new EditorReply { Status = "applied", Snapshot = _previewForm.GetEditorSnapshot() };
      }
      if (snapshot.ReadOnly) return new EditorReply { Status = "error", Message = "Документ открыт только для чтения.", Snapshot = snapshot };
      if (request.Action == "image") {
        return new EditorReply { Status = "image", Url = ImageAsset.Save(path, request.Data), Snapshot = snapshot };
      }
      if (request.Action != "apply" && request.Action != "beginEdit" && request.Action != "save" && request.Action != "saveAs")
        return new EditorReply { Status = "error", Message = "Неизвестная команда редактора." };
      if (request.BaseText != current) return new EditorReply { Status = "conflict", Snapshot = snapshot };
      if (request.Content == null) return new EditorReply { Status = "error", Message = "Не получен текст документа." };

      // This check and replacement run together on the Notepad++ UI thread.
      var modified = gateway.GetModify();
      SourceTextEdit.Replace(gateway, current, request.Content);
      if (request.Action == "beginEdit" && !session.VisualEditing) {
        session.BeginEdit(current, request.Content, modified);
        AttachFormatView(gateway);
      }
      current = request.Content;
      _previewForm.AcceptSourceText(current, gateway.GetModify(), session.VisualEditing);
      if (request.Navigation?.Session == session.Id)
        ApplyNavigation(gateway, _previewForm.GetEditorSnapshot(), request.Navigation);
      RenderMarkdown();
      if (request.Action == "apply" || request.Action == "beginEdit") return new EditorReply { Status = "applied", Snapshot = _previewForm.GetEditorSnapshot() };
      if (request.Action == "save") {
        if (!session.CanSave())
          return new EditorReply { Status = "error", Message = "Не удалось подтвердить, что файл на диске совпадает с открытой версией. Проверь файл в Notepad++. Визуальные правки остаются во вкладке.", Snapshot = _previewForm.GetEditorSnapshot() };
        var saved = Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_SAVECURRENTFILE, 0, 0) != IntPtr.Zero;
        if (!saved) return new EditorReply { Status = "error", Message = "Файл не сохранён. Изменения остаются во вкладке Notepad++.", Snapshot = _previewForm.GetEditorSnapshot() };
      }
      else {
        var saved = session.SaveAs(() => Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_MENUCOMMAND, 0, NppMenuCmd.IDM_FILE_SAVEAS));
        if (!saved) return new EditorReply { Status = "cancelled", Snapshot = _previewForm.GetEditorSnapshot() };
      }
      if (!editorSources.TryGetValue(CurrentBufferId(), out var active) || active != session)
        return new EditorReply { Status = "stale" };
      _previewForm.AcceptSourceText(current, gateway.GetModify());
      return new EditorReply { Status = gateway.GetModify() ? "cancelled" : "saved", Snapshot = _previewForm.GetEditorSnapshot() };
    }
  }
}
