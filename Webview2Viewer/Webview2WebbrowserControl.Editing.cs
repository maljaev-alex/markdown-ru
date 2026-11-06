using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using PanelCommon;
using Webview2Viewer.Web;
using WebView2 = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Webview2Viewer
{
  public partial class Webview2WebbrowserControl
  {
    public EditorSnapshot EditorSnapshot { get; set; }
    public Func<EditorRequest, EditorReply> EditorRequestHandler { get; set; }
    public Action<EditorUiState> EditorUiChanged { get; set; }
    public Action<EditorNavigation> EditorNavigationChanged { get; set; }
    public Task RunNavigationAsync(EditorNavigation state) => ExecuteWebviewActionAsync(webView =>
      webView.ExecuteScriptAsync("window.amReceiveNavigation && window.amReceiveNavigation(" +
        JsonConvert.SerializeObject(state, editorJson) + ")"));
    public Task RunEditorCommandAsync(string command, string session) => ExecuteWebviewActionAsync(webView =>
      webView.ExecuteScriptAsync("window.amEditorCommand && window.amEditorCommand(" +
        JsonConvert.SerializeObject(command, editorJson) + "," + JsonConvert.SerializeObject(session, editorJson) + ")"));
    private readonly Dictionary<string, EditorRequest> editorDrafts = new Dictionary<string, EditorRequest>();
    private readonly HashSet<string> editorSessions = new HashSet<string>();
    private static readonly JsonSerializerSettings editorJson = new JsonSerializerSettings {
      ContractResolver = new CamelCasePropertyNamesContractResolver(),
      StringEscapeHandling = StringEscapeHandling.EscapeHtml
    };

    private EditorSnapshot PrepareEditorSnapshot(EditorSnapshot snapshot)
    {
      if (snapshot == null) return null;
      editorSessions.Add(snapshot.Session);
      snapshot.PreserveLineBreaks = _settings.PreserveEditorLineBreaks;
      snapshot.ShowLinkAddresses = _settings.ShowLinkAddresses;
      snapshot.Dark = _settings.IsDarkModeEnabled;
      snapshot.SyncSelection = _settings.SyncViewWithCaretPosition;
      snapshot.SyncScroll = _settings.SyncViewWithFirstVisibleLine;
      snapshot.DocumentUrl = "http://local.example/" + HttpUtility2.PathToUri(snapshot.Path);
      snapshot.BaseUrl = "http://local.example/" + HttpUtility2.PathToUri(Path.GetDirectoryName(snapshot.AssetPath ?? snapshot.Path) ?? "") + "/";
      snapshot.DraftText = snapshot.DraftBase = null;
      if (editorDrafts.TryGetValue(snapshot.Session, out var draft) && draft.Content != draft.BaseText) {
        snapshot.DraftText = draft.Content;
        snapshot.DraftBase = draft.BaseText;
      }
      return snapshot;
    }

    private bool HandleEditorMessage(WebView2 webView, CoreWebView2WebMessageReceivedEventArgs args, JObject message)
    {
      if ((string)message?["type"] != "am.editor") return false;
      // NavigateToString documents run at about:blank. Do not grant a navigated page the bridge.
      if (args.Source != "about:blank" && !args.Source.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase)) return true;
      var request = message.ToObject<EditorRequest>();
      EditorReply reply;
      try {
        if (request.Action == "navigation") {
          var state = message["state"]?.ToObject<EditorNavigation>();
          if (state?.Session != null && state.Session == EditorSnapshot?.Session && !EditorSnapshot.Translated)
            EditorNavigationChanged?.Invoke(state);
          return true;
        }
        if (request.Action == "ui") {
          var state = message["state"]?.ToObject<EditorUiState>();
          if (state?.Session != null && state.Session == EditorSnapshot?.Session) EditorUiChanged?.Invoke(state);
          return true;
        }
        if (request.Action == "draft" && request.Session != null && editorSessions.Contains(request.Session)) {
          if (request.Content == request.BaseText) editorDrafts.Remove(request.Session);
          else editorDrafts[request.Session] = request;
          return true;
        }
        if (EditorRequestHandler == null) reply = new EditorReply { Status = "unavailable" };
        else reply = EditorRequestHandler(request) ?? new EditorReply { Status = "error" };
        if (reply.Status == "applied" && editorDrafts.TryGetValue(request.Session, out var draft) && draft.Content == request.Content)
          editorDrafts.Remove(request.Session);
        reply.Snapshot = PrepareEditorSnapshot(reply.Snapshot);
      }
      catch (Exception error) { reply = new EditorReply { Status = "error", Message = error.Message }; }
      reply.RequestId = request.RequestId;
      webView.CoreWebView2.PostWebMessageAsJson(JsonConvert.SerializeObject(new { type = "am.editor.reply", reply }, editorJson));
      return true;
    }
  }
}
