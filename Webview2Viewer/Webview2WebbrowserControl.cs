using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PanelCommon;
using Webview2Viewer.Web;
using WebView2 = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Webview2Viewer
{
  public partial class Webview2WebbrowserControl : IDisposable
  {

    public Action RenderingDoneAction { get; set; }

    public Webview2WebbrowserControl() : this(null) { }

    internal Webview2WebbrowserControl(string imagePlacementFile)
    {
      _imagePlacementFile = imagePlacementFile ?? ImageWindowPlacement.DefaultFile;
    }

    public void Dispose()
    {
      if (_disposed) return;
      _disposed = true;
      _imageWindow?.Dispose();
      _webView?.Dispose();
    }

    public void Initialize(ISettings settings, IEventDispatcher eventDispatcher)
    {
      if (_disposed) throw new ObjectDisposedException(nameof(Webview2WebbrowserControl));
      if (_webView != null) return;
      _settings = settings;
      _on = eventDispatcher;
      _webView = new WebView2 { AccessibleName = "webView", Name = "webView",
        Location = new Point(1, 27), Size = new Size(800, 424), Dock = DockStyle.Fill, TabIndex = 0 };
      _initialization = InitializeWebViewAsync(_webView);
    }

    private async Task InitializeWebViewAsync(WebView2 webView)
    {
      var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), CONFIG_FOLDER_NAME, "webview2");
      try {
        var environment = await CoreWebView2Environment.CreateAsync(null, cacheDir, new CoreWebView2EnvironmentOptions());
        if (_disposed || webView.IsDisposed) return;
        await webView.EnsureCoreWebView2Async(environment);
        if (!_disposed && !webView.IsDisposed) ConfigureWebView(webView, environment);
      }
      catch (Exception) when (_disposed || webView.IsDisposed) { }
    }

    private void ConfigureWebView(WebView2 webView, CoreWebView2Environment webEnvironment)
    {
      webView.Source = new Uri("about:blank", UriKind.Absolute);
      webView.NavigationStarting += OnWebBrowser_NavigationStarting;
      webView.CoreWebView2.NewWindowRequested += (_, args) => {
        args.Handled = true;
        OpenLink(args.Uri);
      };
      ApplyConfiguredZoom(webView);
      webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
      webView.CoreWebView2.WebResourceRequested += CoreWebView2_WebResourceRequested;
      webView.CoreWebView2.WebMessageReceived += (sender, args) => {
        var message = JToken.Parse(args.WebMessageAsJson) as JObject;
        if (HandleEditorMessage(webView, args, message)) return;
        if ((string)message?["type"] == "am.openLink") {
          if (args.Source != "about:blank" && !args.Source.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase)) return;
          var address = (string)message["href"];
          if (Uri.TryCreate(address, UriKind.Absolute, out var target) && LocalFileLink.IsLocal(target)) OpenLink(address);
          return;
        }
        if ((string)message?["type"] != "am.openImage") return;
        var imageUrl = (string)message["src"];
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)
          || !(uri.Scheme == "http" || uri.Scheme == "https" || imageUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))) return;
        var title = (string)message["title"];
        if (string.IsNullOrWhiteSpace(title)) title = "\u0418\u0437\u043e\u0431\u0440\u0430\u0436\u0435\u043d\u0438\u0435";
        webView.BeginInvoke(new Action(() => {
          if (webView.IsDisposed) return;
          _imageWindow?.Close();
          var owner = ImagePreviewWindow.OwnerHandle(webView);
          var background = webView.FindForm()?.Opacity == 0;
          var window = new ImagePreviewWindow(webEnvironment, _assetPath, imageUrl, title, owner,
            request => CoreWebView2_WebResourceRequested(webView, request), background, _imagePlacementFile);
          _imageWindow = window;
          window.FormClosed += (_, e) => { if (ReferenceEquals(_imageWindow, window)) _imageWindow = null; };
          window.Show(new WindowOwner(owner));
        }));
      };

      var fs = new LocalFileService(webEnvironment, "local.example", _on);
      AddWebService(webView, fs);

      var api = new ApiService(webEnvironment, "api.example", _on);
      AddWebService(webView, api);
    }

    private void AddWebService(WebView2 webView, IWebService webService)
    {
      webView.CoreWebView2.AddWebResourceRequestedFilter($"http://{webService.Hostname}/*", CoreWebView2WebResourceContext.All);
      _webServices.Add(webService);
    }

    private void CoreWebView2_WebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
      var uri = new Uri(e.Request.Uri);
      foreach (var webservice in _webServices) {
        if (webservice.Hostname == uri.DnsSafeHost) {
          if (webservice.HandleRequest(e)) {
            return;
          }
        }
      }
    }

    public void AddToHost(Control host)
    {
      if (_webView != null && !_disposed) host.Controls.Add(_webView);
    }

    public Task ScrollToElementWithLineNo(int lineNo) => ExecuteWebviewActionAsync(
      webView => webView.ExecuteScriptAsync($"window.scrollToLine({Math.Max(0, lineNo)})"));

    public Task SetContentAsync(string content, string documentPath, bool readOnlyPreview = false) =>
      ExecuteWebviewActionAsync(webView => SetContentAsync(webView, content, documentPath, readOnlyPreview));

    private async Task SetContentAsync(WebView2 webView, string content, string documentPath, bool readOnlyPreview)
    {
      var fs = _webServices.OfType<LocalFileService>().First();

      var assetsPath = (!string.IsNullOrEmpty(_settings.AssetsPath) && Directory.Exists(_settings.AssetsPath))
        ? _settings.AssetsPath
        : _settings.DefaultAssetPath;

      var cssFile = _settings.IsDarkModeEnabled ? _settings.CssDarkModeFileName : _settings.CssFileName;
      if (!File.Exists(cssFile)) {
        cssFile = _settings.IsDarkModeEnabled ? _settings.DefaultDarkModeCssFile : _settings.DefaultCssFile;
      }
      var lineMark = (_settings.SyncViewWithFirstVisibleLine || _settings.SyncViewWithCaretPosition);
      var editing = EditorSnapshot != null && IsMarkdownPath(documentPath);
      fs.ReadOnly = readOnlyPreview || editing;
      var keepEditor = editing && _editing;
      var plugins = string.Join(",", _settings.EnabledMarkdownPlugins);
      var reload = !keepEditor && (_documentPath != documentPath || _readOnlyPreview != readOnlyPreview);
      reload = reload || _editing != editing;
      reload = reload || (_assetPath != assetsPath);
      reload = reload || (_cssFile != cssFile);
      reload = reload || (_lineMark != lineMark);
      reload = reload || (_trackFirstLine != _settings.SyncViewWithFirstVisibleLine);
      reload = reload || (_enabledMarkdownPlugins != plugins);

      if (_assetPath != assetsPath) {
        webView.CoreWebView2.SetVirtualHostNameToFolderMapping("assets.example", assetsPath, CoreWebView2HostResourceAccessKind.Allow);
      }

      var baseDir = Path.GetDirectoryName(documentPath);
      var replaceFileMapping = "file://" + baseDir;
      content = content.Replace(replaceFileMapping, $"http://{fs.Hostname}");
      fs.SetContent(documentPath, content);

      if (editing) PrepareEditorSnapshot(EditorSnapshot);

      if (!reload) {
        // Ordinary content updates should keep a zoom chosen with Ctrl+wheel.
        if (_appliedZoomLevel != _settings.ZoomLevel) ApplyConfiguredZoom(webView);
        var update = editing
          ? "window.__amSnapshot = " + JsonConvert.SerializeObject(EditorSnapshot, editorJson) + "; window.amReceiveSnapshot?.(window.__amSnapshot);"
          : "window.contentChanged?.();";
        await webView.ExecuteScriptAsync("window.amSetShowLinkAddresses?.(" + (_settings.ShowLinkAddresses ? "true" : "false") + ");" + update);
        _documentPath = documentPath;
        _readOnlyPreview = readOnlyPreview;
        return;
      }

      if (!string.IsNullOrEmpty(_documentPath) && _documentPath != documentPath) {
        var value = await webView.ExecuteScriptAsync("window.pageYOffset");
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var offset))
          _preservePosition[_documentPath] = offset;
      }

      var loader = File.ReadAllText(Path.Combine(assetsPath, "loader.html")).Replace("__BASE_URL__", HttpUtility2.PathToUri(baseDir));
      var options = new JObject {
        ["document"] = "http://local.example" + fs.DocumentUri,
        ["showLinkAddresses"] = _settings.ShowLinkAddresses,
        ["readOnlyPreview"] = readOnlyPreview
      };
      if (editing) {
        // Keep custom loaders' inline contract. The bundled loader requests the
        // snapshot over the editor bridge, outside NavigateToString's HTML size limit.
        var defaultAssets = _settings.DefaultAssetPath;
        if (!string.IsNullOrEmpty(defaultAssets) && string.Equals(Path.GetFullPath(assetsPath).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(defaultAssets).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) {
          options["requestEditorSnapshot"] = true;
        }
        else options["editorSnapshot"] = JToken.FromObject(EditorSnapshot, JsonSerializer.Create(editorJson));
      }
      options["nativeEditorToolbar"] = EditorUiChanged != null;

      if (IsMarkdownPath(documentPath)) {
        var assetPrefix = assetsPath.Replace('\\', '/').TrimEnd('/') + "/";
        var cssPath = cssFile.Replace('\\', '/');
        options["css"] = cssPath.StartsWith(assetPrefix, StringComparison.OrdinalIgnoreCase)
          ? "http://assets.example/" + HttpUtility2.UrlPathEncode(cssPath.Substring(assetPrefix.Length))
          : "http://" + fs.Hostname + "/" + HttpUtility2.PathToUri(cssPath);
        options["lineMark"] = lineMark;
        options["trackFirstLine"] = _settings.SyncViewWithFirstVisibleLine;
        if (_preservePosition.TryGetValue(documentPath, out var pageYOffset)) {
          options["pageYOffset"] = pageYOffset;
        }
        options["md.extensions"] = JToken.FromObject(_settings.EnabledMarkdownPlugins);
      }

      loader = loader.Replace("__OPTIONS__", JsonConvert.SerializeObject(options, editorJson));

      webView.NavigateToString(loader);
      ApplyConfiguredZoom(webView);
      _documentPath = documentPath;
      _assetPath = assetsPath;
      _cssFile = cssFile;
      _readOnlyPreview = readOnlyPreview;
      _editing = editing;
      _lineMark = lineMark;
      _trackFirstLine = _settings.SyncViewWithFirstVisibleLine;
      _enabledMarkdownPlugins = plugins;
    }

    public Task SetZoomLevel(int zoomLevel) => ExecuteWebviewActionAsync(webView => {
      SetZoomLevel(webView, zoomLevel);
      return Task.CompletedTask;
    });

    private void ApplyConfiguredZoom(WebView2 webView)
    {
      var zoomLevel = _settings.ZoomLevel;
      SetZoomLevel(webView, zoomLevel);
      _appliedZoomLevel = zoomLevel;
    }

    private static void SetZoomLevel(WebView2 webView, int zoomLevel)
    {
      var factor = ConvertToZoomFactor(zoomLevel);
      if (webView.ZoomFactor != factor) webView.ZoomFactor = factor;
    }

    public static bool IsMarkdownPath(string path) =>
      path != null && (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mdc", StringComparison.OrdinalIgnoreCase));

    private static double ConvertToZoomFactor(int zoomLevel) => zoomLevel / 100.0;

    void OnWebBrowser_NavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
    {
      if (e.Uri.ToString().StartsWith("about:blank")) {
        e.Cancel = true;
      }
      else if (!e.Uri.ToString().StartsWith("data:")) {
        e.Cancel = true;
        OpenLink(e.Uri);
      }
    }

    private void OpenLink(string address)
    {
      if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) return;
      if (LocalFileLink.IsLocal(uri)) {
        if (LocalFileLink.TryParse(uri, out var target) && File.Exists(target.Filename))
          _on.Navigate?.Invoke(this, target);
        return;
      }
      // Internal pages and script/data URLs must never reach the shell.
      if (uri.Scheme != "http" && uri.Scheme != "https" && uri.Scheme != "ftp" &&
          uri.Scheme != "ftps" && uri.Scheme != "mailto") return;
      Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
    }

    private async Task ExecuteWebviewActionAsync(Func<WebView2, Task> action)
    {
      var webView = _webView;
      if (_disposed || webView == null) return;
      try {
        await _initialization;
        if (_disposed || webView.IsDisposed) return;
        if (webView.InvokeRequired) await (Task)webView.Invoke(action, webView);
        else await action(webView);
      }
      catch (Exception) when (_disposed || webView.IsDisposed) { }
    }

    const string CONFIG_FOLDER_NAME = MarkdownRu.PluginBranding.Name;

    private WebView2 _webView;
    private Task _initialization;
    private bool _disposed;
    private ImagePreviewWindow _imageWindow;
    private readonly string _imagePlacementFile;
    private sealed class WindowOwner : IWin32Window
    {
      public WindowOwner(IntPtr handle) { Handle = handle; }
      public IntPtr Handle { get; }
    }
    private string _cssFile;
    private string _assetPath;
    private ISettings _settings;

    private string _documentPath;
    private int _appliedZoomLevel;
    private bool _lineMark;
    private bool _editing;
    private bool _readOnlyPreview;
    private bool _trackFirstLine;
    private string _enabledMarkdownPlugins;

    private Dictionary<string, double> _preservePosition = new Dictionary<string, double>();
    private List<IWebService> _webServices = new List<IWebService>();
    private IEventDispatcher _on;
  }
}
