using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using AnotherMarkdown.Translation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class ProtectedTranslationTests
{
  private const string Source = "Sentence A `keep_a()` and `keep_b()`.";
  private static int checks;
  private static int Main()
  {
    try { Run().GetAwaiter().GetResult(); Console.WriteLine("PASS protected translation: " + checks + " assertions"); return 0; }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
  }
  private static void Check(bool condition, string message)
  { if (!condition) throw new Exception("FAIL " + message); checks++; Console.WriteLine("PASS " + message); }
  private static TaskCompletionSource<T> Completion<T>() => new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
  private static async Task Within(Task task)
  { if (await Task.WhenAny(task, Task.Delay(5000)).ConfigureAwait(false) != task) throw new TimeoutException("Controlled fixture stage did not complete."); await task.ConfigureAwait(false); }
  private static async Task<Exception> Failure(Func<Task> action)
  { try { await action().ConfigureAwait(false); } catch (Exception error) { return error; } throw new Exception("Expected fixture failure."); }
  private static TranslationChunk Chunk(int index, string text, string separator = "") => new TranslationChunk(index, text, separator, "", "", "");
  private static async Task Run()
  {
    await RepairAndCap(); await AnnotationRepair(); await UnrelatedFailures(); await Cancellation(); await MixedBatch(); await ExhaustedBatch(); await LateCancellation();
    var old = new TranslationProgress(1, 2);
    Check(old.Completed == 1 && old.Total == 2 && old.RetryPart == 0 && old.RetryAttempt == 0 && old.RetryMaxAttempts == 0,
      "legacy progress construction retains normal completion semantics");
  }

  private static async Task RepairAndCap()
  {
    var targets = new List<string>(); var prompts = new List<string>(); var retries = new List<Tuple<int, int>>();
    var current = ""; var calls = 0;
    const string damaged = "FAILED_OUTPUT_MUST_NOT_APPEAR_IN_REPAIR_PROMPT";
    var result = await ProtectedTranslation.RunAsync(Source, target => { current = target; targets.Add(target); return "TARGET\n" + target; },
      (prompt, token) => { prompts.Add(prompt); return Task.FromResult(++calls == 1 ? damaged : current.Replace("Sentence A", "Translated A")); },
      CancellationToken.None, (attempt, maximum) => retries.Add(Tuple.Create(attempt, maximum)));
    Check(result == Source.Replace("Sentence A", "Translated A") && calls == 2, "malformed output retries only the damaged request and restores exact inline code");
    Check(targets.Count == 2 && targets[0] != targets[1], "a repair attempt gets fresh protected markers from the original source");
    Check(!prompts[1].Contains(damaged) && prompts[1].Contains("original target again") && !prompts[0].Contains("original target again"),
      "repair adds corrective instructions without copying failed output");
    Check(retries.Count == 1 && retries[0].Item1 == 2 && retries[0].Item2 == 3, "repair reports the next one-based attempt and the three-attempt limit");
    Check(prompts[1].Contains("Validation rule that failed") && prompts[1].Contains("защищённый фрагмент Markdown"), "repair identifies the failed validation rule without echoing model output");
    calls = 0; retries.Clear();
    var exhausted = await Failure(() => ProtectedTranslation.RunAsync(Source, target => target,
      (prompt, token) => { calls++; return Task.FromResult(damaged); }, CancellationToken.None, (attempt, maximum) => retries.Add(Tuple.Create(attempt, maximum))));
    Check(exhausted is MarkdownProtectionException && calls == 3 && retries.Select(item => item.Item1).SequenceEqual(new[] { 2, 3 }),
      "invalid protected output is capped at three total attempts");
    Check(!exhausted.ToString().Contains(damaged), "exhausted repair diagnostics never contain failed model output");
    Check(exhausted.Message.StartsWith("\u041f\u043e\u0441\u043b\u0435 3 \u043f\u043e\u043f\u044b\u0442\u043e\u043a", StringComparison.Ordinal)
      && exhausted.Message.Contains("\u041f\u0435\u0440\u0435\u0432\u043e\u0434 \u043d\u0435 \u0441\u043e\u0445\u0440\u0430\u043d\u0451\u043d"),
      "exhausted repair explains the three attempts and that no translation was saved");
    calls = 0; current = "";
    Check(await ProtectedTranslation.RunAsync("Ordinary prose.", target => { current = target; return target; }, (prompt, token) => {
      calls++; return Task.FromResult(current.Replace("Ordinary", "Translated"));
    }, CancellationToken.None) == "Translated prose." && calls == 1, "documents without protected fragments use one normal request");
  }

  private static async Task AnnotationRepair()
  {
    const string source = "```powershell\nGet-Date # English note\n```";
    var target = ""; var calls = 0; var retries = 0;
    var result = await ProtectedTranslation.RunAsync(source, text => { target = text; return CliTranslator.CreatePrompt(text); }, (prompt, token) => {
      if (++calls == 1) return Task.FromResult(target); // Lost sidecar response, although main code marker survived.
      var match = Regex.Match(prompt, @"(?ms)^ANNOTATIONS_(?<id>PROTECTED_CONTEXT_[a-f0-9]{32})\n(?<data>.*?)\nEND_ANNOTATIONS_\k<id>\n");
      var annotations = JArray.Parse(match.Groups["data"].Value);
      foreach (var annotation in annotations) annotation["text"] = ((string)annotation["text"]).Replace("English note", "Translated note");
      return Task.FromResult(new JObject { ["markdown"] = target, ["annotations"] = annotations }.ToString(Formatting.None));
    }, CancellationToken.None, (attempt, maximum) => retries++);
    Check(calls == 2 && retries == 1 && result == source.Replace("English note", "Translated note"), "a lost annotation envelope is repaired automatically while commands remain exact");
  }

  private static async Task UnrelatedFailures()
  {
    foreach (var expected in new Exception[] { new InvalidOperationException("General parser failure"), new HttpRequestException("HTTP 429"),
      new UnauthorizedAccessException("Authentication failed"), new TimeoutException("Request timed out"), new OperationCanceledException("Transport canceled") }) {
      var calls = 0; var retries = 0;
      var actual = await Failure(() => ProtectedTranslation.RunAsync(Source, target => target, (prompt, token) => {
        calls++; return Task.FromException<string>(expected);
      }, CancellationToken.None, (attempt, maximum) => retries++));
      Check(ReferenceEquals(actual, expected) && calls == 1 && retries == 0, "unrelated failure is propagated without retry: " + expected.GetType().Name);
    }
    var factoryCalls = 0; var transportCalls = 0;
    var factoryError = await Failure(() => ProtectedTranslation.RunAsync(Source, target => { factoryCalls++; throw new InvalidOperationException("Prompt factory failed"); },
      (prompt, token) => { transportCalls++; return Task.FromResult("x"); }, CancellationToken.None));
    Check(factoryError is InvalidOperationException && factoryCalls == 1 && transportCalls == 0, "prompt preparation errors never trigger transport retries");
  }

  private static async Task Cancellation()
  {
    using (var canceled = new CancellationTokenSource()) {
      canceled.Cancel(); var calls = 0;
      var error = await Failure(() => ProtectedTranslation.RunAsync(Source, target => { calls++; return target; }, (prompt, token) => Task.FromResult(prompt), canceled.Token));
      Check(error is OperationCanceledException && calls == 0, "pre-canceled translation performs no prompt preparation or request");
    }
    using (var canceled = new CancellationTokenSource()) {
      var entered = Completion<bool>(); var response = Completion<string>(); var calls = 0; var retries = 0;
      var pending = ProtectedTranslation.RunAsync(Source, target => target, async (prompt, token) => {
        calls++; entered.TrySetResult(true); using (token.Register(() => response.TrySetCanceled())) return await response.Task.ConfigureAwait(false);
      }, canceled.Token, (attempt, maximum) => retries++);
      await Within(entered.Task); canceled.Cancel(); var error = await Failure(() => pending);
      Check(error is OperationCanceledException && calls == 1 && retries == 0, "cancellation of an in-flight request does not enter repair");
    }
    using (var canceled = new CancellationTokenSource()) {
      var calls = 0; var retries = 0;
      var error = await Failure(() => ProtectedTranslation.RunAsync(Source, target => target, (prompt, token) => {
        calls++; canceled.Cancel(); return Task.FromResult("Missing protected markers");
      }, canceled.Token, (attempt, maximum) => retries++));
      Check(error is OperationCanceledException && calls == 1 && retries == 0, "cancellation wins over malformed output before a new attempt is announced");
    }
    using (var canceled = new CancellationTokenSource()) {
      var calls = 0; var retries = 0;
      var error = await Failure(() => ProtectedTranslation.RunAsync(Source, target => target, (prompt, token) => {
        calls++; return Task.FromResult("Missing protected markers");
      }, canceled.Token, (attempt, maximum) => { retries++; canceled.Cancel(); }));
      Check(error is OperationCanceledException && calls == 1 && retries == 1, "canceling from the retry notification prevents the second request");
    }
  }

  private sealed class RecordingProgress : IProgress<TranslationProgress>
  {
    private readonly object gate = new object(); private readonly List<TranslationProgress> values = new List<TranslationProgress>();
    internal Action<TranslationProgress> OnReport;
    public void Report(TranslationProgress value) { lock (gate) values.Add(value); OnReport?.Invoke(value); }
    internal TranslationProgress[] Values { get { lock (gate) return values.ToArray(); } }
  }
  private static async Task MixedBatch()
  {
    var chunks = new[] { Chunk(0, "Text A `code_a`.", "\n\n"), Chunk(1, "Text B `code_b`.", "\n\n"), Chunk(2, "Text C `code_c`.") };
    var calls = new int[3]; var progress = new RecordingProgress(); var oneDone = Completion<bool>(); var twoDone = Completion<bool>();
    var repairEntered = Completion<bool>(); var releaseRepair = Completion<bool>(); Action<int, int> lateA = null;
    progress.OnReport = value => { if (value.Completed >= 1) oneDone.TrySetResult(true); if (value.Completed >= 2) twoDone.TrySetResult(true); };
    var pending = TranslationBatch.RunAsync(chunks, 2, async (chunk, retry, token) => {
      if (chunk.Index == 0) lateA = retry;
      var target = "";
      return await ProtectedTranslation.RunAsync(chunk.Markdown, value => { target = value; return value; }, async (prompt, cancellation) => {
        var call = Interlocked.Increment(ref calls[chunk.Index]);
        if (chunk.Index == 1 && call == 1) { await oneDone.Task.ConfigureAwait(false); return "Invalid marker output"; }
        if (chunk.Index == 1) { repairEntered.TrySetResult(true); await releaseRepair.Task.ConfigureAwait(false); }
        return target.Replace("Text", "Translated");
      }, token, retry).ConfigureAwait(false);
    }, CancellationToken.None, progress);
    await Within(repairEntered.Task); await Within(twoDone.Task);
    var before = progress.Values.Length; lateA(3, 3);
    Check(progress.Values.Length == before && !pending.IsCompleted, "a late retry callback from an already successful part cannot alter an active batch");
    releaseRepair.TrySetResult(true); await Within(pending);
    Check(pending.Result == "Translated A `code_a`.\n\nTranslated B `code_b`.\n\nTranslated C `code_c`.", "a repaired batch retains source order and restores every protected part");
    Check(calls.SequenceEqual(new[] { 1, 2, 1 }), "successful sibling parts are retained and never requested again");
    var values = progress.Values; var repairs = values.Where(value => value.RetryPart != 0).ToArray();
    Check(repairs.Length == 1 && repairs[0].RetryPart == 2 && repairs[0].RetryAttempt == 2 && repairs[0].RetryMaxAttempts == 3 && repairs[0].Completed >= 1,
      "batch retry progress identifies the damaged part and retains already completed work");
    Check(values.Select(value => value.Completed).SequenceEqual(values.Select(value => value.Completed).OrderBy(value => value))
      && values.Where(value => value.RetryPart == 0).Select(value => value.Completed).SequenceEqual(new[] { 0, 1, 2, 3 }),
      "completion progress is monotonic and increments once for each successful part");
    before = progress.Values.Length; lateA(3, 3);
    Check(progress.Values.Length == before, "retry callbacks after completed batch disposal produce no progress");
  }

  private static async Task ExhaustedBatch()
  {
    var peerEntered = Completion<bool>(); var peerCanceled = Completion<bool>(); var cleanup = Completion<bool>(); var calls = 0;
    var pending = TranslationBatch.RunAsync(new[] { Chunk(0, "Failing `code`"), Chunk(1, "Peer `code`") }, 2, async (chunk, retry, token) => {
      if (chunk.Index == 0) {
        await peerEntered.Task.ConfigureAwait(false);
        return await ProtectedTranslation.RunAsync(chunk.Markdown, target => target, (prompt, cancellation) => {
          Interlocked.Increment(ref calls); return Task.FromResult("Invalid protected output");
        }, token, retry).ConfigureAwait(false);
      }
      using (token.Register(() => peerCanceled.TrySetResult(true))) {
        peerEntered.TrySetResult(true); await cleanup.Task.ConfigureAwait(false); token.ThrowIfCancellationRequested(); return "Peer result";
      }
    }, CancellationToken.None);
    await Within(peerCanceled.Task);
    Check(calls == 3 && !pending.IsCompleted, "exhausted repair cancels its peer and waits for peer cleanup");
    cleanup.TrySetResult(true); var error = await Failure(() => pending);
    Check(error is MarkdownProtectionException, "exhausted batch returns its validation failure instead of a partial document");
  }

  private static async Task LateCancellation()
  {
    using (var canceled = new CancellationTokenSource()) {
      var entered = Completion<bool>(); var finish = Completion<bool>(); var progress = new RecordingProgress(); Action<int, int> late = null;
      var pending = TranslationBatch.RunAsync(new[] { Chunk(0, "A `code`") }, 1, async (chunk, retry, token) => {
        late = retry; entered.TrySetResult(true); await finish.Task.ConfigureAwait(false); return "Late result";
      }, canceled.Token, progress);
      await Within(entered.Task); canceled.Cancel(); var before = progress.Values.Length; late(2, 3);
      Check(progress.Values.Length == before, "canceled batches suppress retained retry callbacks immediately");
      finish.TrySetResult(true); var error = await Failure(() => pending);
      Check(error is OperationCanceledException && progress.Values.Last().Completed == 0, "a late successful response cannot count or publish work after cancellation");
      late(3, 3); Check(progress.Values.Length == before, "disposed canceled batches ignore retained callbacks safely");
    }
  }
}
