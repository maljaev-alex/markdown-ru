using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using PanelCommon;

namespace Webview2Viewer.Web
{
  internal class LocalFileService: IWebService
  {
    public string DocumentUri { get; private set; }
    public string DocumentPath { get; private set; }
    public string Hostname { get; }
    public bool ReadOnly { get; set; }

    public LocalFileService(CoreWebView2Environment environment, string host, IEventDispatcher eventDispatcher)
    {
      _httpEnvironment = environment;
      Hostname = host;
      _on = eventDispatcher;
    }

    public void SetContent(string documentPath, string content)
    {
      DocumentPath = documentPath;
      DocumentUri = "/" + HttpUtility2.PathToUri(documentPath);
      _documentBytesData = Encoding.UTF8.GetBytes(content);
    }

    public bool HandleRequest(CoreWebView2WebResourceRequestedEventArgs e)
    {
      if (e.Response != null) {
        return false;
      }
      if (e.Request.Method == "GET") {
        var uri = new Uri(e.Request.Uri);
        HttpGetContent(e, uri);
        return true;
      }
      else if (e.Request.Method == "PUT") {
        var uri = new Uri(e.Request.Uri);
        HttpPutContent(e, uri);
        return true;
      }
      else if (e.Request.Method == "OPTIONS") {
        var uri = new Uri(e.Request.Uri);
        HttpOptionsContent(e, uri);
        return true;
      }
      return false;
    }

    private void HttpPutContent(CoreWebView2WebResourceRequestedEventArgs e, Uri requestUri)
    {
      if (ReadOnly) {
        e.Response = _httpEnvironment.CreateWebResourceResponse(new MemoryStream(), 403, "Read-only preview", "Access-Control-Allow-Origin: *");
        return;
      }
      if (!string.Equals(DocumentUri, requestUri.AbsolutePath, StringComparison.OrdinalIgnoreCase)) {
        Error404(e);
        return;
      }

      var headers = new List<string> {
        "Access-Control-Allow-Origin: *"
      };

      string requestBody;
      using (var reader = new StreamReader(e.Request.Content, Encoding.UTF8)) {
        requestBody = reader.ReadToEnd();
      }

      try {
        _on.DocumentChanged?.Invoke(this, new DocumentChangedEvent { Content = requestBody, DocumentPath = DocumentPath });
      }
      catch (Exception error) {
        Trace.TraceError("Markdown update failed: {0}", error);
        e.Response = _httpEnvironment.CreateWebResourceResponse(new MemoryStream(), 409, "Document update failed", string.Join("\r\n", headers));
        return;
      }
      e.Response = _httpEnvironment.CreateWebResourceResponse(new MemoryStream(), 200, "OK", string.Join("\r\n", headers));
    }

    private void HttpGetContent(CoreWebView2WebResourceRequestedEventArgs e, Uri requestUri)
    {
      var charset = "";
      byte[] bytes = null;
      var headers = new List<string> {
        "Access-Control-Allow-Origin: *"
      };

      if (Regex.IsMatch(requestUri.AbsolutePath, @"\*\.(\*|.+)$")) {
        var path = HttpUtility2.UriToPath(requestUri.AbsolutePath);
        var mask = Path.GetFileName(path);

        var dir = Path.GetDirectoryName(path);
        if (Directory.Exists(dir)) {
          var files = Directory.GetFiles(dir, mask, SearchOption.TopDirectoryOnly)
            .Select(li => new { name = Path.GetFileName(li), type = "file" })
            .ToArray();

          Json(e, headers, files);
          return;
        }
        Error404(e);
        return;
      }

      if (string.Equals(DocumentUri, requestUri.AbsolutePath, StringComparison.OrdinalIgnoreCase)) {
        bytes = _documentBytesData;
        charset = "; charset=utf-8";
      }
      else {
        var path = HttpUtility2.UriToPath(requestUri.AbsolutePath);
        if (File.Exists(path)) {
          bytes = File.ReadAllBytes(path);
        }
      }

      if (bytes == null) {
        Error404(e);
        return;
      }

      if (WebResource.MimeTypes.TryGetValue(Path.GetExtension(requestUri.AbsolutePath), out var contentType)) {
        headers.Add($"Content-Type: {contentType}{charset}");
      }
      else {
        headers.Add($"Content-Type: application/octet-stream");
      }

      e.Response = _httpEnvironment.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK", string.Join("\r\n", headers));
    }

    private void Json<TModel>(CoreWebView2WebResourceRequestedEventArgs e, List<string> headers, TModel data)
    {
      headers.Add("Content-Type: application/json; charset=utf-8");
      var stream = new MemoryStream(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(data)));
      e.Response = _httpEnvironment.CreateWebResourceResponse(stream, 200, "OK", string.Join("\r\n", headers));
    }

    private void Error404(CoreWebView2WebResourceRequestedEventArgs e)
    {
      e.Response = _httpEnvironment.CreateWebResourceResponse(new MemoryStream(), 404, "NotFound", $"");
    }

    private void HttpOptionsContent(CoreWebView2WebResourceRequestedEventArgs e, Uri uri)
    {
      var charset = "";
      var headers = new List<string> {
        "Access-Control-Allow-Origin: *",
        "Access-Control-Allow-Headers: *"
      };

      if (string.Equals(DocumentUri, uri.AbsolutePath, StringComparison.OrdinalIgnoreCase)) {
        charset = "; charset=utf-8";
        headers.Add(ReadOnly ? "Access-Control-Allow-Methods: GET, OPTIONS" : "Access-Control-Allow-Methods: GET, PUT, OPTIONS");
      }
      else {
        var path = HttpUtility2.UriToPath(uri.AbsolutePath);
        if (!File.Exists(path)) {
          Error404(e);
          return;
        }
        headers.Add("Access-Control-Allow-Methods: GET, OPTIONS");
      }

      if (WebResource.MimeTypes.TryGetValue(Path.GetExtension(uri.AbsolutePath), out var contentType) == false) {
        contentType = "application/octet-stream";
      }
      headers.Add($"Content-Type: {contentType}{charset}");

      e.Response = _httpEnvironment.CreateWebResourceResponse(new MemoryStream(), 204, "OK", string.Join("\r\n", headers));
    }

    private byte[] _documentBytesData;
    private readonly CoreWebView2Environment _httpEnvironment;
    private readonly IEventDispatcher _on;
  }
}
