using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using AnotherMarkdown.Editing;
using Kbg.NppPluginNET.PluginInfrastructure;
using PanelCommon;

namespace AnotherMarkdown
{
  public partial class MarkdownPanelController
  {
    private sealed class SourceSession
    {
      public string Id = Guid.NewGuid().ToString("N");
      public string Path;
      public string DiskHash;
      public string ObservedText;
      public bool ObservedModified;
    }
    private readonly Dictionary<IntPtr, SourceSession> editorSources = new Dictionary<IntPtr, SourceSession>();
    private IntPtr CurrentBufferId() => Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_GETCURRENTBUFFERID, 0, 0);
    private static string DiskHash(string path)
    {
      if (!File.Exists(path)) return null;
      using (var hash = SHA256.Create())
      using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        return Convert.ToBase64String(hash.ComputeHash(stream));
    }
    private string GetEditorSession(string path)
    {
      var buffer = CurrentBufferId();
      if (!editorSources.TryGetValue(buffer, out var session)) editorSources[buffer] = session = new SourceSession();
      if (session.Path != path) { session.Path = path; session.DiskHash = DiskHash(path); }
      return session.Id;
    }
    private void EditorNotification(ScNotification notification)
    {
      if (notification.Header.Code == (uint)NppMsg.NPPN_FILECLOSED) editorSources.Remove(notification.Header.IdFrom);
      if (notification.Header.Code == (uint)NppMsg.NPPN_FILESAVED && editorSources.TryGetValue(notification.Header.IdFrom, out var session))
        session.DiskHash = DiskHash(session.Path);
      if (notification.Header.Code == (uint)NppMsg.NPPN_READONLYCHANGED || notification.Header.Code == (uint)NppMsg.NPPN_FILESAVED)
        RenderMarkdown(force: true);
    }
    private void ObserveCleanSource(string text, bool modified, int codePage)
    {
      if (!editorSources.TryGetValue(CurrentBufferId(), out var session)) return;
      if (session.ObservedText == text && session.ObservedModified == modified) return;
      session.ObservedText = text; session.ObservedModified = modified;
      if (modified || !File.Exists(session.Path)) return;
      // Notepad++ has no reload-completed notification. Accept a new disk baseline
      // only when a clean native buffer actually matches the file (including BOM decoding).
      try {
        var hash = DiskHash(session.Path);
        if (hash == session.DiskHash) return;
        using (var reader = new StreamReader(session.Path, Editing.SourceTextEdit.EncodingFor(codePage), true))
          if (reader.ReadToEnd() == text) session.DiskHash = hash;
      }
      catch (IOException) { }
      catch (UnauthorizedAccessException) { }
    }
    private EditorReply HandleSourceEdit(EditorRequest request)
    {
      var path = _nppGateway.GetCurrentFilePath();
      var sessionId = GetEditorSession(path);
      if (request.Session != sessionId || _previewForm.IsTranslationPreview) {
        RenderMarkdown(force: true);
        return new EditorReply { Status = "stale" };
      }
      var gateway = scintillaGatewayFactory();
      var current = gateway.GetText(gateway.GetLength() + 1);
      _previewForm.AcceptSourceText(current, gateway.GetModify());
      var snapshot = _previewForm.GetEditorSnapshot();
      snapshot.ReadOnly = gateway.GetReadOnly();
      if (snapshot.ReadOnly) return new EditorReply { Status = "error", Message = "Документ открыт только для чтения.", Snapshot = snapshot };
      if (request.Action == "image") {
        return new EditorReply { Status = "image", Url = ImageAsset.Save(path, request.Data), Snapshot = snapshot };
      }
      if (request.Action != "apply" && request.Action != "save" && request.Action != "saveAs")
        return new EditorReply { Status = "error", Message = "Неизвестная команда редактора." };
      if (request.BaseText != current) return new EditorReply { Status = "conflict", Snapshot = snapshot };
      if (request.Content == null) return new EditorReply { Status = "error", Message = "Не получен текст документа." };

      // This check and replacement run together on the Notepad++ UI thread.
      var edit = SourceTextEdit.Between(current, request.Content, SourceTextEdit.EncodingFor(gateway.GetCodePage()));
      if (edit != null) {
        gateway.BeginUndoAction();
        try {
          gateway.SetTargetStart(edit.Start); gateway.SetTargetEnd(edit.End);
          gateway.ReplaceTarget(edit.Length, edit.Text);
        }
        finally { gateway.EndUndoAction(); }
        current = gateway.GetText(gateway.GetLength() + 1);
        if (current != request.Content) throw new IOException("Notepad++ не подтвердил изменение текста.");
      }
      _previewForm.AcceptSourceText(current, gateway.GetModify());
      if (request.Navigation?.Session == sessionId)
        ApplyNavigation(gateway, _previewForm.GetEditorSnapshot(), request.Navigation);
      RenderMarkdown();
      if (request.Action == "apply") return new EditorReply { Status = "applied", Snapshot = _previewForm.GetEditorSnapshot() };
      var session = editorSources[CurrentBufferId()];
      if (request.Action == "save") {
        if (DiskHash(path) != session.DiskHash)
          return new EditorReply { Status = "error", Message = "Файл изменён другой программой. Сначала согласуй изменения в Notepad++; визуальные правки остаются во вкладке.", Snapshot = _previewForm.GetEditorSnapshot() };
        var saved = Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_SAVECURRENTFILE, 0, 0) != IntPtr.Zero;
        if (!saved) return new EditorReply { Status = "error", Message = "Файл не сохранён. Изменения остаются во вкладке Notepad++.", Snapshot = _previewForm.GetEditorSnapshot() };
      }
      else Win32.SendMessage(PluginBase.nppData._nppHandle, (uint)NppMsg.NPPM_MENUCOMMAND, 0, NppMenuCmd.IDM_FILE_SAVEAS);
      session.Path = _nppGateway.GetCurrentFilePath();
      session.DiskHash = DiskHash(session.Path);
      _previewForm.AcceptSourceText(current, gateway.GetModify());
      return new EditorReply { Status = gateway.GetModify() ? "cancelled" : "saved", Snapshot = _previewForm.GetEditorSnapshot() };
    }
  }
}
