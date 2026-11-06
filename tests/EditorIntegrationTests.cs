using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PanelCommon;
using Webview2Viewer;
using WebView2 = Microsoft.Web.WebView2.WinForms.WebView2;

internal sealed class EditorIntegrationTests : Form
{
  private readonly string[] args;
  private readonly Timer timer = new Timer { Interval = 20 };
  private readonly StringBuilder log = new StringBuilder();
  private Webview2WebbrowserControl viewer;
  private WebView2 browser;
  private EditorSnapshot state;
  private EditorUiState ui;
  private Settings settings;
  private readonly List<EditorNavigation> navigation = new List<EditorNavigation>();
  private int checks, writes;
  private readonly List<NavigateToEvent> openedLinks = new List<NavigateToEvent>();
  protected override bool ShowWithoutActivation => true;
  [STAThread] private static int Main(string[] args)
  {
    Directory.CreateDirectory(args[1]);
    Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(args[1], "webview-profile"));
    Application.EnableVisualStyles();
    using (var form = new EditorIntegrationTests(args)) Application.Run(form);
    return Environment.ExitCode;
  }
  private EditorIntegrationTests(string[] args)
  {
    this.args = args; Width = 1100; Height = 820; Opacity = 0; ShowInTaskbar = false;
    timer.Tick += async (_, e) => {
      timer.Stop();
      try { await Run(); log.AppendLine("PASS native editor: " + checks + " assertions"); Environment.ExitCode = 0; }
      catch (Exception error) { log.AppendLine(error.ToString()); Environment.ExitCode = 1; }
      finally { File.WriteAllText(Path.Combine(args[1], "native-editor-results.txt"), log.ToString(), new UTF8Encoding(false)); viewer?.Dispose(); Close(); }
    }; timer.Start();
  }
  private async Task Run()
  {
    settings = new Settings(args[0]);
    viewer = new Webview2WebbrowserControl(); viewer.EditorUiChanged = value => ui = value;
    viewer.EditorNavigationChanged = value => navigation.Add(value);
    viewer.Initialize(settings, new ViewerEvents { Navigate = (_, target) => openedLinks.Add(target) });
    browser = await (Task<WebView2>)typeof(Webview2WebbrowserControl).GetField("_webView", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(viewer);
    Controls.Add(browser);
    await browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("window.fixtureErrors=[]; window.addEventListener('error',e=>fixtureErrors.push(e.message)); window.addEventListener('unhandledrejection',e=>fixtureErrors.push(String(e.reason)));");
    browser.CoreWebView2.WebMessageReceived += (_, e) => log.AppendLine("Bridge origin: " + e.Source);
    viewer.EditorRequestHandler = request => {
      if (request.Action == "state") return new EditorReply { Status = "state", Snapshot = state };
      if (request.Session != state.Session) return new EditorReply { Status = "stale", Snapshot = state };
      if (request.BaseText != state.Text) return new EditorReply { Status = "conflict", Snapshot = state };
      if (request.Action == "apply") { writes++; state.Text = request.Content; state.Revision++; return new EditorReply { Status = "applied", Snapshot = state }; }
      return new EditorReply { Status = "saved", Snapshot = state };
    };
    var path = Path.Combine(args[1], "original.md");
    File.WriteAllBytes(Path.Combine(args[1], "image #1.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jzXkAAAAASUVORK5CYII="));
    state = new EditorSnapshot { Session = "native-one", Path = path, Text = "# Native test\r\n\r\n\u041f\u0440\u0438\u0432\u0435\u0442 \ud83d\ude00.\r\n\r\n| A | B |\r\n| --- | --- |\r\n| 1 | 2 |\r\n", Revision = 1 };
    await Publish(); await WaitFor("window.amEditor?.editor");
    await WaitFor("!document.getElementById('content').hidden && document.querySelector('#content h1')");
    Check((bool)await Evaluate("document.querySelector('.am-editor-toolbar').hidden && document.querySelector('.am-editor-surface').hidden && document.querySelector('.am-editor-actions').hidden"), "preview opens without editor tools or a second action row");
    Check(ui != null && !ui.Editing && ui.Session == state.Session, "native toolbar receives initial preview state");
    Check(!browser.CoreWebView2.Settings.IsStatusBarEnabled && (bool)await Evaluate("amEditor.state.snapshot.showLinkAddresses"), "native status bar stays disabled while the saved link-address preference reaches the page");
    Check(writes == 0, "opening the editor does not rewrite source");
    Check((string)await Evaluate("amEditor.source.serialize()") == state.Text, "native initial round trip");
    await viewer.RunEditorCommandAsync("edit", "stale-session");
    Check((bool)await Evaluate("document.querySelector('.am-editor-toolbar').hidden"), "a stale native toolbar command cannot edit another file");
    await viewer.RunEditorCommandAsync("edit", state.Session);
    await WaitFor("!document.querySelector('.am-editor-toolbar').hidden");
    Check(ui.Editing, "native edit command opens WYSIWYG");
    state.Text += "\r\n![Local image](image%20%231.png)\r\n"; state.Revision++; await Publish();
    await WaitFor("document.querySelector('.ck-content img')?.naturalWidth === 1");
    Check(true, "relative images with encoded path characters load in WYSIWYG");
    await Evaluate("window.initialEditor = amEditor.editor; amEditor.editor.model.change(w => w.insertText('Visual ', amEditor.editor.model.document.getRoot().getChild(1), 0)); amEditor.flush()");
    Check(writes == 1 && state.Text.Contains("Visual \u041f\u0440\u0438\u0432\u0435\u0442"), "real WebView2 message updates native host");
    state.Text = "# Source update\n\nChanged outside visual editor.\n"; state.Revision++; await Publish();
    await WaitFor("document.querySelector('.ck-content h1')?.textContent === 'Source update'");
    Check((bool)await Evaluate("initialEditor === amEditor.editor"), "source refresh reuses the editor instance");
    state = new EditorSnapshot { Session = "native-two", Path = Path.Combine(args[1], "second #2.md"), Text = "Second file.\n", Revision = 10 };
    await Publish(); await WaitFor("amEditor.state.snapshot.session === 'native-two'");
    Check((bool)await Evaluate("document.querySelector('.am-editor-toolbar').hidden"), "switching files returns to preview");
    Check((bool)await Evaluate("initialEditor === amEditor.editor"), "switching files keeps editor and draft management alive");
    await Evaluate("amEditor.editor.model.change(w => w.insertText('Second edit ', amEditor.editor.model.document.getRoot().getChild(0), 0)); amEditor.flush()");
    Check(state.Text.StartsWith("Second edit "), "edits target the newly active file");
    const string camel = "\u0412\u0435\u0440\u0431\u043b\u044e\u0436\u044c\u044f\u041d\u043e\u0442\u0430\u0446\u0438\u044f";
    var list = "- First item.\r\n- **Keep:** " + camel + " identifiers.\r\n- Last item.\r\n";
    state = new EditorSnapshot { Session = "native-list", Path = path, Text = list, Revision = 15 };
    await Publish(); await WaitFor("amEditor.state.snapshot.session === 'native-list'");
    Check((bool)await Evaluate("document.querySelector('.am-primary').hidden"), "original has no redundant Save button");
    await Evaluate("(() => { const model = amEditor.editor.model; const text = [...model.createRangeIn(model.document.getRoot()).getItems()].find(n => n.is('$textProxy') && n.data.includes(" + JsonConvert.SerializeObject(camel) + ")); model.change(w => w.insertText(' ', text.parent, text.startOffset + text.data.indexOf(" + JsonConvert.SerializeObject(camel) + ") + 9)); return amEditor.flush(); })()");
    Check(state.Text == list.Replace(camel, camel.Insert(9, " ")), "native bridge changes only the inserted space, retaining list spelling and CRLF");
    state = new EditorSnapshot { Session = "native-translation", Path = path, Text = "Translated paragraph.\n", Revision = 20, Translated = true };
    await Publish(); await WaitFor("amEditor.state.snapshot.translated");
    Check((bool)await Evaluate("document.querySelector('.am-primary').textContent.includes('\u043a\u0430\u043a')"), "translation exposes Save As");
    state = new EditorSnapshot { Session = "native-html", Path = path, Text = "# Safe\n\n<img src=x onerror=\"window.injected=true\">\n", Revision = 30 };
    await Publish(); await WaitFor("amEditor.state.snapshot.session === 'native-html'");
    Check(!(bool)await Evaluate("Boolean(window.injected)"), "raw HTML never executes through the snapshot bridge");
    Check((string)await Evaluate("amEditor.source.serialize()") == state.Text, "existing HTML remains exact opaque source");
    await WaitFor("!document.getElementById('content').hidden && document.getElementById('content').textContent.includes('onerror')");
    Check(!(bool)await Evaluate("Boolean(window.injected)"), "HTML event handlers cannot run in legacy preview with the editing bridge");
    state = new EditorSnapshot { Session = "native-theme", Path = path, Text = "# Readable heading\n\nA paragraph with `code` and a [link](https://example.org).\n\n| A | B |\n| --- | --- |\n| One | Two |\n", Revision = 40 };
    foreach (var dark in new[] { true, false }) {
      settings.IsDarkModeEnabled = dark;
      var navigation = new TaskCompletionSource<bool>();
      EventHandler<CoreWebView2NavigationCompletedEventArgs> onNavigation = (_, e) => navigation.TrySetResult(e.IsSuccess);
      browser.CoreWebView2.NavigationCompleted += onNavigation;
      try {
        await Publish();
        if (await Task.WhenAny(navigation.Task, Task.Delay(20000)) != navigation.Task || !await navigation.Task)
          throw new TimeoutException("Theme navigation did not complete");
      } finally { browser.CoreWebView2.NavigationCompleted -= onNavigation; }
      await WaitFor("window.amEditor && amEditor.state.snapshot.dark === " + (dark ? "true" : "false") + " && !document.getElementById('content').hidden && document.querySelector('#content h1')?.textContent === 'Readable heading'");
      Check((string)await Evaluate("getComputedStyle(document.body).backgroundColor") == (dark ? "rgb(30, 30, 30)" : "rgb(255, 255, 255)"), "preview retains the original " + (dark ? "dark" : "light") + " palette");
      await viewer.RunEditorCommandAsync("edit", state.Session); await WaitFor("!document.querySelector('.am-editor-toolbar').hidden");
      Check((string)await Evaluate("getComputedStyle(document.querySelector('.ck-content')).color") == (dark ? "rgb(207, 207, 207)" : "rgb(51, 51, 51)"), "WYSIWYG body has readable " + (dark ? "dark" : "light") + " text");
      Check((string)await Evaluate("getComputedStyle(document.querySelector('.ck-content h1')).color") == (dark ? "rgb(255, 255, 255)" : "rgb(17, 17, 17)"), "WYSIWYG headings override renderer colors");
      Check((string)await Evaluate("getComputedStyle(document.querySelector('.ck-content td')).color") == (dark ? "rgb(207, 207, 207)" : "rgb(51, 51, 51)"), "WYSIWYG cells inherit readable text");
      using (var stream = File.Create(Path.Combine(args[1], dark ? "dark-editor.png" : "light-editor.png"))) await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
      await viewer.RunEditorCommandAsync("preview", state.Session); await WaitFor("!document.getElementById('content').hidden");
    }
    await LinkAddressChecks(path);
    await LocalLinkChecks(path);
    await LineBreakChecks(path);
    await NavigationChecks(path);
    using (var stream = File.Create(Path.Combine(args[1], "native-editor.png"))) await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
  }
  private async Task LinkAddressChecks(string path)
  {
    state = new EditorSnapshot { Session = "native-link-addresses", Path = path,
      Text = "[Example link](https://example.test/target)\n", Revision = 35 };
    var original = state.Text; var writeCount = writes;
    await Publish(); await WaitFor("amEditor.state.snapshot.session === 'native-link-addresses'");
    var instance = browser.CoreWebView2;
    var navigations = 0;
    EventHandler<CoreWebView2NavigationCompletedEventArgs> onNavigation = (_, __) => navigations++;
    browser.CoreWebView2.NavigationCompleted += onNavigation;
    foreach (var edit in new[] { false, true }) {
      await viewer.RunEditorCommandAsync(edit ? "edit" : "preview", state.Session);
      await WaitFor(edit ? "!document.querySelector('.am-editor-surface').hidden && document.querySelector('.ck-content a')"
        : "!document.getElementById('content').hidden && document.querySelector('#content a')");
      var box = (JObject)await Evaluate("(()=>{const r=document.querySelector('" + (edit ? ".ck-content a" : "#content a") + "').getBoundingClientRect();return {x:r.left+r.width/2,y:r.top+r.height/2};})()");
      await browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonConvert.SerializeObject(new { type = "mouseMoved", x = (double)box["x"], y = (double)box["y"] }));
      foreach (var show in new[] { true, false, true }) {
        settings.ShowLinkAddresses = show;
        await Publish();
        await WaitFor("document.querySelector('#am-link-address').hidden === " + (show ? "false" : "true"));
        Check((string)await Evaluate("document.querySelector('#am-link-address').textContent") == (show ? "https://example.test/target" : ""), "hovered URL actually appears or disappears immediately in " + (edit ? "editing: " : "preview: ") + show);
        Check(!browser.CoreWebView2.Settings.IsStatusBarEnabled, "native URL overlay cannot duplicate or bypass the preference");
        Check(ReferenceEquals(instance, browser.CoreWebView2), "link-address toggle applies without recreating the browser");
      }
    }
    browser.CoreWebView2.NavigationCompleted -= onNavigation;
    Check(navigations == 0, "link-address preference takes effect without a top-level navigation");
    Check(writes == writeCount && state.Text == original, "link-address toggle does not change Markdown");
  }
  private async Task ClickLink(string selector, int modifiers = 0)
  {
    var json = JsonConvert.SerializeObject(selector);
    await Evaluate("document.querySelector(" + json + ").scrollIntoView({block:'center'})");
    var box = (JObject)await Evaluate("(()=>{const r=document.querySelector(" + json + ").getBoundingClientRect();return {x:r.left+r.width/2,y:r.top+r.height/2};})()");
    foreach (var type in new[] { "mouseMoved", "mousePressed", "mouseReleased" })
      await browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonConvert.SerializeObject(new {
        type, x = (double)box["x"], y = (double)box["y"], button = type == "mouseMoved" ? "none" : "left", clickCount = type == "mouseMoved" ? 0 : 1, modifiers
      }));
  }
  private async Task ExpectOpened(string selector, string path, int line, int column, int modifiers = 0)
  {
    var count = openedLinks.Count;
    await ClickLink(selector, modifiers);
    var clock = Stopwatch.StartNew();
    while (openedLinks.Count == count && clock.Elapsed.TotalSeconds < 5) await Task.Delay(20);
    Check(openedLinks.Count == count + 1, "exactly one file-open event: " + Path.GetFileName(path));
    var target = openedLinks[count];
    Check(Path.GetFullPath(target.Filename) == Path.GetFullPath(path) && target.Line == line && target.Column == column,
      "exact path and source location reach Notepad++: " + Path.GetFileName(path));
  }
  private async Task LocalLinkChecks(string path)
  {
    var folder = Path.GetDirectoryName(path);
    var module = Path.Combine(folder, "Module\u041c.bsl");
    var json = Path.Combine(folder, "config+.json");
    var xml = Path.Combine(folder, "document.xml");
    var markdown = Path.Combine(folder, "linked.md");
    var escaped = Path.Combine(folder, "source #25%.bsl");
    foreach (var file in new[] { module, json, xml, markdown, escaped }) File.WriteAllText(file, "first\nsecond\nthird\n");
    var paths = new[] { module, json, xml, markdown, escaped, json };
    var hrefs = new[] { module.Replace('\\', '/') + ":6531", "config+.json", "document.xml", "linked.md", new Uri(escaped).AbsoluteUri + ":3:2", "config+.json:2:3" };
    var text = "| Target |\n| --- |\n";
    for (var index = 0; index < paths.Length; index++) text += "| [**Link " + index + "**](<" + hrefs[index] + ">) |\n";
    state = new EditorSnapshot { Session = "native-local-links", Path = path, Text = text, Revision = 36 };
    var writeCount = writes; await Publish(); await WaitFor("amEditor.state.snapshot.session === 'native-local-links'");
    foreach (var editing in new[] { false, true }) {
      await Evaluate("amEditorCommand('" + (editing ? "edit" : "preview") + "','native-local-links')");
      var prefix = editing ? ".ck-content" : "#content";
      await WaitFor("document.querySelectorAll('" + prefix + " a').length === 6");
      for (var index = 0; index < paths.Length; index++) {
        var selector = prefix + " tbody tr:nth-child(" + (index + 1) + ") a";
        Check((string)await Evaluate("document.querySelector(" + JsonConvert.SerializeObject(selector) + ").getAttribute('href')") != "#", "local links are not replaced with #: " + editing + "/" + index);
        var line = index == 0 ? 6531 : index == 4 ? 3 : index == 5 ? 2 : 0;
        var column = index == 4 ? 2 : index == 5 ? 3 : 0;
        await ExpectOpened(selector, paths[index], line, column, editing ? 2 : 0);
        if (!editing) continue;
        await ClickLink(selector);
        await WaitFor("document.querySelector('.ck-link-toolbar a')");
        Check((string)await Evaluate("document.querySelector('.ck-link-toolbar a').getAttribute('href')") != "#", "balloon points to the actual file: " + index);
        await ExpectOpened(".ck-link-toolbar a", paths[index], line, column);
        // The hidden test form has no OS keyboard focus. Move the model selection
        // outside the link to dismiss its balloon before testing the next row.
        await Evaluate("amEditor.editor.model.change(w => w.setSelection(amEditor.editor.model.document.getRoot(), 'end'))");
        await WaitFor("!document.querySelector('.ck-link-toolbar__preview')?.getClientRects().length");
      }
    }
    Check(writes == writeCount && state.Text == text && (string)await Evaluate("amEditor.source.serialize()") == text, "opening local links preserves source Markdown");
    Check((string)await Evaluate("JSON.stringify(fixtureErrors)") == "[]", "local link interactions produce no script errors");
  }
  private async Task LineBreakChecks(string path)
  {
    state = new EditorSnapshot { Session = "native-line-breaks", Path = path,
      Text = "First wrapped source line.\r\nSecond wrapped source line.\r\nThird wrapped source line.\r\n", Revision = 40 };
    var original = state.Text; var writeCount = writes;
    await Publish(); await WaitFor("amEditor.state.snapshot.session === 'native-line-breaks'");
    await viewer.RunEditorCommandAsync("edit", state.Session);
    for (var index = 0; index < 3; index++) {
      var preserve = index == 1;
      settings.PreserveEditorLineBreaks = preserve;
      await Publish();
      await WaitFor("document.querySelector('.ck-content p').querySelectorAll('br').length === " + (preserve ? "2" : "0"));
      Check((bool)await Evaluate("amEditor.state.snapshot.preserveLineBreaks") == preserve, "native line-break setting reaches the live editor: " + preserve);
      Check((string)await Evaluate("amEditor.source.serialize()") == original, "changing line-break display preserves original source: " + preserve);
      using (var stream = File.Create(Path.Combine(args[1], preserve ? "preserved-lines.png" : "reflowed-lines.png")))
        await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
    }
    Check(writes == writeCount && state.Text == original, "changing editor line-break display sends no source edits");
  }
  private async Task NavigationChecks(string path)
  {
    state = new EditorSnapshot { Session = "native-navigation", Path = path,
      Text = "# Heading\r\n\r\nA **\u041f\u0440\u0438\u0432\u0435\u0442** \ud83d\ude00.\r\n\r\n| Name | Value |\r\n| --- | --- |\r\n| alpha | beta |\r\n", Revision = 50 };
    await Publish(); await WaitFor("amEditor.state.snapshot.session === 'native-navigation' && document.querySelector('#content td')");
    var original = state.Text; var writeCount = writes;
    Check((bool)await Evaluate("!!document.querySelector('#content tr[data-am-from]')"), "real preview renderer supplies source ranges for GFM tables");
    Check(navigation.Exists(n => n.Request && n.Session == state.Session), "new view requests current native navigation after rendering");
    var start = state.Text.IndexOf("\u041f\u0440\u0438\u0432\u0435\u0442", StringComparison.Ordinal);
    await viewer.RunNavigationAsync(new EditorNavigation { Session = state.Session, Revision = state.Revision, Anchor = start, Focus = start + 6, Selection = true });
    Check((string)await Evaluate("[...CSS.highlights.get('am-synced-selection')][0].toString()") == "\u041f\u0440\u0438\u0432\u0435\u0442", "source selection is highlighted in the actual read-only WebView preview");
    await viewer.RunEditorCommandAsync("edit", state.Session); await WaitFor("!document.querySelector('.am-editor-toolbar').hidden");
    Check((string)await Evaluate("[...amEditor.editor.model.document.selection.getFirstRange().getItems()].filter(n=>n.is('$textProxy')).map(n=>n.data).join('')") == "\u041f\u0440\u0438\u0432\u0435\u0442", "native preview-to-editor switch retains selection");
    await viewer.RunNavigationAsync(new EditorNavigation { Session = state.Session, Revision = state.Revision, Anchor = start + 6, Focus = start, Selection = true });
    Check((bool)await Evaluate("amEditor.editor.model.document.selection.isBackward"), "native source reverse selection preserves anchor and caret direction: " + (string)await Evaluate("JSON.stringify({revision:amEditor.state.snapshot.revision,dirty:amEditor.state.text!==amEditor.state.base,inFlight:!!amEditor.state.inFlight,anchor:amEditor.editor.model.document.selection.anchor.path,focus:amEditor.editor.model.document.selection.focus.path,errors:fixtureErrors})"));
    navigation.Clear();
    await Evaluate("amEditor.editor.model.change(w=>w.setSelection(amEditor.editor.model.document.getRoot().getChild(0),2)); amEditor.navigation.send()");
    // send() reads queued selection flags; allow the normal debounced bridge to run.
    await Task.Delay(150);
    Check(navigation.Exists(n => n.Selection && n.Anchor == 4 && n.Focus == 4), "WYSIWYG caret reaches native host at the Markdown character offset");
    // Visibility changes before the asynchronous preview render finishes. Await
    // the command promise before selecting in its DOM, rather than racing refresh.
    await Evaluate("window.amEditorCommand('preview'," + JsonConvert.SerializeObject(state.Session) + ")");
    await WaitFor("!document.getElementById('content').hidden");
    navigation.Clear();
    await Evaluate("(()=>{const n=document.querySelector('#content td').firstChild; getSelection().setBaseAndExtent(n,5,n,0); document.getElementById('content').dispatchEvent(new PointerEvent('pointerup',{bubbles:true}));})()");
    await Task.Delay(150);
    var cell = state.Text.IndexOf("alpha", StringComparison.Ordinal);
    var wait = Stopwatch.StartNew();
    while (!navigation.Exists(n => n.Selection && n.Anchor == cell + 5 && n.Focus == cell) && wait.ElapsedMilliseconds < 2000)
      await Task.Delay(25);
    Check(navigation.Exists(n => n.Selection && n.Anchor == cell + 5 && n.Focus == cell), "GFM preview selection reaches native host with reverse direction: " + JsonConvert.SerializeObject(navigation));
    Check(writes == writeCount && state.Text == original, "navigation leaves source, disk state and undo history untouched");
    Check((string)await Evaluate("JSON.stringify(fixtureErrors)") == "[]", "navigation produces no WebView script errors");
  }
  private async Task Publish() { viewer.EditorSnapshot = state; await viewer.SetContentAsync(state.Text, state.Path, state.Translated); }
  private async Task<JToken> Evaluate(string expression)
  {
    var result = JObject.Parse(await browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonConvert.SerializeObject(new { expression, awaitPromise = true, returnByValue = true })));
    if (result["exceptionDetails"] != null) throw new Exception(result["exceptionDetails"].ToString());
    return result["result"]["value"];
  }
  private async Task WaitFor(string expression)
  {
    var clock = Stopwatch.StartNew();
    while (!(bool)await Evaluate("Boolean(" + expression + ")")) {
      if (clock.Elapsed.TotalSeconds > 20) {
        log.AppendLine((string)await Evaluate("JSON.stringify({errors:window.fixtureErrors,preview:document.getElementById('content')?.outerHTML,viewPlugin:!!window.viewPlugin,status:document.querySelector('.am-editor-status')?.textContent})"));
        throw new TimeoutException(expression);
      }
      await Task.Delay(50);
    }
  }
  private void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; log.AppendLine("PASS " + name); }
  private sealed class Settings : ISettings
  {
    public Settings(string path) { DefaultAssetPath = path; }
    public bool ShowLinkAddresses { get; set; } = true;
    public bool PreserveEditorLineBreaks { get; set; }
    public bool SyncViewWithCaretPosition => true;
    public bool SyncViewWithFirstVisibleLine => true;
    public string AssetsPath => DefaultAssetPath;
    public string CssFileName => DefaultCssFile;
    public string CssDarkModeFileName => DefaultDarkModeCssFile;
    public int ZoomLevel => 100;
    public bool IsDarkModeEnabled { get; set; }
    public string DefaultAssetPath { get; }
    public string DefaultCssFile => Path.Combine(DefaultAssetPath, "markdown", "style.css");
    public string DefaultDarkModeCssFile => Path.Combine(DefaultAssetPath, "markdown", "style-dark.css");
    public string[] EnabledMarkdownPlugins => new string[0];
  }
  private sealed class ViewerEvents : IEventDispatcher
  {
    public EventHandler<DocumentChangedEvent> DocumentChanged => null;
    public EventHandler<FirstLineChangedEvent> FirstLineChanged => null;
    public EventHandler<PasteImageEvent> PasteImage => null;
    public EventHandler<NavigateToEvent> Navigate { get; set; }
  }
}
