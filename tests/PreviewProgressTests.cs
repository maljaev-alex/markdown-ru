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
    var tempParent = Directory.Exists(@"D:\Temp") ? @"D:\Temp\agent\markdown-ru" : Path.Combine(Path.GetTempPath(), "markdown-ru");
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

  private static void Check(bool condition, string label)
  {
    if (!condition) throw new Exception("FAIL " + label);
    checks++;
    Console.WriteLine("PASS " + label);
  }

  private static T Field<T>(MarkdownPreviewForm form, string name) =>
    (T)typeof(MarkdownPreviewForm).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);

  private static void DeferredProgress(bool changeSource)
  {
    var releaseRenderer = new TaskCompletionSource<bool>();
    var rendererEntered = false;
    var renderedTranslation = "";
    var settings = new Settings { Translation = new TranslationOptions {
      Executable = Assembly.GetExecutingAssembly().Location, ProviderId = "custom", Arguments = "fake-translation",
      UseCustomArguments = true, UseDefaultModel = false, Model = "fixture-model", OutputFormat = "text",
      TimeoutSeconds = 10, ParallelRequests = 2, ShowButtons = true
    } };
    Func<string, string, bool, Task> renderer = (text, path, translated) => {
      if (!translated) return Task.CompletedTask;
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
        context.PumpUntil(() => translation.IsCompleted && (sourceUpdate == null || sourceUpdate.IsCompleted));
        translation.GetAwaiter().GetResult();
        if (sourceUpdate != null) sourceUpdate.GetAwaiter().GetResult();
        Check(Field<CancellationTokenSource>(preview, "translationCancellation") == null,
          "completed preview releases its owned translation request");
        Check(status.Text == terminalStatus, "renderer completion retains the terminal status after delayed progress delivery");
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
    public int ProgressPosted { get; private set; }
    public int ProgressDelivered { get; private set; }
    public override SynchronizationContext CreateCopy() => this;

    public override void Post(SendOrPostCallback callback, object state)
    {
      lock (gate) {
        if (state is TranslationProgress) { progress.Enqueue(() => callback(state)); ProgressPosted++; }
        else continuations.Enqueue(() => callback(state));
      }
    }

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
