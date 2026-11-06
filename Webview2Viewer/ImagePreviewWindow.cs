using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WebView2 = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Webview2Viewer
{
  internal sealed class ImagePreviewWindow : Form
  {
    private readonly CoreWebView2Environment environment;
    private readonly string assetsPath;
    private readonly string imageUrl;
    private readonly string imageTitle;
    private readonly Action<CoreWebView2WebResourceRequestedEventArgs> resourceRequested;
    private readonly bool background;
    private readonly WebView2 browser;
    private readonly string placementFile;
    private readonly Timer placementTimer = new Timer { Interval = 250 };
    private bool placementReady;

    public ImagePreviewWindow(CoreWebView2Environment environment, string assetsPath, string imageUrl, string imageTitle,
      IntPtr owner, Action<CoreWebView2WebResourceRequestedEventArgs> resourceRequested, bool background, string placementFile)
    {
      this.environment = environment;
      this.assetsPath = assetsPath;
      this.imageUrl = imageUrl;
      this.imageTitle = imageTitle;
      this.resourceRequested = resourceRequested;
      this.background = background;
      this.placementFile = placementFile;
      Text = imageTitle + " - AnotherMarkdown";
      ShowInTaskbar = false;
      MinimizeBox = false;
      KeyPreview = true;
      StartPosition = FormStartPosition.Manual;
      MinimumSize = new Size(360, 240);
      var area = Screen.FromHandle(owner).WorkingArea;
      Size = new Size(Math.Min(1600, area.Width - 40), Math.Min(1000, area.Height - 40));
      Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
      var placement = ImageWindowPlacement.Load(placementFile);
      if (placement != null) {
        Bounds = placement.VisibleBounds(Screen.AllScreens.Select(screen => screen.WorkingArea).ToArray(), area);
        if (placement.Maximized) WindowState = FormWindowState.Maximized;
      }
      placementTimer.Tick += (sender, e) => SavePlacement();
      BackColor = Color.FromArgb(23, 27, 34);
      if (background) Opacity = 0;
      browser = new WebView2 { Dock = DockStyle.Fill };
      Controls.Add(browser);
    }

    protected override bool ShowWithoutActivation => background;

    protected override void OnShown(EventArgs e)
    {
      base.OnShown(e);
      placementReady = true;
    }

    protected override void OnLocationChanged(EventArgs e)
    {
      base.OnLocationChanged(e);
      SchedulePlacementSave();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
      base.OnSizeChanged(e);
      SchedulePlacementSave();
    }

    private void SchedulePlacementSave()
    {
      if (!placementReady || WindowState == FormWindowState.Minimized) return;
      placementTimer.Stop();
      placementTimer.Start();
    }

    private void SavePlacement()
    {
      placementTimer.Stop();
      if (placementReady) ImageWindowPlacement.Save(placementFile, this);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
      base.OnFormClosing(e);
      if (!e.Cancel) { SavePlacement(); placementReady = false; }
    }

    protected override void Dispose(bool disposing)
    {
      if (disposing) {
        SavePlacement();
        placementReady = false;
        placementTimer.Dispose();
      }
      base.Dispose(disposing);
    }

    protected override async void OnLoad(EventArgs e)
    {
      base.OnLoad(e);
      try {
        await browser.EnsureCoreWebView2Async(environment);
        if (IsDisposed) return;
        browser.CoreWebView2.SetVirtualHostNameToFolderMapping("assets.example", assetsPath, CoreWebView2HostResourceAccessKind.Allow);
        browser.CoreWebView2.AddWebResourceRequestedFilter("http://local.example/*", CoreWebView2WebResourceContext.All);
        browser.CoreWebView2.WebResourceRequested += (sender, args) => resourceRequested(args);
        browser.CoreWebView2.WebMessageReceived += (sender, args) => {
          var message = JToken.Parse(args.WebMessageAsJson) as JObject;
          if ((string)message?["type"] == "am.closeImage") BeginInvoke(new Action(() => { if (!IsDisposed) Close(); }));
        };
        browser.CoreWebView2.NavigationStarting += (sender, args) => {
          if (args.Uri != "about:blank" && !args.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) args.Cancel = true;
        };
        browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        var options = JsonConvert.SerializeObject(new { src = imageUrl, title = imageTitle }, new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeHtml });
        browser.NavigateToString("<!doctype html><html><head><meta charset='utf-8'><link rel='stylesheet' href='http://assets.example/image-preview.css'>"
          + "<script src='http://assets.example/image-preview.js'></script></head><body style='margin:0;background:#171b22'>"
          + "<div id='content' hidden><img alt=''></div><script>const args=" + options + ";const original=document.querySelector('#content img');"
          + "original.alt=args.title;const viewer=window.createImagePreview(document.getElementById('content'),{standalone:true});"
          + "original.onload=()=>viewer.show(original);original.src=args.src;</script></body></html>");
      }
      catch (Exception error) {
        if (IsDisposed) return;
        Controls.Clear();
        browser.Dispose();
        Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = Color.White, Padding = new Padding(16), Text = "Image preview: " + error.Message });
      }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
      if (keyData == Keys.Escape) { Close(); return true; }
      return base.ProcessCmdKey(ref msg, keyData);
    }

    internal static IntPtr OwnerHandle(Control source)
    {
      var owner = GetAncestor(source.Handle, 3);
      return owner == IntPtr.Zero ? source.Handle : owner;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);
  }
}
