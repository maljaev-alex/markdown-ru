using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PanelCommon;
using Webview2Viewer;
using WebView2 = Microsoft.Web.WebView2.WinForms.WebView2;

internal sealed class ImagePreviewTests : Form
{
  private readonly string[] args;
  private readonly Timer startup = new Timer { Interval = 20 };
  private readonly StringBuilder log = new StringBuilder();
  private Webview2WebbrowserControl viewer;
  private WebView2 browser;
  private int writes;
  private int failedRequests;
  private int checks;
  protected override bool ShowWithoutActivation { get { return true; } }

  [STAThread]
  private static int Main(string[] args)
  {
    if (args.Length < 3) { Console.Error.WriteLine("Usage: ImagePreviewTests assets browser-test.js work-directory [live-notes-directory]"); return 2; }
    Directory.CreateDirectory(args[2]);
    Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(args[2], "webview-profile"));
    Application.EnableVisualStyles();
    using (var test = new ImagePreviewTests(args)) Application.Run(test);
    return Environment.ExitCode;
  }

  private ImagePreviewTests(string[] args)
  {
    this.args = args;
    Text = "Image preview verification"; Width = 600; Height = 750; Opacity = 0; ShowInTaskbar = false;
    startup.Tick += async (sender, e) => {
      startup.Stop();
      try { await Run(); Environment.ExitCode = 0; log.AppendLine("PASS image preview: " + checks + " assertions; Markdown writes=" + writes); }
      catch (Exception error) { log.AppendLine(error.ToString()); Environment.ExitCode = 1; }
      finally {
        File.WriteAllText(Path.Combine(args[2], "results.txt"), log.ToString(), new UTF8Encoding(false));
        if (viewer != null) viewer.Dispose();
        Close();
      }
    };
    startup.Start();
  }

  private async Task Run()
  {
    var imagePath = Path.Combine(args[2], "screenshot #1.png");
    using (var bitmap = new Bitmap(1600, 1000)) {
      using (var graphics = Graphics.FromImage(bitmap)) {
        graphics.Clear(Color.White);
        using (var font = new Font("Segoe UI", 15)) {
          for (var row = 0; row < 30; row++) graphics.DrawString("Screenshot row " + row + " - readable at the original scale", font, Brushes.Black, 15, 15 + row * 30);
        }
      }
      bitmap.Save(imagePath, System.Drawing.Imaging.ImageFormat.Png);
    }
    var path = Path.Combine(args[2], "preview #38.md");
    var markdown = "# Image preview fixture\n\n![Screenshot](screenshot%20%231.png)\n\n[![Linked screenshot](screenshot%20%231.png)](other.md)\n";
    var constructor = typeof(Webview2WebbrowserControl).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string) }, null);
    viewer = (Webview2WebbrowserControl)constructor.Invoke(new object[] { Path.Combine(args[2], "image-window.json") });
    var placementFile = Path.Combine(args[2], "image-window.json");
    var previousPlacement = File.Exists(placementFile) ? JObject.Parse(File.ReadAllText(placementFile, Encoding.UTF8)) : null;
    viewer.Initialize(new TestSettings(args[0]), new TestEvents(() => writes++));
    browser = await (Task<WebView2>)typeof(Webview2WebbrowserControl).GetField("_webView", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(viewer);
    Controls.Add(browser);
    browser.CoreWebView2.WebResourceResponseReceived += (sender, e) => {
      if (e.Response.StatusCode >= 400) { failedRequests++; log.AppendLine("HTTP " + e.Response.StatusCode + " " + e.Request.Uri); }
    };
    foreach (var readOnly in new[] { false, true }) {
      await viewer.SetContentAsync(markdown, path, readOnly);
      await WaitFor("document.querySelector('.am-image-zoom') && document.querySelector('#content img')?.complete");
      var markup = (string)await Evaluate("document.getElementById('content').innerHTML");
      await Evaluate("document.querySelector('.am-image-zoom').click(); true");
      var popup = await Popup();
      var popupBrowser = PopupBrowser(popup);
      await WaitFor(popupBrowser, "document.querySelector('.am-image-dialog')?.open && document.querySelector('[data-action=fit]').getAttribute('aria-pressed') === 'true'");
      if (!readOnly && previousPlacement != null) {
        var expected = new Rectangle((int)previousPlacement["Left"], (int)previousPlacement["Top"], (int)previousPlacement["Width"], (int)previousPlacement["Height"]);
        Check(popup.WindowState == ((bool)previousPlacement["Maximized"] ? FormWindowState.Maximized : FormWindowState.Normal), "fresh process restores the persisted window state");
        Check((popup.WindowState == FormWindowState.Normal ? popup.Bounds : popup.RestoreBounds) == expected, "fresh process restores persisted normal bounds");
      }
      popup.WindowState = FormWindowState.Normal;
      Check(popup.TopLevel && popup.Handle != Handle && popup.Handle != browser.Handle, "magnifier creates a distinct top-level native window");
      Check(GetWindow(popup.Handle, 4) == Handle, "image window is owned by the editor host and stays above it");
      Check(Enabled && !popup.Modal && !popup.TopMost, "owner remains usable and popup belongs only to its editor");
      Check(popup.Width > browser.Width && popup.MaximizeBox, "popup size is independent of the narrow preview and supports maximize");
      Check(!(bool)await Evaluate("document.querySelector('.am-image-dialog').open") && (string)await Evaluate("document.documentElement.style.overflow") != "hidden", "owner preview contains no open image overlay");
      var result = await Evaluate(popupBrowser, File.ReadAllText(args[1], Encoding.UTF8));
      checks += (int)result["checks"];
      var x = (double)result["stage"]["x"]; var y = (double)result["stage"]["y"];
      await popupBrowser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonConvert.SerializeObject(new { type = "mousePressed", x, y, button = "left", buttons = 1, clickCount = 1 }));
      await popupBrowser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonConvert.SerializeObject(new { type = "mouseMoved", x = x - 90, y = y - 60, button = "left", buttons = 1 }));
      await popupBrowser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonConvert.SerializeObject(new { type = "mouseReleased", x = x - 90, y = y - 60, button = "left", buttons = 0, clickCount = 1 }));
      await WaitFor(popupBrowser, "document.querySelector('.am-image-stage').scrollLeft >= 80 && document.querySelector('.am-image-stage').scrollTop >= 50");
      checks++;
      var oldSize = Size;
      popup.Size = new Size(800, 600);
      popup.Location = new Point(Screen.PrimaryScreen.WorkingArea.Left + 80, Screen.PrimaryScreen.WorkingArea.Top + 70);
      var savedBounds = popup.Bounds;
      await WaitUntil(() => {
        if (!File.Exists(placementFile)) return false;
        var stored = JObject.Parse(File.ReadAllText(placementFile, Encoding.UTF8));
        return (int)stored["Left"] == savedBounds.Left && (int)stored["Top"] == savedBounds.Top
          && (int)stored["Width"] == savedBounds.Width && (int)stored["Height"] == savedBounds.Height && !(bool)stored["Maximized"];
      });
      Check(true, "moving and resizing persist while the window remains open");
      await WaitFor(popupBrowser, "innerWidth < 800");
      Check(Size == oldSize, "resizing the popup does not resize the editor");
      using (var stream = File.Create(Path.Combine(args[2], readOnly ? "translated-preview.png" : "image-preview.png")))
        await popupBrowser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
      var closing = Evaluate(popupBrowser, "document.querySelector('.am-image-dialog').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true})); true");
      await WaitUntil(() => popup.IsDisposed);
      if (closing.IsFaulted) { var ignored = closing.Exception; }
      Check((string)await Evaluate("document.getElementById('content').innerHTML") == markup, "opening and closing the native window preserves Markdown DOM");
      await Evaluate("document.querySelectorAll('.am-image-zoom')[1].click(); true");
      popup = await Popup();
      popupBrowser = PopupBrowser(popup);
      await WaitFor(popupBrowser, "document.querySelector('.am-image-dialog')?.open");
      Check(popup.Bounds == savedBounds && popup.WindowState == FormWindowState.Normal, "next image restores the saved size and position");
      Check(popup.Text.StartsWith("Linked screenshot"), "linked image magnifier opens its own window and retains the original link");
      await viewer.SetContentAsync("# Updated buffer\n\nNo images here.", path, readOnly);
      await WaitFor("document.querySelector('#content h1')?.textContent === 'Updated buffer' && document.querySelectorAll('.am-image-zoom').length === 0");
      Check(!popup.IsDisposed && popup.Visible, "popup remains independent when the owner document updates");
      popup.WindowState = FormWindowState.Maximized;
      await WaitUntil(() => popup.WindowState == FormWindowState.Maximized);
      popup.Close();
      Check(popup.IsDisposed, "native close button disposes the image window");
      await viewer.SetContentAsync(markdown, path, readOnly);
      await WaitFor("document.querySelectorAll('.am-image-zoom').length === 2");
      await Evaluate("document.querySelector('#content img').click(); true");
      popup = await Popup();
      Check(popup.TopLevel, "plain image click opens the separate window");
      Check(popup.WindowState == FormWindowState.Maximized, "next image restores the maximized state");
      popup.WindowState = FormWindowState.Normal;
      await WaitUntil(() => popup.WindowState == FormWindowState.Normal);
      Check(popup.Bounds == savedBounds, "restoring maximized window retains the saved normal bounds");
      popup.Close();
      log.AppendLine("PASS " + (readOnly ? "read-only" : "original") + " native owned popup, independent size, zoom, drag, Esc, source refresh");
    }
    if (args.Length > 3) {
      var file = Directory.GetFiles(args[3], "*.md").First(item => Path.GetFileName(item).Contains("#38 "));
      await viewer.SetContentAsync(File.ReadAllText(file, Encoding.UTF8), file, false);
      await WaitFor("document.querySelectorAll('#content img').length > 0 && document.querySelector('.am-image-zoom:not([hidden])')");
      await Evaluate("document.querySelector('.am-image-zoom:not([hidden])').click(); true");
      var popup = await Popup();
      var popupBrowser = PopupBrowser(popup);
      await WaitFor(popupBrowser, "document.querySelector('.am-image-dialog')?.open && document.querySelector('[data-action=fit]').getAttribute('aria-pressed') === 'true'");
      using (var stream = File.Create(Path.Combine(args[2], "task-38-enlarged.png")))
        await popupBrowser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
      log.AppendLine("PASS live task #38 separate popup HWND=" + popup.Handle + " owner=" + GetWindow(popup.Handle, 4) + " size=" + popup.Size); checks++;
      if (args.Length > 4 && args[4] == "--leave-maximized") popup.WindowState = FormWindowState.Maximized;
      popup.Close();
    }
    if (writes != 0 || failedRequests != 0) throw new Exception("Unexpected writes or failed resource requests: " + writes + "/" + failedRequests);
    checks++;
  }

  private async Task<JToken> Evaluate(string expression)
    => await Evaluate(browser, expression);

  private async Task<JToken> Evaluate(WebView2 target, string expression)
  {
    var response = JObject.Parse(await target.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonConvert.SerializeObject(new { expression, awaitPromise = true, returnByValue = true })));
    if (response["exceptionDetails"] != null) throw new Exception(response["exceptionDetails"].ToString());
    return response["result"]["value"];
  }

  private async Task WaitFor(string expression)
    => await WaitFor(browser, expression);

  private async Task WaitFor(WebView2 target, string expression)
  {
    var watch = Stopwatch.StartNew();
    do {
      if ((bool)await Evaluate(target, "Boolean(" + expression + ")")) return;
      await Task.Delay(50);
    } while (watch.Elapsed < TimeSpan.FromSeconds(12));
    throw new TimeoutException(expression);
  }

  private void Check(bool condition, string label)
  {
    if (!condition) throw new Exception(label);
    checks++;
  }

  private async Task WaitUntil(Func<bool> condition)
  {
    var watch = Stopwatch.StartNew();
    while (!condition()) {
      if (watch.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("Native window state");
      await Task.Delay(30);
    }
  }

  private async Task<Form> Popup()
  {
    Form popup = null;
    await WaitUntil(() => {
      popup = (Form)typeof(Webview2WebbrowserControl).GetField("_imageWindow", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(viewer);
      return popup != null && !popup.IsDisposed && PopupBrowser(popup).CoreWebView2 != null;
    });
    return popup;
  }

  private static WebView2 PopupBrowser(Form popup) =>
    (WebView2)popup.GetType().GetField("browser", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(popup);

  [DllImport("user32.dll")]
  private static extern IntPtr GetWindow(IntPtr window, uint command);

  private sealed class TestSettings : ISettings
  {
    public TestSettings(string assets) { DefaultAssetPath = assets; }
    public bool ShowLinkAddresses => false;
    public bool PreserveEditorLineBreaks => false;
    public bool SyncViewWithCaretPosition { get { return false; } }
    public bool SyncViewWithFirstVisibleLine { get { return false; } }
    public string AssetsPath { get { return DefaultAssetPath; } }
    public string CssFileName { get { return DefaultCssFile; } }
    public string CssDarkModeFileName { get { return DefaultDarkModeCssFile; } }
    public int ZoomLevel { get { return 100; } }
    public bool IsDarkModeEnabled { get { return true; } }
    public string DefaultAssetPath { get; private set; }
    public string DefaultCssFile { get { return Path.Combine(DefaultAssetPath, "markdown", "markdown.css"); } }
    public string DefaultDarkModeCssFile { get { return Path.Combine(DefaultAssetPath, "markdown", "markdown-dark.css"); } }
    public string[] EnabledMarkdownPlugins { get { return new string[0]; } }
  }

  private sealed class TestEvents : IEventDispatcher
  {
    private readonly Action changed;
    public TestEvents(Action changed) { this.changed = changed; }
    public EventHandler<DocumentChangedEvent> DocumentChanged { get { return (sender, e) => changed(); } }
    public EventHandler<FirstLineChangedEvent> FirstLineChanged { get { return null; } }
    public EventHandler<PasteImageEvent> PasteImage { get { return null; } }
    public EventHandler<NavigateToEvent> Navigate { get; set; }
  }
}
