using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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
          PreviewIsLazy(); ConcurrentAndResponsive(); DiscoveryGeometry(); WarmCacheAndLateEdits(); SelectionAndCancellation(); CliProfilesSettings(); Disposal(); RelativeLauncher(); Geometry();
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
    private readonly List<IDisposable> owned = new List<IDisposable>();
    public int InstallationCalls, ModelCalls;
    private DateTime now = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public readonly SettingsDiscoveryCache Cache;
    public Backend()
    {
      Cache = new SettingsDiscoveryCache(token => {
        Gate<List<CliInstallation>> next; lock (gate) { InstallationCalls++; next = scans.Dequeue(); } return next.Load(token);
      }, (provider, executable, token) => {
        Gate<CliModelCatalog> next; lock (gate) { ModelCalls++; next = models[provider].Dequeue(); } return next.Load(token);
      }, () => now, TimeSpan.FromMinutes(5));
    }
    public Gate<List<CliInstallation>> Scan(bool block = false, bool ignore = false)
    { var value = new Gate<List<CliInstallation>>(block, ignore); lock (gate) { scans.Enqueue(value); owned.Add(value); } return value; }
    public Gate<CliModelCatalog> Models(string provider = "codex", bool block = false, bool ignore = false)
    {
      var value = new Gate<CliModelCatalog>(block, ignore);
      lock (gate) { if (!models.ContainsKey(provider)) models[provider] = new Queue<Gate<CliModelCatalog>>(); models[provider].Enqueue(value); owned.Add(value); }
      return value;
    }
    public void Seed(CliModelCatalog catalog = null)
    {
      Scan().Complete(Installations()); Models().Complete(catalog ?? Catalog());
      Cache.LoadInstallationsAsync(false, CancellationToken.None).GetAwaiter().GetResult();
      Cache.LoadModelsAsync("codex", executableA, false, CancellationToken.None).GetAwaiter().GetResult();
    }
    public void Expire() { now += TimeSpan.FromMinutes(6); }
    public void Dispose() { foreach (var value in owned) value.Dispose(); }
  }

  private static void PreviewIsLazy()
  {
    using (var backend = new Backend())
    using (var form = new BackgroundSettings(Saved(), backend.Cache)) {
      Check(backend.InstallationCalls == 0 && backend.ModelCalls == 0, "constructor does not start discovery");
      form.Show(); Heartbeats();
      Check(form.Paints > 0 && backend.InstallationCalls == 0 && backend.ModelCalls == 0, "preview-only opening paints and pumps UI without discovery (paint/scan/model=" + form.Paints + "/" + backend.InstallationCalls + "/" + backend.ModelCalls + ")");
      form.Close();
    }
  }

  private static void ConcurrentAndResponsive()
  {
    using (var backend = new Backend()) {
      var scan = backend.Scan(true); var models = backend.Models(block: true);
      using (var form = new BackgroundSettings(Saved(), backend.Cache)) {
        form.SelectTranslationTab();
        Check(backend.InstallationCalls == 0 && backend.ModelCalls == 0, "selecting Translation before Show does not discover");
        var shownObserved = false; var shownWithoutDiscovery = false;
        form.Shown += (_, __) => { shownWithoutDiscovery = backend.InstallationCalls == 0 && backend.ModelCalls == 0; shownObserved = true; };
        form.Show();
        Pump(() => shownObserved);
        Check(shownWithoutDiscovery, "Shown defers discovery until its event handlers return");
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
      "translationModelRow", "translationModel", "translationEffort", "translationModelStatus", "translationDefaults", "translationShowButtons", "btnSave", "btnCancel",
      "apiConnections", "apiModelRow", "apiModel", "apiEffort", "apiActions", "apiStatus", "apiTimeout", "apiShowButtons" }) {
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
        Check(Find<ComboBox>(form, "translationEffort").Enabled && Draft(form).ReasoningEffort == "high",
          "delayed catalog makes the saved effort available without delaying the dialog");
        GeometryUnchanged(shown, form, "supported-effort catalog does not hide rows or move interactive controls");
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
      using (var form = new BackgroundSettings(settings, backend.Cache)) {
        Dictionary<string, System.Drawing.Rectangle> firstPaint = null, shown = null;
        form.FirstPaint = () => firstPaint = VisibleGeometry(form); form.Shown += (_, __) => shown = VisibleGeometry(form);
        form.SelectTranslationTab(); form.Show(); Pump(() => firstPaint != null && shown != null); Heartbeats();
        GeometryUnchanged(firstPaint, form, "API controls stay in their first native paint positions after Shown");
        var message = string.Concat(Enumerable.Repeat("Synthetic API status details. ", 20));
        // Exercise a delayed status update on the same UI dispatcher as API results, without network or credentials.
        form.BeginInvoke(new Action(() => Find<Label>(form, "apiStatus").Text = message));
        Pump(() => Find<Label>(form, "apiStatus").Text == message); Heartbeats();
        GeometryUnchanged(shown, form, "long asynchronous API status does not recompose the shown fields or footer");
        Check(backend.InstallationCalls == 0 && backend.ModelCalls == 0 && Find<Button>(form, "btnSave").Enabled,
          "stable API opening remains responsive and performs no CLI discovery"); form.Close();
      }
    }
  }

  private static void WarmCacheAndLateEdits()
  {
    using (var backend = new Backend()) {
      backend.Seed();
      using (var form = new BackgroundSettings(Saved(), backend.Cache)) {
        Check(Has(form, "Alpha") && Draft(form).Model == "saved-a" && Draft(form).ReasoningEffort == "low",
          "warm-cache constructor initializes parallel controls before restoring cached model and effort");
        form.SelectTranslationTab(); form.Show(); Heartbeats(); Pump(() => Idle(form));
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
        Check(Has(form, "Fresh Alpha"), "expired catalog is shown immediately while a background refresh is pending");
        form.SelectTranslationTab(); form.Show(); Pump(() => staleScan.Started.Task.IsCompleted && staleModels.Started.Task.IsCompleted);
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
        var oldTask = Field<Task>(form, "modelDiscoveryTask");
        var picker = Find<ComboBox>(form, "translationCli"); picker.SelectedItem = picker.Items.Cast<CliInstallation>().Single(value => value.ProviderId == "cursor");
        Pump(() => oldScan.Canceled.Task.IsCompleted && oldModels.Canceled.Task.IsCompleted && newModels.Started.Task.IsCompleted && oldTask.IsCompleted);
        Check(true, "changing CLI cancels both old scan and old catalog subscriptions");
        newModels.Complete(Catalog("new-b", "Fresh Beta")); Pump(() => Idle(form), "new CLI catalog");
        oldModels.Complete(Catalog(name: "STALE Alpha")); oldScan.Complete(new List<CliInstallation> { Installations()[0] }); Heartbeats();
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
