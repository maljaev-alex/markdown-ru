using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AnotherMarkdown.Entities;
using AnotherMarkdown.Forms;
using AnotherMarkdown.Translation;

internal static class PreviewProgressTests
{
  private const string Source = "# Local progress fixture\n\nTranslation sample.\n";
  private const string Result = "# \u041f\u0435\u0440\u0435\u0432\u043e\u0434\n\n\u041f\u0440\u0438\u043c\u0435\u0440.\n";
  private const string RussianStatus = "\u0420\u0443\u0441\u0441\u043a\u0438\u0439 \u00b7 fixture-model";
  private static int checks;

  [STAThread]
  private static int Main(string[] args)
  {
    Console.InputEncoding = new UTF8Encoding(false);
    Console.OutputEncoding = new UTF8Encoding(false);
    if (args.Length == 1 && args[0] == "fake-translation") {
      var prompt = Console.In.ReadToEnd();
      if (!prompt.Contains(Source.TrimEnd())) return 2;
      Console.Write(Result);
      return 0;
    }
    if (args.Length == 2 && args[0] == "fake-retry-translation") {
      var prompt = Console.In.ReadToEnd(); var counter = args[1];
      if (!File.Exists(counter)) { File.WriteAllText(counter, "1"); Console.Write("Missing protected marker"); return 0; }
      File.WriteAllText(counter + ".started", "2");
      var deadline = Stopwatch.StartNew();
      while (!File.Exists(counter + ".release")) { if (deadline.Elapsed > TimeSpan.FromSeconds(5)) return 3; Thread.Sleep(5); }
      var marker = System.Text.RegularExpressions.Regex.Match(prompt, @"AM_KEEP_[a-f0-9]+_[0-9]+_END").Value;
      if (marker.Length == 0) return 4;
      Console.Write("# Fixture\n\nTranslated " + marker + ".\n"); return 0;
    }
    var tempParent = Directory.Exists(@"D:\Temp") ? @"D:\Temp\agent\markdown-ru\ru16-retry" : Path.Combine(Path.GetTempPath(), "markdown-ru");
    var directory = Path.GetFullPath(Path.Combine(tempParent, "preview-progress-" + Guid.NewGuid().ToString("N")));
    var previousTemp = Environment.GetEnvironmentVariable("TEMP");
    var previousTmp = Environment.GetEnvironmentVariable("TMP");
    Directory.CreateDirectory(directory);
    // CliTranslator uses Path.GetTempPath(); redirect only this fixture process.
    Environment.SetEnvironmentVariable("TEMP", directory);
    Environment.SetEnvironmentVariable("TMP", directory);
    try {
      Application.EnableVisualStyles();
      DeferredProgress(false);
      DeferredProgress(true);
      DeferredProgress(false, true);
      RetryProgress(directory);
      Console.WriteLine("PASS preview progress: " + checks + " assertions");
      return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    finally {
      Environment.SetEnvironmentVariable("TEMP", previousTemp);
      Environment.SetEnvironmentVariable("TMP", previousTmp);
      var parent = Path.GetFullPath(tempParent) + Path.DirectorySeparatorChar;
      if (directory.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) Directory.Delete(directory, true);
    }
  }

  private static void RetryProgress(string directory)
  {
    var counter = Path.Combine(directory, "retry-stage"); var releaseRenderer = new TaskCompletionSource<bool>(); var rendererEntered = false;
    var settings = new Settings { Translation = new TranslationOptions {
      Executable = Assembly.GetExecutingAssembly().Location, ProviderId = "custom", Arguments = "fake-retry-translation " + CliTranslator.QuoteArgument(counter),
      UseCustomArguments = true, UseDefaultModel = false, Model = "fixture-model", OutputFormat = "text", TimeoutSeconds = 10, ParallelRequests = 2, ShowButtons = true
    } };
    Func<string, string, bool, Task> renderer = (text, path, translated) => {
      if (!translated) return Task.CompletedTask;
      Check(text == "# Fixture\n\nTranslated `keep()`.", "repaired local CLI output restores the protected code before rendering");
      rendererEntered = true; return releaseRenderer.Task;
    };
    var constructor = typeof(MarkdownPreviewForm).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
      null, new[] { typeof(Settings), typeof(Func<string, string, bool, Task>) }, null);
    using (var preview = (MarkdownPreviewForm)constructor.Invoke(new object[] { settings, renderer })) {
      var previousContext = SynchronizationContext.Current; var context = new DeferredContext(); Task translation = null;
      SynchronizationContext.SetSynchronizationContext(context);
      try {
        preview.RenderMarkdown("# Retry fixture\n\nEnglish `keep()`.\n", "retry-fixture.md").GetAwaiter().GetResult();
        translation = (Task)typeof(MarkdownPreviewForm).GetMethod("TranslateCurrentAsync", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(preview, null);
        context.PumpUntil(() => File.Exists(counter + ".started") || translation.IsCompleted);
        Check(!translation.IsCompleted && File.ReadAllText(counter + ".started") == "2", "a real local retry request remains pending at its controlled second attempt");
        context.DeliverProgress(); var status = Field<ToolStripLabel>(preview, "translationStatus");
        Check(status.Text.Contains("\u041f\u043e\u0432\u0442\u043e\u0440 \u0447\u0430\u0441\u0442\u0438 1")
          && status.Text.Contains("\u043f\u043e\u043f\u044b\u0442\u043a\u0430 2 \u0438\u0437 3"), "preview identifies the damaged part and its active repair attempt");
        Check(Field<ToolStripLabel>(preview, "translationIndicator").Available && Field<System.Windows.Forms.Timer>(preview, "translationAnimation").Enabled
          && Field<ToolStripButton>(preview, "cancelButton").Available, "retry keeps the spinner and cancellation action active");
        File.WriteAllText(counter + ".release", "continue"); context.PumpUntil(() => rendererEntered || translation.IsCompleted);
        Check(rendererEntered && status.Text == RussianStatus && !translation.IsCompleted, "successful repair publishes terminal status while rendering is still deferred");
        context.ReplayRetry(); context.DeliverProgress();
        Check(status.Text == RussianStatus, "a delayed real retry notification cannot overwrite terminal success");
        releaseRenderer.SetResult(true); context.PumpUntil(() => translation.IsCompleted); translation.GetAwaiter().GetResult();
        Check(Field<CancellationTokenSource>(preview, "translationCancellation") == null && !Field<System.Windows.Forms.Timer>(preview, "translationAnimation").Enabled,
          "completed repair releases the request and stops the spinner");
      }
      finally {
        File.WriteAllText(counter + ".release", "cleanup"); releaseRenderer.TrySetResult(true); preview.Dispose();
        if (translation != null) context.PumpUntil(() => translation.IsCompleted); SynchronizationContext.SetSynchronizationContext(previousContext);
      }
    }
  }

  private static void Check(bool condition, string label)
  {
    if (!condition) throw new Exception("FAIL " + label);
    checks++;
    Console.WriteLine("PASS " + label);
  }

  private static T Field<T>(MarkdownPreviewForm form, string name) =>
    (T)typeof(MarkdownPreviewForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);

  private static void DeferredProgress(bool changeSource, bool cancelDuringRender = false)
  {
    var releaseRenderer = new TaskCompletionSource<bool>();
    var rendererEntered = false;
    var cancelledOriginalRendered = false;
    var cancelRequested = false;
    var renderedTranslation = "";
    var settings = new Settings { Translation = new TranslationOptions {
      Executable = Assembly.GetExecutingAssembly().Location, ProviderId = "custom", Arguments = "fake-translation",
      UseCustomArguments = true, UseDefaultModel = false, Model = "fixture-model", OutputFormat = "text",
      TimeoutSeconds = 10, ParallelRequests = 2, ShowButtons = true
    } };
    Func<string, string, bool, Task> renderer = (text, path, translated) => {
      if (!translated) { if (cancelRequested) cancelledOriginalRendered = true; return Task.CompletedTask; }
      renderedTranslation = text;
      rendererEntered = true;
      return releaseRenderer.Task;
    };
    var constructor = typeof(MarkdownPreviewForm).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
      null, new[] { typeof(Settings), typeof(Func<string, string, bool, Task>) }, null);
    using (var preview = (MarkdownPreviewForm)constructor.Invoke(new object[] { settings, renderer })) {
      var previousContext = SynchronizationContext.Current;
      var context = new DeferredContext();
      Task translation = null;
      Task sourceUpdate = null;
      SynchronizationContext.SetSynchronizationContext(context);
      try {
        preview.RenderMarkdown(Source, "progress-fixture.md").GetAwaiter().GetResult();
        translation = (Task)typeof(MarkdownPreviewForm).GetMethod("TranslateCurrentAsync", BindingFlags.NonPublic | BindingFlags.Instance)
          .Invoke(preview, null);
        context.PumpUntil(() => rendererEntered || translation.IsCompleted);
        Check(rendererEntered && renderedTranslation == Result.TrimEnd('\r', '\n'), "real local CLI translation enters the deferred preview renderer");
        Check(context.ProgressPosted >= 2 && context.ProgressDelivered == 0,
          "real start and completion progress callbacks remain queued without a cache shortcut");
        Check(!translation.IsCompleted && Field<CancellationTokenSource>(preview, "translationCancellation") != null,
          "terminal status is tested while renderer awaits and the request is still owned");
        var status = Field<ToolStripLabel>(preview, "translationStatus");
        Check(preview.IsTranslationPreview && status.Text == RussianStatus, "successful translation publishes terminal Russian status before rendering completes");
        if (cancelDuringRender) {
          cancelRequested = true;
          Field<ToolStripButton>(preview, "cancelButton").PerformClick();
          Check(!preview.IsTranslationPreview && status.Text == "Перевод отменён",
            "cancel during deferred rendering immediately restores original preview state");
        }
        if (changeSource) {
          sourceUpdate = preview.RenderMarkdown("# Replacement source\n", "replacement.md");
          Check(!preview.IsTranslationPreview && status.Text == "" && !sourceUpdate.IsCompleted,
            "source replacement clears the old status while waiting for the current renderer");
        }
        var terminalStatus = status.Text;
        context.DeliverProgress();
        Check(context.ProgressDelivered >= 2, "deferred real progress callbacks are actually delivered in the critical window");
        Check(status.Text == terminalStatus, changeSource
          ? "late progress from the previous source cannot restore an obsolete translating status"
          : "late progress cannot replace terminal Russian status while rendering is pending");
        releaseRenderer.SetResult(true);
        context.PumpUntil(() => translation.IsCompleted && (sourceUpdate == null || sourceUpdate.IsCompleted) && (!cancelDuringRender || cancelledOriginalRendered));
        translation.GetAwaiter().GetResult();
        if (sourceUpdate != null) sourceUpdate.GetAwaiter().GetResult();
        Check(Field<CancellationTokenSource>(preview, "translationCancellation") == null,
          "completed preview releases its owned translation request");
        Check(status.Text == terminalStatus, "renderer completion retains the terminal status after delayed progress delivery");
        if (cancelDuringRender) Check(cancelledOriginalRendered && !preview.IsTranslationPreview,
          "queued render after cancellation displays the original document rather than translation");
      }
      finally {
        releaseRenderer.TrySetResult(true);
        preview.Dispose();
        if (translation != null) context.PumpUntil(() => translation.IsCompleted && (sourceUpdate == null || sourceUpdate.IsCompleted));
        SynchronizationContext.SetSynchronizationContext(previousContext);
      }
    }
  }

  // Defer data notifications independently of await continuations. No timer,
  // UI message pump, sleeps between stages, or delegate-name heuristics decide order.
  private sealed class DeferredContext : SynchronizationContext
  {
    private readonly object gate = new object();
    private readonly Queue<Action> continuations = new Queue<Action>();
    private readonly Queue<Action> progress = new Queue<Action>();
    private Action retry;
    public int ProgressPosted { get; private set; }
    public int ProgressDelivered { get; private set; }
    public override SynchronizationContext CreateCopy() => this;

    public override void Post(SendOrPostCallback callback, object state)
    {
      lock (gate) {
        if (state is TranslationProgress value) {
          Action deliver = () => callback(state); progress.Enqueue(deliver); ProgressPosted++;
          if (value.RetryPart != 0) retry = deliver;
        }
        else continuations.Enqueue(() => callback(state));
      }
    }

    public void ReplayRetry()
    { lock (gate) { if (retry == null) throw new Exception("No real retry callback was captured."); progress.Enqueue(retry); } }

    public void PumpUntil(Func<bool> complete)
    {
      var timer = Stopwatch.StartNew();
      while (!complete()) {
        Action next;
        lock (gate) next = continuations.Count > 0 ? continuations.Dequeue() : null;
        if (next != null) next();
        else Thread.Sleep(1);
        if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Local preview fixture did not reach its deterministic stage.");
      }
    }

    public void DeliverProgress()
    {
      while (true) {
        Action next;
        lock (gate) next = progress.Count > 0 ? progress.Dequeue() : null;
        if (next == null) return;
        next(); ProgressDelivered++;
      }
    }
  }
}
