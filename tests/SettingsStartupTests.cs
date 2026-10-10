using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AnotherMarkdown.Entities;
using AnotherMarkdown.Forms;
using AnotherMarkdown.Translation;

internal static class SettingsStartupTests
{
  private static int checks, uiThread;
  private static string scratch, executableA, executableB;
  private static Exception uiError;

  [STAThread]
  private static int Main()
  {
    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
    Application.ThreadException += (_, args) => uiError = args.Exception;
    Application.EnableVisualStyles();
    uiThread = Thread.CurrentThread.ManagedThreadId;
    var parent = Path.GetFullPath(Directory.Exists(@"D:\Temp") ? @"D:\Temp\agent\markdown-ru" : Path.Combine(Path.GetTempPath(), "markdown-ru"));
    scratch = Path.Combine(parent, "settings-startup-tests-" + Guid.NewGuid().ToString("N"));
    try {
      Directory.CreateDirectory(scratch);
      executableA = Path.Combine(scratch, "fixture-a.exe"); executableB = Path.Combine(scratch, "fixture-b.exe");
      // Existing launchers for path resolution only. Injected loaders never execute them.
      File.WriteAllBytes(executableA, new byte[] { 0 }); File.WriteAllBytes(executableB, new byte[] { 0 });
      // Keep one native message loop and its marshaling control alive across
      // scenarios. DoEvents alone tears down contexts between top-level forms.
      var exitCode = 1; EventHandler run = null;
      run = (_, __) => {
        Application.Idle -= run;
        try {
          NoPaintDiscovery(); StartupPrefetch(); ConcurrentAndResponsive(); DiscoveryGeometry(); ApiAutoDiscovery(); NativeModelPicker(); WarmCacheAndLateEdits(); SelectionAndCancellation(); CliProfilesSettings(); Disposal(); RelativeLauncher(); Geometry();
          Console.WriteLine("PASS settings startup: " + checks + " assertions"); exitCode = 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); }
        finally { Application.ExitThread(); }
      };
      Application.Idle += run; Application.Run(); return exitCode;
    }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    finally {
      var resolved = Path.GetFullPath(scratch);
      if (Directory.Exists(resolved) && resolved.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Directory.Delete(resolved, true);
    }
  }

  private static void Check(bool condition, string label)
  { if (!condition) throw new Exception("FAIL " + label); checks++; Console.WriteLine("PASS " + label); }
  private static T Find<T>(Control root, string name) where T : Control => (T)root.Controls.Find(name, true).Single();
  private static T Field<T>(SettingsForm form, string name) => (T)typeof(SettingsForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
  private static TranslationOptions Draft(SettingsForm form) => (TranslationOptions)typeof(SettingsForm).GetMethod("ReadTranslationDraft", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
  private static void Pump(Func<bool> complete, string stage = "settings stage")
  {
    var deadline = Stopwatch.StartNew();
    while (!complete()) {
      Application.DoEvents();
      if (uiError != null) throw new Exception("Uncaught UI callback", uiError);
      if (deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Controlled " + stage + " did not complete.");
      Thread.Sleep(1);
    }
    if (uiError != null) throw new Exception("Uncaught UI callback", uiError);
  }
  private static void Heartbeats()
  {
    var ticks = 0;
    using (var timer = new System.Windows.Forms.Timer { Interval = 10 }) {
      timer.Tick += (_, __) => ticks++; timer.Start(); Pump(() => ticks >= 3);
    }
  }
  private static Settings Saved(string model = "saved-a", string effort = "low")
  {
    var settings = new Settings { ZoomLevel = 100, EnabledMarkdownPlugins = new[] { "attrs" } };
    settings.Translation = CliProfiles.Defaults("codex", executableA);
    settings.Translation.Model = model; settings.Translation.UseDefaultModel = false; settings.Translation.ReasoningEffort = effort;
    settings.Translation.ShowButtons = false; settings.Translation.ParallelRequests = 3;
    return settings;
  }
  private static List<CliInstallation> Installations() => new List<CliInstallation> {
    new CliInstallation { ProviderId = "codex", Executable = executableA }, new CliInstallation { ProviderId = "cursor", Executable = executableB }
  };
  private static CliModelCatalog Catalog(string id = "saved-a", string name = "Alpha") => new CliModelCatalog {
    DefaultModelId = id, ConfiguredReasoningEffort = "low", Models = new List<CliModel> {
      new CliModel { Id = id, Name = name, IsDefault = true, DefaultReasoningEffort = "low", ReasoningEfforts = new List<CliReasoningEffort> {
        new CliReasoningEffort { Id = "low" }, new CliReasoningEffort { Id = "high" }
      } }, new CliModel { Id = id + "-other", Name = name + " other" }
    }
  };
  private static bool Idle(SettingsForm form) => Field<CancellationTokenSource>(form, "modelCancellation") == null && Field<CancellationTokenSource>(form, "cliDiscoveryCancellation") == null;
  private static bool Has(SettingsForm form, string title) => Find<ComboBox>(form, "translationModel").Items.Cast<object>().Any(item => item.ToString().Contains(title));

  private sealed class BackgroundSettings : SettingsForm
  {
    public int Paints;
    public Action FirstPaint;
    public BackgroundSettings(Settings settings, SettingsDiscoveryCache cache) : base(settings, cache) { }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000; return p; } }
    protected override void OnShown(EventArgs e)
    {
      base.OnShown(e);
      // An occluded background window may not receive WM_PAINT promptly.
      // Service its native paint without activation or waiting for discovery.
      Invalidate(); Update();
      using (var bitmap = new System.Drawing.Bitmap(Width, Height)) DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, Width, Height));
    }
    protected override void WndProc(ref Message message)
    {
      var paint = message.Msg == 0x000f || message.Msg == 0x0317 || message.Msg == 0x0318;
      base.WndProc(ref message);
      if (paint && Visible) { Paints++; if (Paints == 1) FirstPaint?.Invoke(); }
    }
  }

  // Reproduce a settings window whose child controls cover the entire client.
  // Do not DrawToBitmap/Update or call base.OnPaint: model loading must not
  // depend on an artificial paint sent by the screenshot test harness.
  private sealed class NoPaintSettings : SettingsForm
  {
    public NoPaintSettings(Settings settings, SettingsDiscoveryCache cache) : base(settings, cache) { }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x08000000; return p; } }
    protected override void OnPaint(PaintEventArgs e) { }
  }

  private static void NoPaintDiscovery()
  {
    using (var backend = new Backend()) {
      var scan = backend.Scan(); var models = backend.Models();
      using (var form = new NoPaintSettings(Saved(), backend.Cache)) {
        form.SelectTranslationTab(); form.Show();
        Pump(() => scan.Started.Task.IsCompleted && models.Started.Task.IsCompleted);
        models.Complete(Catalog()); scan.Complete(Installations());
        Pump(() => Has(form, "Alpha") && Idle(form));
        Check(Has(form, "Alpha"), "cold CLI model catalog reaches a shown form without any parent paint");
        form.Close();
      }
      using (var form = new NoPaintSettings(Saved(), backend.Cache)) {
        Check(Has(form, "Alpha") && Find<ComboBox>(form, "translationModel").Items.Count == 3,
          "warm CLI catalog is fully populated in the constructor before Show or painting");
      }
      var settings = Saved(); settings.Translation.ConnectionMode = "api";
      var profile = new ApiConnection { Endpoint = "https://example.test/v1", Model = "api-model" };
      settings.Translation.ApiConnections.Add(profile); settings.Translation.SelectedApiConnectionId = profile.Id;
      var api = backend.ApiModels();
      using (var form = new NoPaintSettings(settings, backend.Cache)) {
        form.SelectTranslationTab(); form.Show(); Pump(() => api.Started.Task.IsCompleted);
        api.Complete(Catalog("api-model", "API model"));
        Pump(() => Find<ComboBox>(form, "apiModel").Items.Count == 2);
        Check(Find<ComboBox>(form, "apiModel").Items.Count == 2,
          "cold API catalog reaches a shown form without any parent paint or manual refresh");
        form.Close();
      }
      using (var form = new NoPaintSettings(settings, backend.Cache)) {
        Check(Find<ComboBox>(form, "apiModel").Items.Count == 2,
          "warm API catalog is fully populated in the constructor before Show or painting");
      }
      Check(backend.ModelCalls == 1 && backend.ApiModelCalls == 1, "warm constructors make no duplicate backend requests");
    }
  }

  private sealed class Gate<T> : IDisposable where T : class
  {
    private readonly ManualResetEventSlim release = new ManualResetEventSlim();
    private readonly TaskCompletionSource<T> result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenRegistration registration;
    public readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource<bool> Canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly bool BlockSynchronously, IgnoreCancellation;
    public int ThreadId;
    public Gate(bool block = false, bool ignoreCancellation = false) { BlockSynchronously = block; IgnoreCancellation = ignoreCancellation; }
    public Task<T> Load(CancellationToken token)
    {
      ThreadId = Thread.CurrentThread.ManagedThreadId;
      registration = token.Register(() => Canceled.TrySetResult(true)); Started.TrySetResult(true);
      if (BlockSynchronously && !release.Wait(TimeSpan.FromSeconds(5), IgnoreCancellation ? CancellationToken.None : token))
        throw new TimeoutException("Synchronous fixture loader was not released.");
      return result.Task;
    }
    public void Complete(T value) { result.TrySetResult(value); release.Set(); }
    public void Fail(Exception error) { result.TrySetException(error); release.Set(); }
    public void Dispose() { result.TrySetCanceled(); release.Set(); registration.Dispose(); release.Dispose(); }
  }
  private sealed class Backend : IDisposable
  {
    private readonly object gate = new object();
    private readonly Queue<Gate<List<CliInstallation>>> scans = new Queue<Gate<List<CliInstallation>>>();
    private readonly Dictionary<string, Queue<Gate<CliModelCatalog>>> models = new Dictionary<string, Queue<Gate<CliModelCatalog>>>();
    private readonly Queue<Gate<CliModelCatalog>> apiModels = new Queue<Gate<CliModelCatalog>>();
    private readonly List<IDisposable> owned = new List<IDisposable>();
    public int InstallationCalls, ModelCalls, ApiModelCalls;
    private DateTime now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public readonly SettingsDiscoveryCache Cache;
    public Backend()
    {
      Cache = new SettingsDiscoveryCache(token => {
        Gate<List<CliInstallation>> next; lock (gate) { InstallationCalls++; next = scans.Dequeue(); } return next.Load(token);
      }, (provider, executable, token) => {
        Gate<CliModelCatalog> next; lock (gate) { ModelCalls++; next = models[provider].Dequeue(); } return next.Load(token);
      }, () => now, TimeSpan.FromMinutes(5), (profile, timeout, token) => {
        Gate<CliModelCatalog> next; lock (gate) { ApiModelCalls++; next = apiModels.Dequeue(); } return next.Load(token);
      });
    }
    public Gate<List<CliInstallation>> Scan(bool block = false, bool ignore = false)
    { var value = new Gate<List<CliInstallation>>(block, ignore); lock (gate) { scans.Enqueue(value); owned.Add(value); } return value; }
    public Gate<CliModelCatalog> Models(string provider = "codex", bool block = false, bool ignore = false)
    {
      var value = new Gate<CliModelCatalog>(block, ignore);
      lock (gate) { if (!models.ContainsKey(provider)) models[provider] = new Queue<Gate<CliModelCatalog>>(); models[provider].Enqueue(value); owned.Add(value); }
      return value;
    }
    public Gate<CliModelCatalog> ApiModels(bool block = false, bool ignore = false)
    { var value = new Gate<CliModelCatalog>(block, ignore); lock (gate) { apiModels.Enqueue(value); owned.Add(value); } return value; }
    public void Seed(CliModelCatalog catalog = null)
    {
      Scan().Complete(Installations()); Models().Complete(catalog ?? Catalog());
      Cache.LoadInstallationsAsync(false, CancellationToken.None).GetAwaiter().GetResult();
      Cache.LoadModelsAsync("codex", executableA, false, CancellationToken.None).GetAwaiter().GetResult();
    }
    public void Expire() { now += TimeSpan.FromMinutes(6); }
    public void Dispose() { foreach (var value in owned) value.Dispose(); }
  }

  private static void StartupPrefetch()
  {
    using (var backend = new Backend()) {
      var scan = backend.Scan(true); var models = backend.Models(block: true);
      using (var form = new BackgroundSettings(Saved(), backend.Cache)) {
      form.Show(); Heartbeats();
      Pump(() => scan.Started.Task.IsCompleted && models.Started.Task.IsCompleted);
      Check(form.Paints > 0 && scan.ThreadId != uiThread && models.ThreadId != uiThread,
        "opening the dialog starts CLI and model prefetch on workers while preview paints");
      models.Complete(Catalog()); scan.Complete(Installations()); Heartbeats();
      form.Close();
      }
    }
  }

  private static void ConcurrentAndResponsive()
  {
    using (var backend = new Backend()) {
      var scan = backend.Scan(true); var models = backend.Models(block: true);
      using (var form = new BackgroundSettings(Saved(), backend.Cache)) {
        form.SelectTranslationTab();
        var shownObserved = false;
        form.Shown += (_, __) => shownObserved = true;
        form.Show();
        Pump(() => shownObserved);
        Pump(() => scan.Started.Task.IsCompleted && models.Started.Task.IsCompleted);
        Heartbeats();
        Check(scan.ThreadId != uiThread && models.ThreadId != uiThread, "synchronous installation and model loaders execute off the UI thread");
        Check(form.Paints > 0 && Find<Button>(form, "btnSave").Enabled && Find<ComboBox>(form, "translationModel").Enabled,
          "first paint and UI heartbeat occur while both loaders are blocked and Save/model remain usable");
        Check(Draft(form).Model == "saved-a" && Draft(form).ReasoningEffort == "low", "blocked discovery retains the saved model and effort");
        models.Complete(Catalog()); Pump(() => Has(form, "Alpha") && Field<CancellationTokenSource>(form, "modelCancellation") == null);
        Check(!scan.Canceled.Task.IsCompleted && backend.InstallationCalls == 1, "saved absolute launcher gets models without waiting for installation scan");
        scan.Complete(Installations()); Pump(() => Idle(form));
        Check(backend.ModelCalls == 1 && Draft(form).Model == "saved-a", "scan completion reuses the fresh catalog without a duplicate model request");
        form.Close();
      }
    }
  }

  private static Dictionary<string, System.Drawing.Rectangle> VisibleGeometry(SettingsForm form)
  {
    var result = new Dictionary<string, System.Drawing.Rectangle>();
    foreach (var name in new[] { "translationConnectionMode", "translationParallelGroup", "translationParallelRequests", "translationMinimumChunk", "translationCli", "translationExecutable",
      "translationModelRow", "translationModel", "translationModelStatus", "translationDefaults", "translationShowButtons", "btnSave", "btnCancel",
      "apiConnections", "apiModelRow", "apiModel", "apiActions", "apiStatus", "apiTimeout", "apiShowButtons" }) {
      var control = form.Controls.Find(name, true).Single();
      if (control.Visible) result[name] = new System.Drawing.Rectangle(form.PointToClient(control.PointToScreen(System.Drawing.Point.Empty)), control.Size);
    }
    return result;
  }
  private static void GeometryUnchanged(Dictionary<string, System.Drawing.Rectangle> expected, SettingsForm form, string label)
  {
    var current = VisibleGeometry(form);
    var differences = expected.Where(item => !current.ContainsKey(item.Key) || current[item.Key] != item.Value)
      .Select(item => item.Key + ": " + item.Value + " -> " + (current.ContainsKey(item.Key) ? current[item.Key].ToString() : "missing"))
      .Concat(current.Keys.Except(expected.Keys).Select(name => name + ": added")).ToArray();
    Check(expected.Count > 0 && differences.Length == 0, label + (differences.Length == 0 ? "" : " [" + string.Join("; ", differences) + "]"));
  }
  private static void DiscoveryGeometry()
  {
    using (var backend = new Backend()) {
      var scan = backend.Scan(); var models = backend.Models();
      using (var form = new BackgroundSettings(Saved("unknown-saved", "high"), backend.Cache)) {
        Dictionary<string, System.Drawing.Rectangle> firstPaint = null, shown = null;
        form.FirstPaint = () => firstPaint = VisibleGeometry(form);
        form.Shown += (_, __) => shown = VisibleGeometry(form);
        form.SelectTranslationTab(); form.Show();
        Pump(() => firstPaint != null && shown != null && scan.Started.Task.IsCompleted && models.Started.Task.IsCompleted);
        Heartbeats();
        Check(form.Paints > 0 && Find<Button>(form, "btnSave").Enabled && !models.Canceled.Task.IsCompleted,
          "cold CLI settings paint and respond before delayed metadata completes");
        GeometryUnchanged(firstPaint, form, "cold CLI control membership and bounds stay unchanged from the first native paint");
        GeometryUnchanged(shown, form, "starting CLI discovery does not move the shown controls");
        models.Complete(Catalog("unknown-saved", "Supported model")); scan.Complete(Installations()); Pump(() => Idle(form)); Heartbeats();
         Check(Find<ComboBox>(form, "translationEffort").Visible && Draft(form).ReasoningEffort == "high",
          "delayed catalog makes the saved effort available without delaying the dialog");
        var beforeEffortWidth = shown["translationModel"].Width;
        var allowedModelBounds = shown["translationModel"];
        allowedModelBounds.Width = Find<ComboBox>(form, "translationModel").Width;
        shown["translationModel"] = allowedModelBounds;
        Check(allowedModelBounds.Width < beforeEffortWidth,
          "the model field yields horizontal space when supported effort becomes available");
        GeometryUnchanged(shown, form, "supported effort reflows within its model row without moving other controls");
        var failure = backend.Models(); Find<Button>(form, "translationModelsRefresh").PerformClick(); Pump(() => failure.Started.Task.IsCompleted);
        failure.Fail(new InvalidOperationException(string.Concat(Enumerable.Repeat("Long metadata failure details. ", 20))));
        Pump(() => Idle(form)); Heartbeats();
        Check(Find<Label>(form, "translationModelStatus").Text.Contains("Long metadata failure details"), "long discovery failure remains available in the status text");
        GeometryUnchanged(shown, form, "long CLI failure status leaves all visible control bounds and membership unchanged"); form.Close();
      }
    }
    using (var backend = new Backend()) {
      var settings = Saved(); settings.Translation.ConnectionMode = "api";
      settings.Translation.ApiConnections.Add(new ApiConnection { Endpoint = "https://example.test/v1", Model = "fixture-model" });
      var api = backend.ApiModels(block: true);
      using (var form = new BackgroundSettings(settings, backend.Cache)) {
        Dictionary<string, System.Drawing.Rectangle> firstPaint = null, shown = null;
        form.FirstPaint = () => firstPaint = VisibleGeometry(form); form.Shown += (_, __) => shown = VisibleGeometry(form);
        form.SelectTranslationTab(); form.Show(); Pump(() => firstPaint != null && shown != null && api.Started.Task.IsCompleted); Heartbeats();
        GeometryUnchanged(firstPaint, form, "API controls stay in their first native paint positions after Shown");
        var message = string.Concat(Enumerable.Repeat("Synthetic API status details. ", 20));
        // Exercise a delayed status update on the same UI dispatcher as API results, without network or credentials.
        form.BeginInvoke(new Action(() => Find<Label>(form, "apiStatus").Text = message));
        Pump(() => Find<Label>(form, "apiStatus").Text == message); Heartbeats();
        GeometryUnchanged(shown, form, "long asynchronous API status does not recompose the shown fields or footer");
        Check(backend.InstallationCalls == 0 && backend.ModelCalls == 0 && Find<Button>(form, "btnSave").Enabled,
          "stable API opening remains responsive and performs no CLI discovery");
        api.Complete(Catalog("fixture-model", "Fixture API")); form.Close();
      }
    }
  }

  private static void ApiAutoDiscovery()
  {
    using (var backend = new Backend()) {
      var settings = Saved(); settings.Translation.ConnectionMode = "api";
      var first = new ApiConnection { Name = "First", Endpoint = "https://first.example.test/v1", Model = "api-z", ReasoningEffort = "high" };
      var second = new ApiConnection { Name = "Second", Endpoint = "https://second.example.test/v1", Model = "api-b" };
      settings.Translation.ApiConnections.Add(first); settings.Translation.ApiConnections.Add(second);
      settings.Translation.SelectedApiConnectionId = first.Id;
      var startup = backend.ApiModels(block: true);
      using (var form = new BackgroundSettings(settings, backend.Cache)) {
        form.SelectTranslationTab(); form.Show(); Pump(() => startup.Started.Task.IsCompleted); Heartbeats();
        Check(form.Paints > 0 && Find<Button>(form, "btnSave").Enabled && startup.ThreadId != uiThread,
          "API model prefetch starts with form construction without blocking paint or Save");
        var firstCatalog = new CliModelCatalog { Models = new List<CliModel> {
          new CliModel { Id = "api-z", Name = "Zulu" }, new CliModel { Id = "api-a", Name = "Alpha" }
        } };
        startup.Complete(firstCatalog);
        var model = Find<ComboBox>(form, "apiModel");
        Pump(() => model.Items.Count == 2 && Field<CancellationTokenSource>(form, "apiCancellation") == null);
        Check(model.Items[0].ToString() == "Alpha" && (model.SelectedItem as CliModel)?.Id == "api-z" &&
          Find<Button>(form, "apiRefreshModels").Text == "Обновить",
          "opening the saved API profile sorts models, restores selection and labels Refresh consistently");
        var profileName = Find<TextBox>(form, "apiName");
        var leave = typeof(Control).GetMethod("OnLeave", BindingFlags.Instance | BindingFlags.NonPublic);
        leave.Invoke(profileName, new object[] { EventArgs.Empty }); Heartbeats();
        Check(model.Items.Count == 2 && backend.ApiModelCalls == 1 && (model.SelectedItem as CliModel)?.Id == "api-z",
          "leaving an unchanged API profile name preserves the loaded catalog and selection without a new request");
        profileName.Text = "Renamed API"; leave.Invoke(profileName, new object[] { EventArgs.Empty }); Heartbeats();
        Check(model.Items.Count == 2 && Find<ComboBox>(form, "apiConnections").Text == "Renamed API" && backend.ApiModelCalls == 1,
          "renaming an API profile refreshes only its caption and preserves the current catalog");
         Check(!Find<ComboBox>(form, "apiEffort").Visible && !Field<Label>(form, "apiEffortLabel").Visible && Draft(form).ActiveApiConnection.ReasoningEffort == "",
          "API effort is unavailable and a legacy value is cleared when the selected model advertises no levels");
        var switched = backend.ApiModels();
        Find<ComboBox>(form, "apiConnections").SelectedItem = Find<ComboBox>(form, "apiConnections").Items.Cast<ApiConnection>().Single(p => p.Id == second.Id);
        Pump(() => switched.Started.Task.IsCompleted);
        Check(model.Items.Count == 0 && model.Text == "api-b" && Find<Button>(form, "btnSave").Enabled,
          "changing API profile clears stale models and keeps the saved model editable during discovery");
        switched.Complete(Catalog("api-b", "Beta")); Pump(() => (model.SelectedItem as CliModel)?.Id == "api-b");
        var refresh = backend.ApiModels(); Find<Button>(form, "apiRefreshModels").PerformClick(); Pump(() => refresh.Started.Task.IsCompleted);
        refresh.Complete(Catalog("api-b", "Refreshed Beta")); Pump(() => model.Items.Count > 0 && model.Items[0].ToString() == "Refreshed Beta");
        Check(backend.ApiModelCalls == 3, "explicit API Refresh forces a new lookup after automatic profile loading");
        var preset = backend.ApiModels(); Find<ComboBox>(form, "apiPreset").SelectedIndex = 1;
        Pump(() => preset.Started.Task.IsCompleted);
        Check(Find<TextBox>(form, "apiEndpoint").Text == "https://api.openai.com/v1" && model.Items.Count == 0,
          "choosing an API service with an endpoint starts discovery immediately and clears the prior catalog");
        preset.Complete(Catalog("gpt-6.1-sol", "GPT-6.1 Sol")); Pump(() => model.Items.Count > 0);
        model.SelectedItem = model.Items.Cast<CliModel>().Single(item => item.Id == "gpt-6.1-sol");
        var apiEffort = Find<ComboBox>(form, "apiEffort");
        apiEffort.SelectedItem = apiEffort.Items.Cast<object>().Single(item => item.ToString() == "high");
         Check(apiEffort.Visible && Field<Label>(form, "apiEffortLabel").Visible && Draft(form).ActiveApiConnection.ReasoningEffort == "high" &&
          Draft(form).ActiveApiConnection.ReasoningEffortModel == "gpt-6.1-sol" &&
          Draft(form).ActiveApiConnection.ReasoningEffortCatalogKey == SettingsDiscoveryCache.ApiModelKey(Draft(form).ActiveApiConnection),
          "only a catalog-advertised API effort is bound to its selected model");
        model.SelectedItem = model.Items.Cast<CliModel>().Single(item => item.Id == "gpt-6.1-sol-other");
         Check(!apiEffort.Visible && apiEffort.Items.Count == 1 && Draft(form).ActiveApiConnection.ReasoningEffort == "",
          "changing API model clears an effort value that is not supported by the new model");
        var emptyPicker = backend.ApiModels(); Find<TextBox>(form, "apiEndpoint").Text = "https://third.example.test/v1";
        Check(model.Items.Count == 0, "editing an endpoint clears a stale API catalog without losing the editable model field");
        model.DroppedDown = true; Pump(() => emptyPicker.Started.Task.IsCompleted);
        Heartbeats();
        Check(!model.DroppedDown && model.Items.Count == 0,
          "an empty API model popup is closed while asynchronous discovery is pending");
        emptyPicker.Complete(Catalog("third-model", "Third model")); Pump(() => model.Items.Count > 0);
        Check(backend.ApiModelCalls == 5, "opening an empty API model picker triggers discovery for the current endpoint");
        model.DroppedDown = false; Heartbeats();

        var lateEdit = backend.ApiModels(); Find<TextBox>(form, "apiEndpoint").Text = "https://fourth.example.test/v1";
        leave.Invoke(Find<TextBox>(form, "apiEndpoint"), new object[] { EventArgs.Empty });
        Pump(() => lateEdit.Started.Task.IsCompleted); Heartbeats();
        model.Text = "late-model"; Find<NumericUpDown>(form, "apiMaxTokens").Value = 1000;
        Find<TextBox>(form, "apiParameters").Text = "{\"user\":\"fixture\"}"; Heartbeats();
        Check(Field<CancellationTokenSource>(form, "apiCancellation") != null && !lateEdit.Canceled.Task.IsCompleted,
          "editing the selected model and output options does not cancel an independent API model-list request");
        lateEdit.Complete(Catalog("late-model", "Late model"));
        Pump(() => model.Items.Count > 0 && Field<CancellationTokenSource>(form, "apiCancellation") == null);
        Check((model.SelectedItem as CliModel)?.Id == "late-model" && Draft(form).ActiveApiConnection.Model == "late-model",
          "a late API model-list result preserves the current model rather than its old request snapshot");

        model.DroppedDown = true; Heartbeats();
        Find<TextBox>(form, "apiEndpoint").Text = "https://fifth.example.test/v1"; Heartbeats();
        Check(!model.DroppedDown && model.Items.Count == 0 && uiError == null,
          "changing API identity closes an open native model popup before clearing its selected items");
        form.Close();
      }
    }
  }

  [DllImport("user32.dll")]
  private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wparam, IntPtr lparam);
  [DllImport("user32.dll")]
  private static extern bool PostMessage(IntPtr handle, int message, IntPtr wparam, IntPtr lparam);
  [DllImport("user32.dll")]
  private static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")]
  private static extern bool GetComboBoxInfo(IntPtr handle, ref ComboInfo info);
  [DllImport("user32.dll")]
  private static extern bool GetWindowRect(IntPtr handle, out NativeRectangle rectangle);
  [StructLayout(LayoutKind.Sequential)]
  private struct NativeRectangle { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)]
  private struct ComboInfo {
    public int Size;
    public NativeRectangle Item, Button;
    public int State;
    public IntPtr Combo, Edit, List;
  }
  [StructLayout(LayoutKind.Sequential)]
  private struct NativeWindowPosition {
    public IntPtr Window, InsertAfter;
    public int X, Y, Width, Height;
    public uint Flags;
  }
  private sealed class PopupObserver : NativeWindow, IDisposable
  {
    public int ShownCount;
    public PopupObserver(IntPtr handle) { AssignHandle(handle); }
    protected override void WndProc(ref Message message)
    {
      if (message.Msg == 0x0018 && message.WParam != IntPtr.Zero) ShownCount++;
      if (message.Msg == 0x0047 && message.LParam != IntPtr.Zero &&
        (((NativeWindowPosition)Marshal.PtrToStructure(message.LParam, typeof(NativeWindowPosition))).Flags & 0x0040) != 0) ShownCount++;
      base.WndProc(ref message);
    }
    public void Dispose() { ReleaseHandle(); }
  }
  private static void NativeModelPicker()
  {
    using (var backend = new Backend()) {
      var settings = Saved(); settings.Translation.ConnectionMode = "api";
      var profile = new ApiConnection { Name = "Native picker", Endpoint = "https://fixture.example.test/v1", Model = "api-model" };
      settings.Translation.ApiConnections.Add(profile); settings.Translation.SelectedApiConnectionId = profile.Id;
      var request = backend.ApiModels();
      var foreground = GetForegroundWindow();
      using (var form = new BackgroundSettings(settings, backend.Cache)) {
        form.SelectTranslationTab(); form.Show(); Pump(() => request.Started.Task.IsCompleted); Heartbeats();
        var model = Find<ComboBox>(form, "apiModel");
        var info = new ComboInfo { Size = Marshal.SizeOf(typeof(ComboInfo)) };
        Check(GetComboBoxInfo(model.Handle, ref info) && info.Edit != IntPtr.Zero && info.List != IntPtr.Zero,
          "native API picker exposes its edit child and popup for interaction regression checks");
        var opened = 0; model.DropDown += (_, __) => opened++;
        using (var popup = new PopupObserver(info.List)) {
          SendMessage(model.Handle, 0x014f, new IntPtr(1), IntPtr.Zero); Heartbeats();
          Check(opened == 1 && !model.DroppedDown && popup.ShownCount == 0,
            "native CB_SHOWDROPDOWN requests an empty API catalog without displaying an empty popup");
          var point = new IntPtr((model.Width - 8) | (model.Height / 2 << 16));
          SendMessage(model.Handle, 0x0201, new IntPtr(1), point);
          SendMessage(model.Handle, 0x0202, IntPtr.Zero, point); Heartbeats();
          Check(opened == 2 && !model.DroppedDown && popup.ShownCount == 0,
            "native arrow click requests an empty API catalog without flashing a blank popup");
          // Posting to the real native EDIT child exercises WinForms keyboard
          // pretranslation. SendMessage alone bypasses that message-loop path.
          Check(PostMessage(info.Edit, 0x0100, new IntPtr((int)Keys.F4), new IntPtr(0x003e0001)),
            "F4 is posted to the native editable model field");
          Pump(() => opened == 3); Heartbeats();
          PostMessage(info.Edit, 0x0101, new IntPtr((int)Keys.F4), new IntPtr(unchecked((int)0xc03e0001)));
          Check(!model.DroppedDown && popup.ShownCount == 0 && backend.ApiModelCalls == 1,
            "queued native F4 keeps the empty popup hidden and shares the pending catalog request");

          request.Complete(Catalog("api-model", "Model with effort"));
          Pump(() => model.Items.Count == 2 && Field<CancellationTokenSource>(form, "apiCancellation") == null); Heartbeats();
          model.DroppedDown = true; Heartbeats();
          NativeRectangle rectangle;
          Check(model.DroppedDown && popup.ShownCount > 0 && GetWindowRect(info.List, out rectangle) && rectangle.Right - rectangle.Left == model.Width,
            "the populated native model popup opens with exactly the field width beside visible effort");
          model.DroppedDown = false;
          var narrowWidth = model.Width;
          model.SelectedItem = model.Items.Cast<CliModel>().Single(item => item.Id == "api-model-other"); Heartbeats();
          model.DroppedDown = true; Heartbeats();
          Check(model.Width > narrowWidth && GetWindowRect(info.List, out rectangle) && rectangle.Right - rectangle.Left == model.Width,
            "the native popup follows the expanded model field after unsupported effort is hidden");
          model.DroppedDown = false;
        }
        form.Close();
      }
      Check(foreground == GetForegroundWindow(), "native picker checks preserve the user's foreground window");
    }
  }

  private static void WarmCacheAndLateEdits()
  {
    using (var backend = new Backend()) {
      backend.Seed();
      using (var form = new BackgroundSettings(Saved(), backend.Cache)) {
        form.SelectTranslationTab(); form.Show(); Heartbeats(); Pump(() => Idle(form));
        Check(Has(form, "Alpha") && Draft(form).Model == "saved-a" && Draft(form).ReasoningEffort == "low",
          "warm catalog is applied after first paint without changing saved model or effort");
        Check(backend.InstallationCalls == 1 && backend.ModelCalls == 1, "fresh cached opening makes no new backend requests");
        var refresh = backend.Models(); Find<Button>(form, "translationModelsRefresh").PerformClick(); Pump(() => refresh.Started.Task.IsCompleted);
        Check(Find<Button>(form, "btnSave").Enabled && Find<ComboBox>(form, "translationModel").Enabled && Find<ComboBox>(form, "translationEffort").Enabled,
          "cached model and advertised effort remain editable during forced refresh");
        var effort = Find<ComboBox>(form, "translationEffort"); effort.SelectedItem = effort.Items.Cast<object>().Single(item => item.ToString() == "high");
        refresh.Complete(Catalog(name: "Fresh Alpha")); Pump(() => Idle(form));
        Check(backend.ModelCalls == 2 && Draft(form).ReasoningEffort == "high", "forced model refresh reaches the backend and preserves a late effort edit");
        var scan = backend.Scan(); Find<Button>(form, "translationCliRefresh").PerformClick(); Pump(() => scan.Started.Task.IsCompleted);
        scan.Complete(Installations()); Pump(() => Idle(form));
        Check(backend.InstallationCalls == 2 && backend.ModelCalls == 2, "Find CLI forces a scan while retaining a fresh model catalog");
        form.Close();
      }
      backend.Expire(); var staleScan = backend.Scan(); var staleModels = backend.Models();
      var original = Saved();
      using (var form = new BackgroundSettings(original, backend.Cache)) {
        form.SelectTranslationTab(); form.Show(); Pump(() => staleScan.Started.Task.IsCompleted && staleModels.Started.Task.IsCompleted);
        Check(Draft(form).Model == "saved-a", "expired catalog refresh preserves the saved model during loading");
        Field<CheckBox>(form, "translationUseManualModel").Checked = true; Find<TextBox>(form, "translationManualModel").Text = "manual-user-model";
        Field<CheckBox>(form, "translationCustomArguments").Checked = true; Find<TextBox>(form, "translationArguments").Text = "--user-kept";
        Find<NumericUpDown>(form, "translationParallelRequests").Value = 6; Find<NumericUpDown>(form, "translationMinimumChunk").Value = 3700; Find<NumericUpDown>(form, "translationTimeout").Value = 47;
        Heartbeats(); Check(Find<Button>(form, "btnSave").Enabled, "late manual settings can be saved while metadata remains pending");
        staleModels.Complete(Catalog(name: "Replacement Alpha")); staleScan.Complete(Installations()); Pump(() => Idle(form));
        var edited = Draft(form);
        Check(Field<CheckBox>(form, "translationUseManualModel").Checked && edited.Model == "manual-user-model", "late catalog does not replace the manually typed model");
        Check(edited.UseCustomArguments && edited.Arguments == "--user-kept" && !Find<TextBox>(form, "translationArguments").ReadOnly,
          "late scan/catalog does not reset custom arguments or their editing mode");
        Check(edited.ParallelRequests == 6 && edited.MinimumChunkCharacters == 3700 && edited.TimeoutSeconds == 47, "late metadata preserves threading, minimum and timeout edits");
        Find<Button>(form, "btnSave").PerformClick();
        Check(form.DialogResult == DialogResult.OK && form.TranslationOptions.Model == "manual-user-model" && original.Translation.Model == "saved-a",
          "Save accepts edited values without mutating the supplied settings");
        form.Close();
      }
    }
    using (var backend = new Backend()) {
      backend.Seed(Catalog("unrelated", "Unrelated"));
      using (var form = new BackgroundSettings(Saved("missing-saved", "high"), backend.Cache))
        Check(Draft(form).Model == "missing-saved" && Draft(form).ReasoningEffort == "high", "missing saved model and unconfirmed high effort survive a warm catalog");
    }
  }

  private static void SelectionAndCancellation()
  {
    using (var backend = new Backend()) {
      backend.Scan().Complete(Installations()); backend.Cache.LoadInstallationsAsync(false, CancellationToken.None).GetAwaiter().GetResult(); backend.Expire();
      var oldScan = backend.Scan(ignore: true); var oldModels = backend.Models(ignore: true); var newModels = backend.Models("cursor");
      using (var form = new BackgroundSettings(Saved(), backend.Cache)) {
        form.SelectTranslationTab(); form.Show(); Pump(() => oldScan.Started.Task.IsCompleted && oldModels.Started.Task.IsCompleted);
        Pump(() => Field<Task>(form, "modelDiscoveryTask") != null);
        var oldTask = Field<Task>(form, "modelDiscoveryTask");
        oldScan.Complete(Installations());
        Pump(() => Find<ComboBox>(form, "translationCli").Items.Cast<CliInstallation>().Any(value => value.ProviderId == "cursor"));
        var picker = Find<ComboBox>(form, "translationCli"); picker.SelectedItem = picker.Items.Cast<CliInstallation>().Single(value => value.ProviderId == "cursor");
        Pump(() => newModels.Started.Task.IsCompleted && oldTask.IsCompleted);
        Check(Draft(form).ProviderId == "cursor", "changing CLI discards the old catalog view without blocking the new lookup");
        newModels.Complete(Catalog("new-b", "Fresh Beta")); Pump(() => Idle(form), "new CLI catalog");
        oldModels.Complete(Catalog(name: "STALE Alpha")); Heartbeats();
        Check(Draft(form).ProviderId == "cursor" && Draft(form).Executable == executableB && Has(form, "Fresh Beta") && !Has(form, "STALE Alpha"),
          "late results cannot replace the selected CLI or its catalog");
        var pending = backend.Models("cursor", ignore: true); Find<Button>(form, "translationModelsRefresh").PerformClick(); Pump(() => pending.Started.Task.IsCompleted, "API switch setup");
        var pendingTask = Field<Task>(form, "modelDiscoveryTask");
        Find<ComboBox>(form, "translationConnectionMode").SelectedIndex = 1;
        Pump(() => pending.Canceled.Task.IsCompleted && pendingTask.IsCompleted, "API switch cancellation");
        pending.Complete(Catalog("wrong", "STALE API")); Heartbeats();
        Check(Draft(form).UseApi && Find<Button>(form, "btnSave").Enabled && !Has(form, "STALE API"), "API mode invalidates a pending CLI response without disabling Save");
        form.Close();
      }
    }
  }

  private static Task ApplyCli(SettingsForm form, CliInstallation installation, bool resetDefaults) =>
    (Task)typeof(SettingsForm).GetMethod("ApplyCliAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { installation, resetDefaults });
  private static void PickCli(SettingsForm form, string provider, string executable = null)
  {
    var picker = Find<ComboBox>(form, "translationCli");
    var path = executable ?? (provider == "codex" ? executableA : executableB);
    picker.SelectedItem = picker.Items.Cast<CliInstallation>().Single(value => value.ProviderId == provider && string.Equals(value.Executable, path, StringComparison.OrdinalIgnoreCase));
    Pump(() => Idle(form), "profile selection");
  }
  private static bool SameValues(TranslationOptions expected, TranslationOptions actual) =>
    expected.ProviderId == actual.ProviderId && string.Equals(expected.Executable, actual.Executable, StringComparison.OrdinalIgnoreCase)
    && expected.Model == actual.Model && expected.UseDefaultModel == actual.UseDefaultModel && expected.UseManualModel == actual.UseManualModel
    && expected.ReasoningEffort == actual.ReasoningEffort && expected.UseCustomArguments == actual.UseCustomArguments
    && expected.Arguments == actual.Arguments && expected.OutputFormat == actual.OutputFormat && expected.TimeoutSeconds == actual.TimeoutSeconds
    && expected.ParallelRequests == actual.ParallelRequests && expected.MinimumChunkCharacters == actual.MinimumChunkCharacters && expected.ShowButtons == actual.ShowButtons;

  private static void CliProfilesSettings()
  {
    using (var backend = new Backend()) {
      backend.Seed(); backend.Models("cursor").Complete(Catalog("saved-b", "Beta"));
      backend.Cache.LoadModelsAsync("cursor", executableB, false, CancellationToken.None).GetAwaiter().GetResult();
      var original = Saved(); TranslationOptions expectedA, expectedB, expectedC; Settings saved;
      using (var form = new BackgroundSettings(original, backend.Cache)) {
        form.SelectTranslationTab(); form.Show(); Heartbeats(); Pump(() => Idle(form));
        var effort = Find<ComboBox>(form, "translationEffort");
        effort.SelectedItem = effort.Items.Cast<object>().Single(item => item.ToString() == "high");
        Field<CheckBox>(form, "translationCustomArguments").Checked = true;
        Find<TextBox>(form, "translationArguments").Text = "--fixture-a {input}";
        Find<ComboBox>(form, "translationOutput").SelectedItem = "text";
        Find<NumericUpDown>(form, "translationTimeout").Value = 31;
        Find<NumericUpDown>(form, "translationParallelRequests").Value = 2;
        Find<NumericUpDown>(form, "translationMinimumChunk").Value = 2700;
        Find<CheckBox>(form, "translationShowButtons").Checked = false; expectedA = Draft(form);

        var same = ApplyCli(form, new CliInstallation { ProviderId = "codex", Executable = executableA }, false); Pump(() => same.IsCompleted);
        same.GetAwaiter().GetResult();
        var count = Find<ComboBox>(form, "translationCli").Items.Count;
        var alias = (Task)typeof(SettingsForm).GetMethod("SelectCliPathAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { executableA.ToUpperInvariant() });
        Pump(() => alias.IsCompleted);
        alias.GetAwaiter().GetResult();
        Check(SameValues(expectedA, Draft(form)) && backend.ModelCalls == 2 && Find<ComboBox>(form, "translationCli").Items.Count == count,
          "same CLI and browsed case-equivalent path preserve all edits without discovery, adapter changes or duplicate rows");

        PickCli(form, "cursor");
        Check(Draft(form).ParallelRequests == 2 && Draft(form).MinimumChunkCharacters == 2700,
          "first visit to another CLI inherits the current threading choices");
        Field<CheckBox>(form, "translationUseManualModel").Checked = true;
        Find<TextBox>(form, "translationManualModel").Text = "manual-b";
        Field<CheckBox>(form, "translationCustomArguments").Checked = true;
        Find<TextBox>(form, "translationArguments").Text = "--fixture-b {input}";
        Find<ComboBox>(form, "translationOutput").SelectedItem = "json-result";
        Find<NumericUpDown>(form, "translationTimeout").Value = 52;
        Find<NumericUpDown>(form, "translationParallelRequests").Value = 5;
        Find<NumericUpDown>(form, "translationMinimumChunk").Value = 0;
        Find<CheckBox>(form, "translationShowButtons").Checked = true; expectedB = Draft(form);
        PickCli(form, "codex");
        Check(SameValues(expectedA, Draft(form)), "A-B-A restores every unsaved CLI value, including effort and threading");
        PickCli(form, "cursor");
        Check(SameValues(expectedB, Draft(form)) && Field<CheckBox>(form, "translationUseManualModel").Checked,
          "returning to B restores its manual-model mode, custom output and independent timing");
        PickCli(form, "codex");
        backend.Models().Complete(Catalog("alternate-c", "Alternate Alpha"));
        backend.Cache.LoadModelsAsync("codex", executableB, false, CancellationToken.None).GetAwaiter().GetResult();
        var alternate = new CliInstallation { ProviderId = "codex", Executable = executableB };
        var selection = (Task)typeof(SettingsForm).GetMethod("SelectCliAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { alternate });
        Pump(() => selection.IsCompleted); selection.GetAwaiter().GetResult();
        Field<CheckBox>(form, "translationUseManualModel").Checked = true;
        Find<TextBox>(form, "translationManualModel").Text = "manual-c";
        Field<CheckBox>(form, "translationCustomArguments").Checked = true;
        Find<TextBox>(form, "translationArguments").Text = "--fixture-c {input}";
        Find<NumericUpDown>(form, "translationTimeout").Value = 63;
        Find<NumericUpDown>(form, "translationMinimumChunk").Value = 1800; expectedC = Draft(form);
        PickCli(form, "codex");
        Check(SameValues(expectedA, Draft(form)), "a second executable of the same provider keeps independent model and timing settings");
        Find<Button>(form, "btnSave").PerformClick();
        Check(form.DialogResult == DialogResult.OK && form.TranslationOptions.CliConnections.Count == 3,
          "Save keeps all visited CLI profiles without duplicating the same path or its case alias");
        Check(original.Translation.CliConnections.Count == 0 && original.Translation.Model == "saved-a" && original.Translation.ReasoningEffort == "low"
          && original.Translation.TimeoutSeconds == 300 && original.Translation.ParallelRequests == 3,
          "visiting and saving CLI drafts does not mutate the supplied settings");
        saved = new Settings { EnabledMarkdownPlugins = new[] { "attrs" }, Translation = form.TranslationOptions.Copy() }; form.Close();
      }
      using (var form = new BackgroundSettings(saved, backend.Cache)) {
        form.SelectTranslationTab(); form.Show(); Heartbeats(); Pump(() => Idle(form));
        Check(SameValues(expectedA, Draft(form)), "reopening restores all saved active CLI values");
        PickCli(form, "codex", executableB);
        Check(SameValues(expectedC, Draft(form)), "reopening distinguishes provider-plus-path profiles even when another provider shares that path");
        PickCli(form, "cursor");
        Check(SameValues(expectedB, Draft(form)), "reopening retains the inactive saved CLI profile and manual-model flag");
        Find<NumericUpDown>(form, "translationParallelRequests").Value = 8;
        Find<TextBox>(form, "translationArguments").Text = "--canceled";
        Find<Button>(form, "btnCancel").PerformClick(); form.Close();
        var persisted = saved.Translation.Copy();
        saved.Translation.CliConnections.Single(profile => profile.ProviderId == "cursor").ApplyTo(persisted);
        Check(form.DialogResult == DialogResult.Cancel && SameValues(expectedB, persisted) && saved.Translation.CliConnections.Count == 3,
          "Cancel discards edits to inactive CLI drafts and preserves the saved profiles");
      }
      using (var form = new BackgroundSettings(saved, backend.Cache)) {
        form.SelectTranslationTab(); form.Show(); Heartbeats(); Pump(() => Idle(form));
        Find<Button>(form, "translationDefaults").PerformClick(); Pump(() => Idle(form));
        var reset = Draft(form);
        Check(reset.UseDefaultModel && !reset.UseManualModel && !reset.UseCustomArguments && reset.ReasoningEffort == ""
          && Find<TextBox>(form, "translationArguments").ReadOnly && reset.ParallelRequests == expectedA.ParallelRequests
          && reset.MinimumChunkCharacters == expectedA.MinimumChunkCharacters && reset.ShowButtons == expectedA.ShowButtons,
          "explicit CLI defaults reset model, effort and arguments while preserving threading and visibility");
        PickCli(form, "cursor");
        Check(SameValues(expectedB, Draft(form)), "resetting A defaults leaves B's saved settings intact"); form.Close();
      }
    }
  }

  private static void Disposal()
  {
    using (var backend = new Backend()) {
      var scan = backend.Scan(ignore: true); var models = backend.Models(ignore: true);
      var form = new BackgroundSettings(Saved(), backend.Cache); form.SelectTranslationTab(); form.Show();
      Pump(() => scan.Started.Task.IsCompleted && models.Started.Task.IsCompleted);
      Pump(() => Field<Task>(form, "modelDiscoveryTask") != null);
      var pending = Field<Task>(form, "modelDiscoveryTask"); form.Dispose();
      Pump(() => scan.Canceled.Task.IsCompleted && models.Canceled.Task.IsCompleted && pending.IsCompleted);
      scan.Complete(Installations()); models.Complete(Catalog()); Heartbeats();
      Check(form.IsDisposed && uiError == null, "Dispose cancels discovery and ignores results from loaders that finish late");
    }
  }

  private static void RelativeLauncher()
  {
    var previous = Environment.CurrentDirectory;
    try {
      var folder = Path.Combine(scratch, "relative", "nested"); Directory.CreateDirectory(folder); var path = Path.Combine(folder, "fixture.exe"); File.WriteAllBytes(path, new byte[] { 0 });
      Environment.CurrentDirectory = scratch;
      var options = CliProfiles.Defaults("custom", Path.Combine("relative", "nested", "fixture.exe"));
      var result = typeof(SettingsForm).GetMethod("ResolveCliSelection", BindingFlags.NonPublic | BindingFlags.Static)
        .Invoke(null, new object[] { new List<CliInstallation>(), options, CancellationToken.None });
      var selected = (CliInstallation)result.GetType().GetField("Selected").GetValue(result);
      Check(selected != null && selected.Executable == path && Path.IsPathRooted(selected.Executable), "existing relative subdirectory launcher migrates to a canonical absolute path");
    }
    finally { Environment.CurrentDirectory = previous; }
  }

  private static void Geometry()
  {
    using (var backend = new Backend()) {
      var settings = Saved(); settings.Translation.ConnectionMode = "api";
      using (var form = new BackgroundSettings(settings, backend.Cache)) {
        form.SelectTranslationTab(); form.Show(); form.Font = new System.Drawing.Font("Segoe UI", 13.5F);
        form.ClampToWorkingArea(new System.Drawing.Rectangle(form.Left, form.Top, 640, 480));
        var numeric = Find<NumericUpDown>(form, "translationParallelRequests"); Find<TabPage>(form, "translationPage").ScrollControlIntoView(numeric); Application.DoEvents();
        Check(VisibleFully(numeric) && VisibleFully(Find<Button>(form, "btnSave")) && VisibleFully(Find<Button>(form, "btnCancel")),
          "batched layout keeps the common count and footer usable at 640x480 with a large font");
        Check(backend.InstallationCalls == 0 && backend.ModelCalls == 0, "API geometry does not trigger CLI discovery"); form.Close();
      }
      backend.Seed();
      using (var form = new BackgroundSettings(Saved(), backend.Cache)) {
        form.SelectTranslationTab(); form.Show(); Heartbeats(); Pump(() => Idle(form));
        form.Font = new System.Drawing.Font("Segoe UI", 13.5F);
        form.ClampToWorkingArea(new System.Drawing.Rectangle(form.Left, form.Top, 640, 480));
        var page = Find<TabPage>(form, "translationPage");
        foreach (var name in new[] { "translationModel", "translationEffort", "translationParallelRequests", "translationMinimumChunk" }) {
          var control = form.Controls.Find(name, true).Single(); page.ScrollControlIntoView(control); Application.DoEvents();
          Check(VisibleFully(control) && (name != "translationModel" || control.Width >= control.Font.Height * 3),
            "compact CLI control remains usable and reachable at 640x480 with a large font: " + name);
        }
        var help = Find<Label>(form, "translationParallelHelp"); page.ScrollControlIntoView(help); Application.DoEvents();
        Check(VisibleFully(help), "the final threading help remains reachable after font and narrow-window reflow updates the scroll range");
        form.Close();
      }
    }
  }
  private static bool VisibleFully(Control control)
  {
    var full = control.RectangleToScreen(control.ClientRectangle); var clip = full;
    for (var parent = control.Parent; parent != null; parent = parent.Parent) clip = System.Drawing.Rectangle.Intersect(clip, parent.RectangleToScreen(parent.ClientRectangle));
    return control.Visible && full == clip;
  }
}
