using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AnotherMarkdown.Translation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class ParallelTranslationTests
{
  private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
  private static readonly string Executable = Process.GetCurrentProcess().MainModule.FileName;
  private static int checks;
  private static string scratch, scratchRoot;

  private static int Main(string[] args)
  {
    Console.InputEncoding = Utf8; Console.OutputEncoding = Utf8;
    if (args.Length != 0 && args[0] == "app-server") return FakeServer();
    if (args.Length != 0 && (args[0] == "fake-echo" || args[0] == "exec")) return FakeEcho(args);
    scratchRoot = Path.GetFullPath(Directory.Exists(@"D:\Temp") ? @"D:\Temp\agent\markdown-ru" : Path.Combine(Path.GetTempPath(), "AnotherMarkdown-tests"));
    scratch = Path.Combine(scratchRoot, "parallel-tests-" + Guid.NewGuid().ToString("N"));
    var oldTemp = Environment.GetEnvironmentVariable("TEMP");
    var oldTmp = Environment.GetEnvironmentVariable("TMP");
    var oldLog = Environment.GetEnvironmentVariable("PARALLEL_TRANSLATION_FIXTURE_LOG");
    try {
      Directory.CreateDirectory(scratch); Environment.SetEnvironmentVariable("TEMP", scratch); Environment.SetEnvironmentVariable("TMP", scratch);
      Run().GetAwaiter().GetResult(); Console.WriteLine("PASS parallel translation: " + checks + " assertions"); return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    finally {
      Environment.SetEnvironmentVariable("TEMP", oldTemp); Environment.SetEnvironmentVariable("TMP", oldTmp);
      Environment.SetEnvironmentVariable("PARALLEL_TRANSLATION_FIXTURE_LOG", oldLog);
      var resolved = Path.GetFullPath(scratch);
      if (Directory.Exists(resolved) && resolved.StartsWith(scratchRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Directory.Delete(resolved, true);
    }
  }

  private static async Task Run()
  {
    Reconstruction(); SectionGranularity(); MinimumChunkSize(); AtomicBlocks(); Context(); CacheKeys();
    await Validation(); await OrderedWorkers(); await Cancellation(); await PrimaryFailure(); await EmptyAndPartial();
    await CliIntegration(false); await CliIntegration(true); await ApiIntegration(); await ProtectedCodeIntegration();
  }

  private static void Check(bool condition, string label)
  { if (!condition) throw new Exception("FAIL: " + label); checks++; Console.WriteLine("PASS " + label); }
  private static async Task<Exception> Failure(Func<Task> run)
  { try { await run(); } catch (Exception error) { return error; } throw new Exception("Expected an operation failure."); }
  private static async Task<T> Within<T>(Task<T> task, int milliseconds = 10000)
  { if (await Task.WhenAny(task, Task.Delay(milliseconds)) != task) throw new TimeoutException("Local parallel fixture did not finish."); return await task; }
  private static string Reconstruct(IReadOnlyList<TranslationChunk> chunks) => string.Concat(chunks.Select(c => c.PrefixBefore + c.Markdown + c.SeparatorAfter));
  private static string Fill(int size) => new string('x', size);
  private static string Paragraphs(string label, int count = 16, int size = 1100) => string.Concat(Enumerable.Range(0, count).Select(i => label + i + " " + Fill(size) + "\n\n"));
  private static string Source(string newline = "\n")
  {
    var text = "# Shared SOURCE_glossary\n\n[fixture-link]: https://fixture.invalid/unchanged\n\n";
    for (var i = 0; i < 26; i++) text += "BLOCK_" + i.ToString("D2") + " SOURCE_term \u041f\u0440\u0438\u0432\u0435\u0442 \ud83c\udf0d \"quote\" C:\\fixture\\path " + Fill(1540) + "\n\n";
    return (text + "SOURCE_final tail without newline").Replace("\n", newline);
  }

  private static void Reconstruction()
  {
    foreach (var newline in new[] { "\n", "\r\n", "\r" })
      foreach (var prefix in new[] { "", "\uFEFF", " \t" + newline + newline, "\uFEFF" + newline })
        foreach (var tail in new[] { "", newline, newline + " \t" + newline }) {
          var source = prefix + Source(newline) + tail; var plan = MarkdownTranslationPlan.Create(source, 4);
          Check(Reconstruct(plan) == source && plan.Select(c => c.Index).SequenceEqual(Enumerable.Range(0, plan.Count)), "exact Markdown reconstruction with native line endings BOM prefix and tail");
        }
    var whitespace = string.Concat(Enumerable.Repeat(" \t\r\n", 3500)) + Source("\r\n");
    var prefixed = MarkdownTranslationPlan.Create(whitespace, 4);
    Check(Reconstruct(prefixed) == whitespace && prefixed.All(c => !string.IsNullOrWhiteSpace(c.Markdown.TrimStart('\uFEFF'))), "a giant whitespace prefix never becomes an empty translation target");
    Check(MarkdownTranslationPlan.Create(Source(), 1).Count == 1, "one request remains the default for a long document");
    Check(MarkdownTranslationPlan.Create("# short\n\nbody\n", 8).Count == 1, "short Markdown uses one request even at maximum concurrency");
    var giant = "# Heading\n\n" + Fill(50000);
    Check(MarkdownTranslationPlan.Create(giant, 8).Count == 1 && Reconstruct(MarkdownTranslationPlan.Create(giant, 8)) == giant, "one giant indivisible paragraph stays one request");
    Check(MarkdownTranslationPlan.Create("literal unmatched ` tick\n\n" + Source(), 4).Count > 1, "a literal unmatched backtick does not disable later safe paragraph splitting");
  }

  private static void Atomic(string label, string atom, bool atStart = false, bool unclosed = false)
  {
    var source = (atStart ? "" : Paragraphs("before", 16)) + atom + (unclosed ? "" : "\n\n" + Paragraphs("after", 20));
    var plan = MarkdownTranslationPlan.Create(source, 4);
    Check(Reconstruct(plan) == source && plan.Any(c => c.Markdown.Contains(atom)), label + " stays whole across target-sized internal blank lines");
  }

  private static void SectionGranularity()
  {
    var sections = "---\nname: fixture\ndescription: SOURCE_description\n---\n\n# Reviewer\n\nSOURCE_introduction\n\n";
    for (var i = 0; i < 8; i++) sections += "## Section " + i + "\n\nSOURCE_section " + Fill(540) + "\n\n";
    Check(sections.Length < 8000, "compact section regression is below the former whole-document threshold");
    foreach (var workers in new[] { 2, 4, 8 }) {
      var plan = MarkdownTranslationPlan.Create(sections, workers);
      Check(plan.Count == workers && Reconstruct(plan) == sections, "compact sections use all requested workers: " + workers);
      Check(plan.Skip(1).All(c => c.Markdown.StartsWith("## Section ", StringComparison.Ordinal)), "compact sections are grouped at headings rather than cut inside their prose: " + workers);
      Check(plan[0].Markdown.StartsWith("---", StringComparison.Ordinal) && plan[0].Markdown.Contains("# Reviewer"), "front matter stays with the opening document section: " + workers);
    }
    var paragraphs = string.Concat(Enumerable.Range(0, 8).Select(i => "SOURCE_paragraph " + i + " " + Fill(120) + "\n\n"));
    Check(MarkdownTranslationPlan.Create(paragraphs, 8).Count == 8, "eight independent short paragraphs can use eight requests");
    Check(MarkdownTranslationPlan.Create("SOURCE_first\n\nSOURCE_second", 8).Count == 2, "fewer independent blocks produce only useful requests without empty padding");
    Check(MarkdownTranslationPlan.Create("\uFEFF# Title\n\nSOURCE_body\n", 8).Count == 1, "a BOM-prefixed heading remains attached to its single body");
    var adjacent = string.Concat(Enumerable.Range(0, 8).Select(i => "## Section " + i + "\nSOURCE_body " + Fill(150) + "\n"));
    var adjacentPlan = MarkdownTranslationPlan.Create(adjacent, 8);
    Check(adjacentPlan.Count == 8 && Reconstruct(adjacentPlan) == adjacent && adjacentPlan.All(c => c.Markdown.StartsWith("## Section", StringComparison.Ordinal)),
      "top-level ATX sections split without blank lines between them");
    var nested = "- SOURCE_item\n  ## Nested heading\n  SOURCE_nested " + Fill(600) + "\n  ## Still nested\n  SOURCE_tail\n";
    var mixed = nested + "## Root section\nSOURCE_root\n";
    var nestedPlan = MarkdownTranslationPlan.Create(mixed, 8);
    Check(nestedPlan.Count == 2 && nestedPlan[0].Markdown.Contains(nested.TrimEnd('\n')) && Reconstruct(nestedPlan) == mixed,
      "list-contained headings remain atomic while an unindented heading ends the list");
    var nestedLevels = "- SOURCE_parent\n  - SOURCE_child\n  ## Heading in parent item\n  SOURCE_parent_tail\n";
    var nestedLevelsPlan = MarkdownTranslationPlan.Create(nestedLevels + "## Outside\nSOURCE_body", 8);
    Check(nestedLevelsPlan.Count == 2 && nestedLevelsPlan[0].Markdown.Contains(nestedLevels.TrimEnd('\n')),
      "dedenting from a nested list keeps a heading inside its parent list item");
    var quotedFence = "> ```markdown\n> # SOURCE_not_a_section\n>\n> ## SOURCE_also_code\n> ```";
    var protectedSource = "# Intro\n\nSOURCE_intro\n\n" + quotedFence + "\n\n## End\n\nSOURCE_end";
    var protectedPlan = MarkdownTranslationPlan.Create(protectedSource, 8);
    Check(protectedPlan.Any(c => c.Markdown.Contains(quotedFence)) && Reconstruct(protectedPlan) == protectedSource,
      "small-document splitting still protects headings inside a quoted fence");
  }

  private static void MinimumChunkSize()
  {
    Check(new TranslationOptions().MinimumChunkCharacters == 2000, "the user-selected default minimum chunk size is 2000 characters");
    var document = string.Concat(Enumerable.Range(0, 12).Select(i => "## Section " + i + "\n\n" + Fill(540) + "\n\n"));
    foreach (var minimum in new[] { 0, 500, 2000, 1000000 }) {
      var plan = MarkdownTranslationPlan.Create(document, 8, minimum);
      Check(Reconstruct(plan) == document && (plan.Count == 1 || plan.All(c => c.Markdown.Length >= minimum)), "the configured minimum applies to target text, excluding context and separators: " + minimum);
      if (minimum == 0 || minimum == 500) Check(plan.Count == 8, "a small or disabled minimum lets a compact document use eight requests: " + minimum);
      if (minimum == 2000) Check(plan.Count == 3, "a 2000-character minimum limits a roughly 6800-character document to three parts");
      if (minimum == 1000000) Check(plan.Count == 1, "a source shorter than the minimum is sent whole");
    }
    var prefix = string.Concat(Enumerable.Repeat(" \t\r\n", 700));
    var padded = prefix + string.Join("\r\n\r\n", Enumerable.Repeat(Fill(490), 8)) + "\r\n\r\n";
    var paddedPlan = MarkdownTranslationPlan.Create(padded, 8, 500);
    Check(paddedPlan.Count == 4 && paddedPlan.All(c => c.Markdown.Length >= 500) && Reconstruct(paddedPlan) == padded,
      "a long whitespace prefix and CRLF separators do not satisfy a chunk minimum on their own");

    // Exhaustively enumerate small paragraph partitions independently of the planner.
    // This catches an early attractive heading/cut consuming space needed by later parts.
    var random = new Random(481);
    for (var trial = 0; trial < 70; trial++) {
      var atoms = Enumerable.Range(0, random.Next(2, 10)).Select(_ => Fill(random.Next(10, 550))).ToArray();
      var source = string.Join("\n\n", atoms); var minimum = new[] { 0, 50, 200, 500, 2000 }[trial % 5]; var workers = trial % 2 == 0 ? 4 : 8;
      var maximum = 1;
      for (var mask = 0; mask < (1 << (atoms.Length - 1)); mask++) {
        var start = 0; var count = 0; var valid = true;
        for (var end = 1; end <= atoms.Length; end++) {
          if (end < atoms.Length && (mask & (1 << (end - 1))) == 0) continue;
          if (string.Join("\n\n", atoms.Skip(start).Take(end - start)).Length < minimum) { valid = false; break; }
          count++; start = end;
        }
        if (valid) maximum = Math.Max(maximum, count);
      }
      var plan = MarkdownTranslationPlan.Create(source, workers, minimum);
      if (plan.Count != Math.Min(workers, maximum) || Reconstruct(plan) != source || (plan.Count > 1 && plan.Any(c => c.Markdown.Length < minimum)))
        throw new Exception("Minimum-size partition regression at trial " + trial);
    }
    Check(true, "70 varied compact documents reach the feasible worker count while respecting every minimum-sized target");
  }

  private static void AtomicBlocks()
  {
    var body = Paragraphs("inside", 18, 1050).TrimEnd('\r', '\n');
    Atomic("YAML front matter", "---\ndescription: |\n" + string.Concat(Enumerable.Range(0, 18).Select(i => "  SOURCE_description " + Fill(1050) + "\n\n")) + "title: \"SOURCE_title\"\n---", true);
    Atomic("unclosed YAML", "---\ndescription: |\n" + body, true, true);
    Atomic("four-backtick fence containing a shorter fence", "````text\n" + body + "\n```\n\n" + Fill(2500) + "\n````");
    Atomic("tilde fence containing shorter tildes", "~~~~text\n" + body + "\n~~~\n\n" + Fill(2500) + "\n~~~~");
    foreach (var fakePrefix in new[] { "    ", "> ", "- ", "1. " })
      Atomic("top-level fence with a fake indented quote or list closer", "```text\n" + Fill(12000) + "\n" + fakePrefix + "```\n\n" + Fill(4000) + "\n```");
    Atomic("unclosed fenced tail", "```text\n" + body, false, true);
    Atomic("two-space list continuations", string.Concat(Enumerable.Range(0, 18).Select(i => "- SOURCE_item " + i + "\n\n  continuation " + Fill(1100) + "\n\n")).TrimEnd('\r', '\n'));
    Atomic("empty list item with continued paragraphs", "-\n\n" + string.Concat(Enumerable.Range(0, 18).Select(i => "  continuation " + Fill(1100) + "\n\n")).TrimEnd('\r', '\n'));
    Atomic("list beginning with a fenced block and continued prose", "- ```text\n  " + body.Replace("\n", "\n  ") + "\n  ```\n\n  SOURCE_continuation " + Fill(4500));
    Atomic("blockquote paragraphs", string.Concat(Enumerable.Range(0, 18).Select(i => "> SOURCE_quote " + Fill(1100) + "\n\n")).TrimEnd('\r', '\n'));
    Atomic("nested HTML", "<div>\n" + body + "\n<div>\n" + Fill(2500) + "\n</div>\n</div>");
    Atomic("HTML script", "<script>\n" + body + "\n</script>");
    Atomic("HTML textarea", "<textarea>\n" + body + "\n</textarea>");
    Atomic("HTML details", "<details>\n<summary>SOURCE_title</summary>\n" + body + "\n</details>");
    Atomic("multiline HTML opening tag", "<div\n class=\"fixture\">\n" + body + "\n</div>");
    Atomic("HTML comment", "<!--\n" + body + "\n-->");
    Atomic("CDATA", "<![CDATA[\n" + body + "\n]]>");
    Atomic("HTML processing instruction", "<?fixture\n" + body + "\n?>");
    Atomic("multiline HTML declaration", "<!DOCTYPE fixture\n" + body + "\n>");
    Atomic("table", "| SOURCE_field | Value |\n| --- | --- |\n" + string.Concat(Enumerable.Range(0, 35).Select(i => "| row" + i + " | " + Fill(650) + " |\n")).TrimEnd('\n'));
    Atomic("setext heading with its body", "SOURCE_heading\n---\n\nSOURCE_body " + Fill(22000));
    Atomic("ATX heading with its body", "## SOURCE_heading\n\nSOURCE_body " + Fill(22000));
    Atomic("indented code", string.Concat(Enumerable.Range(0, 18).Select(i => "    code" + i + " " + Fill(1100) + "\n\n")).TrimEnd('\r', '\n'));
    Atomic("indented code continued with one space and a tab", "    " + Fill(12000) + "\n\n \t" + Fill(4000));
    Atomic("dollar math", "$$\n" + body + "\n$$");
    Atomic("bracket math", "\\[\n" + body + "\n\\]");
    Atomic("nested Markdown containers", "::: warning\n" + body + "\n::: note\n" + Fill(2500) + "\n:::\n:::");
    Atomic("valid multiline inline code", "SOURCE_prose ``" + string.Join("\n", Enumerable.Range(0, 40).Select(i => "literal" + i + " " + Fill(550))) + "`` SOURCE_end");
    Atomic("footnote definition continuation", "[^fixture]: SOURCE_note\n\n" + string.Concat(Enumerable.Range(0, 18).Select(i => "  continued" + i + " " + Fill(1100) + "\n\n")).TrimEnd('\r', '\n'));
    Atomic("definition list with continued definitions", "SOURCE_term\n\n" + string.Concat(Enumerable.Range(0, 18).Select(i => ": SOURCE_definition " + Fill(650) + "\n\n  continued" + i + " " + Fill(650) + "\n\n")).TrimEnd('\r', '\n'));
  }

  private static bool ValidUtf16(string value)
  {
    for (var i = 0; i < value.Length; i++) {
      if (char.IsHighSurrogate(value[i])) { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false; }
      else if (char.IsLowSurrogate(value[i])) return false;
    }
    return true;
  }
  private static void Context()
  {
    var source = Source(); var plan = MarkdownTranslationPlan.Create(source, 4);
    Check(plan.Count > 1 && plan.All(c => c.DocumentContext == plan[0].DocumentContext) && plan[0].DocumentContext.Contains("# Shared")
      && plan[0].DocumentContext.Contains("[fixture-link]:"), "every chunk shares the document outline and reference definitions");
    Check(plan[0].ContextBefore == "" && plan.Last().ContextAfter == "" && plan.Skip(1).All(c => c.ContextBefore.Length > 0)
      && plan.Take(plan.Count - 1).All(c => c.ContextAfter.Length > 0), "neighbor context follows the actual document boundaries");
    var offset = 0;
    foreach (var chunk in plan) {
      var start = offset; offset += chunk.PrefixBefore.Length + chunk.Markdown.Length + chunk.SeparatorAfter.Length;
      Check(chunk.ContextBefore.Length <= 1200 && chunk.ContextAfter.Length <= 1200 && chunk.DocumentContext.Length <= 2400
        && source.Substring(0, start).EndsWith(chunk.ContextBefore, StringComparison.Ordinal)
        && source.Substring(offset).StartsWith(chunk.ContextAfter, StringComparison.Ordinal), "context is bounded exact neighboring source rather than translated output");
      var target = Target(CliTranslator.CreateChunkPrompt(chunk), out var context);
      Check(context && target == chunk.Markdown, "chunk prompt keeps shared context outside the single target DOCUMENT section");
    }
    var emoji = "# Emoji\n\n" + string.Concat(Enumerable.Range(0, 24).Select(i => "paragraph" + i + " " + string.Concat(Enumerable.Repeat("\ud83c\udf0d", 805)) + "\n\n"));
    Check(MarkdownTranslationPlan.Create(emoji, 4).All(c => ValidUtf16(c.Markdown) && ValidUtf16(c.ContextBefore) && ValidUtf16(c.ContextAfter)), "neighbor context never slices an emoji surrogate pair");
    foreach (var length in new[] { 2398, 2399 }) {
      var heading = "# " + Fill(length - 2); var edge = MarkdownTranslationPlan.Create(heading + "\n\n" + Source(), 4);
      Check(edge.Count > 1 && edge.All(c => c.DocumentContext.Length <= 2400 && c.DocumentContext == heading + "\n"),
        "outline uses bounded fixed LF at the maximum heading length without Windows CRLF overflow");
    }
  }

  private static void CacheKeys()
  {
    var cli = CliOptions(1); var source = Source(); var original = TranslationCache.Key(source, cli); cli.ParallelRequests = 3;
    Check(original != TranslationCache.Key(source, cli) && original == TranslationCache.Key(source, CliOptions(1)), "CLI cache identity changes with concurrency and stays stable for an unchanged draft");
    var api = new ApiConnection { Endpoint = "http://127.0.0.1:1/v1", Model = "fixture-api-model", ApiKey = "SYNTHETIC_PARALLEL_KEY", ProxyMode = "direct" };
    var options = ApiOptions(api, 1); var key = TranslationCache.Key(source, options); options.ParallelRequests = 3;
    Check(key != TranslationCache.Key(source, options) && !key.Contains(api.ApiKey) && TranslationCache.Key(source, options) == TranslationCache.Key(source, options.Copy()), "API cache includes concurrency without exposing a credential and survives a draft copy");
    Check(new TranslationOptions().ParallelRequests == 1, "fresh translation settings retain a single request by default");
    var beforeMinimum = TranslationCache.Key(source, cli); cli.MinimumChunkCharacters = 500;
    Check(beforeMinimum != TranslationCache.Key(source, cli) && cli.Copy().MinimumChunkCharacters == 500, "CLI translation cache and option snapshots include the minimum chunk size");
    beforeMinimum = TranslationCache.Key(source, options); options.MinimumChunkCharacters = 0;
    Check(beforeMinimum != TranslationCache.Key(source, options) && options.Copy().MinimumChunkCharacters == 0, "API translation cache and option snapshots include a disabled minimum");
  }

  private static async Task Validation()
  {
    foreach (var minimum in new[] { -1, 1000001 }) {
      Check(await Failure(() => Task.FromResult(MarkdownTranslationPlan.Create(Source(), 4, minimum))) is ArgumentOutOfRangeException, "plan rejects invalid minimum chunk size");
      var invalid = CliOptions(4); invalid.MinimumChunkCharacters = minimum;
      Check(await Failure(() => new CliTranslator().TranslateAsync(Source(), invalid, CancellationToken.None)) is ArgumentException, "public translation rejects an invalid minimum before starting transports");
    }
    foreach (var number in new[] { 0, 9 }) {
      Check(await Failure(() => Task.FromResult(MarkdownTranslationPlan.Create(Source(), number))) is ArgumentOutOfRangeException, "plan rejects concurrency outside one to eight");
      Check(await Failure(() => TranslationBatch.RunAsync(MarkdownTranslationPlan.Create(Source(), 3), number, (c, t) => Task.FromResult(c.Markdown), CancellationToken.None)) is ArgumentOutOfRangeException, "batch rejects concurrency outside one to eight");
      var options = CliOptions(number);
      Check(await Failure(() => new CliTranslator().TranslateAsync(Source(), options, CancellationToken.None)) is ArgumentException, "public translation rejects invalid concurrency before any transport");
    }
    Check(await Failure(() => Task.FromResult(MarkdownTranslationPlan.Create(null, 1))) is ArgumentNullException, "plan rejects a missing document");
    Check(await Failure(() => TranslationBatch.RunAsync(new TranslationChunk[0], 1, (c, t) => Task.FromResult("x"), CancellationToken.None)) is ArgumentException, "batch rejects a missing translation plan");
  }

  private sealed class ProgressLog : IProgress<TranslationProgress>
  {
    private readonly List<TranslationProgress> values = new List<TranslationProgress>();
    public void Report(TranslationProgress value) { lock (values) values.Add(value); }
    public List<TranslationProgress> Values { get { lock (values) return values.ToList(); } }
  }
  private static void Maximum(ref int maximum, int value)
  { int old; do { old = maximum; if (old >= value) return; } while (Interlocked.CompareExchange(ref maximum, value, old) != old); }

  private static async Task OrderedWorkers()
  {
    var plan = MarkdownTranslationPlan.Create(Source(), 3); var active = 0; var maximum = 0; var started = 0;
    var firstWave = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var order = new List<int>(); var progress = new ProgressLog();
    var output = await Within(TranslationBatch.RunAsync(plan, 3, async (chunk, token) => {
      Maximum(ref maximum, Interlocked.Increment(ref active)); if (Interlocked.Increment(ref started) == 3) firstWave.TrySetResult(true);
      try { await firstWave.Task; await Task.Delay(chunk.Index == 0 ? 250 : 15, token); lock (order) order.Add(chunk.Index); return "RU_CHUNK_" + chunk.Index; }
      finally { Interlocked.Decrement(ref active); }
    }, CancellationToken.None, progress));
    Check(plan.Count >= 3 && maximum == 3 && active == 0 && started == plan.Count, "worker pool reaches but never exceeds its bound and releases every worker");
    Check(order[0] != 0 && output == string.Concat(plan.Select(c => c.PrefixBefore + "RU_CHUNK_" + c.Index + c.SeparatorAfter)), "out-of-order completion stitches results in original Markdown order");
    Check(progress.Values.Select(p => p.Completed).SequenceEqual(Enumerable.Range(0, plan.Count + 1)) && progress.Values.All(p => p.Total == plan.Count), "progress is monotonic from zero through all completed chunks");
  }

  private static async Task Cancellation()
  {
    var plan = MarkdownTranslationPlan.Create(Source(), 3); var active = 0; var started = 0; var released = 0;
    var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var cancel = new CancellationTokenSource()) {
      var pending = TranslationBatch.RunAsync(plan, 3, async (chunk, token) => {
        Interlocked.Increment(ref active); if (Interlocked.Increment(ref started) == 3) ready.TrySetResult(true);
        try { await Task.Delay(Timeout.Infinite, token); return "unreachable"; }
        finally { Interlocked.Decrement(ref active); Interlocked.Increment(ref released); }
      }, cancel.Token);
      await Within(ready.Task); cancel.Cancel(); var error = await Within(Failure(async () => { await pending; }));
      Check(error is OperationCanceledException && active == 0 && started == 3 && released == 3, "external cancellation waits for every active worker before returning without a partial document");
    }
    using (var cancel = new CancellationTokenSource()) {
      cancel.Cancel(); var invoked = 0;
      var error = await Failure(() => TranslationBatch.RunAsync(plan, 3, (c, t) => { Interlocked.Increment(ref invoked); return Task.FromResult("x"); }, cancel.Token));
      Check(error is OperationCanceledException && invoked == 0, "pre-cancellation prevents any worker from starting");
    }
  }

  private static async Task PrimaryFailure()
  {
    var plan = MarkdownTranslationPlan.Create(Source(), 3); var active = 0; var started = 0; var released = 0;
    var primary = new InvalidOperationException("EXPECTED_PRIMARY_FAILURE"); var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var error = await Within(Failure(() => TranslationBatch.RunAsync(plan, 3, async (chunk, token) => {
      Interlocked.Increment(ref active); if (Interlocked.Increment(ref started) == 3) ready.TrySetResult(true);
      try { await ready.Task; if (chunk.Index == 0) throw primary; await Task.Delay(Timeout.Infinite, token); return "unreachable"; }
      finally { Interlocked.Decrement(ref active); Interlocked.Increment(ref released); }
    }, CancellationToken.None)));
    Check(ReferenceEquals(error, primary) && active == 0 && started == 3 && released == 3, "primary failure cancels peers waits for shutdown and remains the reported cause");
  }

  private static async Task EmptyAndPartial()
  {
    var plan = MarkdownTranslationPlan.Create(Source(), 3);
    foreach (var empty in new[] { "", " \r\n\t", "\uFEFF", "\r\n\uFEFF\r\n" }) {
      var error = await Failure(() => TranslationBatch.RunAsync(plan, 1, (c, t) => Task.FromResult(empty), CancellationToken.None));
      Check(error is InvalidOperationException, "empty whitespace or BOM-only model output cannot become a successful partial document");
    }
    var progress = new ProgressLog(); var invoked = 0;
    var failure = await Failure(() => TranslationBatch.RunAsync(plan, 1, (c, t) => {
      invoked++; if (c.Index == 1) throw new InvalidOperationException("FAIL_AFTER_FIRST_RESULT"); return Task.FromResult("translated first chunk");
    }, CancellationToken.None, progress));
    Check(failure is InvalidOperationException && invoked == 2 && progress.Values.Last().Completed == 1 && progress.Values.Last().Total > 1,
      "a later failure never reports a successfully completed partial translation");
    var single = MarkdownTranslationPlan.Create("    indented code", 1);
    Check(await TranslationBatch.RunAsync(single, 8, (c, t) => Task.FromResult("\uFEFF    translated code\r\n"), CancellationToken.None) == "    translated code", "single-chunk normalization preserves significant leading spaces");
    var aggregate = new[] {
      new TranslationChunk(0, "first target", "", "", "", ""),
      new TranslationChunk(1, "second target", "", "", "", "")
    };
    var aggregateProgress = new ProgressLog(); var aggregateCalls = 0;
    var overflow = await Failure(() => TranslationBatch.RunAsync(aggregate, 1, (c, t) => {
      aggregateCalls++; return Task.FromResult(new string('x', 16000001));
    }, CancellationToken.None, aggregateProgress));
    Check(overflow is InvalidOperationException && overflow.Message.Contains("32") && aggregateCalls == 2 && aggregateProgress.Values.Last().Completed == 1,
      "two individually bounded results exceeding 32 million aggregate characters fail before complete progress or stitched success");
  }

  private static TranslationOptions CliOptions(int parallel) => new TranslationOptions {
    Executable = Executable, ProviderId = "custom", UseCustomArguments = true, Arguments = "fake-echo", Model = "fixture-cli-model", TimeoutSeconds = 15, ParallelRequests = parallel
  };
  private static TranslationOptions ApiOptions(ApiConnection connection, int parallel) => new TranslationOptions {
    ConnectionMode = "api", ApiConnections = new List<ApiConnection> { connection }, SelectedApiConnectionId = connection.Id, TimeoutSeconds = 15, ParallelRequests = parallel
  };
  private static string Target(string prompt, out bool context)
  {
    context = prompt.Contains("_OUTLINE\n") && prompt.Contains("_BEFORE\n") && prompt.Contains("_AFTER\n") && prompt.Contains("Do NOT translate, copy");
    var match = Regex.Match(prompt, @"(?m)^DOCUMENT_(?<id>[a-f0-9]{32})\n");
    if (!match.Success) throw new Exception("Fixture did not receive a DOCUMENT target.");
    var start = match.Index + match.Length; var end = prompt.IndexOf("\nEND_DOCUMENT_" + match.Groups["id"].Value + "\n", start, StringComparison.Ordinal);
    if (end < start) throw new Exception("Fixture received an unterminated DOCUMENT target.");
    return prompt.Substring(start, end - start);
  }
  private static int Delay(string target) => target.Contains("BLOCK_00") ? 550 : 120;
  private static void Log(JObject value)
  {
    var directory = Environment.GetEnvironmentVariable("PARALLEL_TRANSLATION_FIXTURE_LOG");
    if (!string.IsNullOrEmpty(directory)) File.WriteAllText(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"), value.ToString(Formatting.None), Utf8);
  }
  private static int FakeEcho(string[] args)
  {
    var started = DateTime.UtcNow.Ticks; var prompt = Console.In.ReadToEnd(); var target = Target(prompt, out var context);
    Thread.Sleep(Delay(target)); var result = target.Replace("SOURCE_", "RU_");
    var output = Array.IndexOf(args, "--output-last-message");
    if (output >= 0) File.WriteAllText(args[output + 1], result, Utf8); else Console.Write(result);
    Log(new JObject { ["kind"] = "translation", ["target"] = target, ["context"] = context, ["start"] = started, ["end"] = DateTime.UtcNow.Ticks,
      ["isolated"] = args.Contains("mcp_servers.fixture.enabled=false") });
    return 0;
  }
  private static int FakeServer()
  {
    string line;
    while ((line = Console.ReadLine()) != null) {
      if (string.IsNullOrWhiteSpace(line.TrimStart('\uFEFF'))) continue;
      var request = JObject.Parse(line); var method = (string)request["method"];
      Log(new JObject { ["kind"] = "server", ["method"] = method }); if (request["id"] == null) continue;
      JObject result = new JObject();
      if (method == "model/list") result = new JObject { ["data"] = new JArray(new JObject { ["id"] = "fixture-codex", ["model"] = "fixture-codex", ["displayName"] = "Fixture", ["isDefault"] = true }) };
      if (method == "config/read") result = new JObject { ["config"] = new JObject { ["model"] = "fixture-codex", ["mcp_servers"] = new JObject { ["fixture"] = new JObject { ["enabled"] = true } } } };
      Console.WriteLine(new JObject { ["id"] = request["id"], ["result"] = result }.ToString(Formatting.None));
    }
    return 0;
  }

  private static int Overlap(IEnumerable<JObject> records)
  {
    var events = records.SelectMany(r => new[] { new KeyValuePair<long, int>((long)r["start"], 1), new KeyValuePair<long, int>((long)r["end"], -1) }).OrderBy(e => e.Key).ThenBy(e => e.Value);
    var active = 0; var maximum = 0; foreach (var value in events) { active += value.Value; maximum = Math.Max(maximum, active); } return maximum;
  }
  private static async Task CliIntegration(bool codex)
  {
    var directory = Path.Combine(scratch, codex ? "fake-codex" : "fake-custom"); Directory.CreateDirectory(directory);
    Environment.SetEnvironmentVariable("PARALLEL_TRANSLATION_FIXTURE_LOG", directory);
    var source = Source(); var plan = MarkdownTranslationPlan.Create(source, 3); var options = CliOptions(3);
    if (codex) { options.ProviderId = "codex"; options.UseCustomArguments = false; options.UseDefaultModel = true; options.Model = ""; }
    var progress = new ProgressLog(); var output = await Within(new CliTranslator().TranslateAsync(source, options, CancellationToken.None, progress), 20000);
    var records = Directory.GetFiles(directory, "*.json").Select(path => JObject.Parse(File.ReadAllText(path, Utf8))).ToList();
    var translations = records.Where(r => (string)r["kind"] == "translation").ToList();
    Check(output == source.Replace("SOURCE_", "RU_") && translations.Count == plan.Count && translations.All(r => (bool)r["context"]), (codex ? "Codex" : "custom CLI") + " native processes translate only targets and never duplicate shared context");
    Check(translations.Select(r => (string)r["target"]).OrderBy(v => v, StringComparer.Ordinal).SequenceEqual(plan.Select(c => c.Markdown).OrderBy(v => v, StringComparer.Ordinal)), "native CLI receives each exact planned target once");
    Check(Overlap(translations) >= 2 && Overlap(translations) <= 3 && (string)translations.OrderBy(r => (long)r["end"]).First()["target"] != plan[0].Markdown,
      "real native CLI requests overlap within the bound finish out of order and retain document order");
    Check(progress.Values.Last().Completed == plan.Count, "native CLI progress completes only after every target result");
    if (codex) Check(records.Count(r => (string)r["method"] == "initialize") == 1 && records.Count(r => (string)r["method"] == "config/read") == 1
      && records.Count(r => (string)r["method"] == "model/list") == 1 && translations.All(r => (bool)r["isolated"]), "parallel Codex resolves one native configuration and disables each discovered MCP server for every worker");
    else {
      var before = translations.Count; options.ParallelRequests = 1;
      Check(await Within(new CliTranslator().TranslateAsync(source, options, CancellationToken.None), 20000) == source.Replace("SOURCE_", "RU_"), "default single-request CLI keeps full-document output unchanged");
      var all = Directory.GetFiles(directory, "*.json").Select(path => JObject.Parse(File.ReadAllText(path, Utf8))).Where(r => (string)r["kind"] == "translation").ToList();
      Check(all.Count == before + 1 && all.Any(r => !(bool)r["context"] && (string)r["target"] == source), "single-request CLI prompt has one whole-document target without chunk context");
    }
  }

  private static async Task ApiIntegration()
  {
    var source = Source("\r\n"); var plan = MarkdownTranslationPlan.Create(source, 3);
    using (var server = new ApiOrigin()) {
      var connection = new ApiConnection { Endpoint = "http://127.0.0.1:" + server.Port + "/v1", Model = "fixture-api-model", ApiKey = "SYNTHETIC_PARALLEL_KEY", ProxyMode = "direct" };
      var progress = new ProgressLog(); var output = await Within(new CliTranslator().TranslateAsync(source, ApiOptions(connection, 3), CancellationToken.None, progress), 20000);
      var records = server.Records;
      Check(output == source.Replace("SOURCE_", "RU_") && records.Count == plan.Count && records.All(r => (bool)r["context"]), "loopback API uses the same target-only chunk prompts through central translation dispatch");
      Check(records.All(r => (string)r["method"] == "POST" && (string)r["path"] == "/v1/chat/completions" && (string)r["model"] == connection.Model)
        && records.Select(r => (string)r["target"]).OrderBy(v => v, StringComparer.Ordinal).SequenceEqual(plan.Select(c => c.Markdown).OrderBy(v => v, StringComparer.Ordinal)), "API wire JSON preserves every exact CRLF target and selected model once");
      Check(server.MaximumActive >= 2 && server.MaximumActive <= 3 && (string)records.OrderBy(r => (long)r["end"]).First()["target"] != plan[0].Markdown
        && progress.Values.Last().Completed == plan.Count, "real loopback API requests honor concurrency and stitch out-of-order results without context seams");
      Check(server.Failure == null, "loopback API fixture completed without hidden transport failures");
    }
  }

  private static async Task ProtectedCodeIntegration()
  {
    var source = "# Intro\n\nSOURCE_read `SOURCE_literal`.[^n]\n\n## Example\n\nSOURCE_explain.\n\n```markdown\n# SOURCE_example\n```\n\n## Notes\n\n[^n]: SOURCE_note with `SOURCE_footnote`.\n";
    var expected = source.Replace("SOURCE_read", "RU_read").Replace("SOURCE_explain", "RU_explain").Replace("SOURCE_note", "RU_note");
    var cli = CliOptions(8); cli.MinimumChunkCharacters = 0;
    Check(MarkdownTranslationPlan.Create(source, 8, 0).Count == 3, "global reference index cannot merge independent sections into one source container");
    var actualCli = await Within(new CliTranslator().TranslateAsync(source, cli, CancellationToken.None));
    Check(actualCli == expected,
      "native CLI restores fenced inline and cross-chunk footnote code while translating prose");
    using (var server = new ApiOrigin()) {
      var connection = new ApiConnection { Endpoint = "http://127.0.0.1:" + server.Port + "/v1", Model = "fixture-model", ProxyMode = "direct" };
      var api = ApiOptions(connection, 8); api.MinimumChunkCharacters = 0;
      Check(await Within(new CliTranslator().TranslateAsync(source, api, CancellationToken.None)) == expected,
        "parallel API shares exact code protection and source-ordered assembly with CLI");
      var unused = "[^unused]: SOURCE_prose with `SOURCE_code`.";
      Check(await Within(new ApiTranslator().TranslateAsync(unused, connection, 15, CancellationToken.None)) == unused.Replace("SOURCE_prose", "RU_prose"),
        "direct API path also protects code inside an unused footnote");
      var tail = "```text\nSOURCE_literal\n\n\n";
      api.ParallelRequests = 1;
      Check(await Within(new CliTranslator().TranslateAsync(tail, api, CancellationToken.None)) == tail,
        "single API response keeps every trailing code newline after transport normalization");
      cli.ParallelRequests = 1;
      Check(await Within(new CliTranslator().TranslateAsync(tail, cli, CancellationToken.None)) == tail,
        "single native CLI response keeps every trailing code newline after transport normalization");
    }
  }

  private sealed class ApiOrigin : IDisposable
  {
    private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new CancellationTokenSource();
    private readonly List<TcpClient> clients = new List<TcpClient>();
    private readonly List<JObject> records = new List<JObject>();
    private int active, maximum;
    public int Port { get; }
    public int MaximumActive => Volatile.Read(ref maximum);
    public Exception Failure { get; private set; }
    public List<JObject> Records { get { lock (records) return records.ToList(); } }
    public ApiOrigin() { listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port; Observe(Accept()); }
    private async Task Accept()
    { while (!stop.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(); lock (clients) clients.Add(client); Observe(Handle(client)); } }
    private async void Observe(Task task)
    { try { await task; } catch (Exception error) { if (!stop.IsCancellationRequested) Failure = error; } }
    private async Task Handle(TcpClient client)
    {
      using (client) {
        var started = DateTime.UtcNow.Ticks; Maximum(ref maximum, Interlocked.Increment(ref active));
        try {
          var stream = client.GetStream(); var header = new MemoryStream();
          while (true) {
            var bytes = await ReadExactly(stream, 1, stop.Token); header.WriteByte(bytes[0]); var buffer = header.GetBuffer(); var length = (int)header.Length;
            if (length >= 4 && buffer[length - 4] == 13 && buffer[length - 3] == 10 && buffer[length - 2] == 13 && buffer[length - 1] == 10) break;
            if (length > 65536) throw new Exception("Fixture HTTP header exceeds its bound.");
          }
          var lines = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
          var lengthLine = lines.Single(v => v.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
          var count = int.Parse(lengthLine.Substring(lengthLine.IndexOf(':') + 1)); if (count < 0 || count > 2000000) throw new Exception("Fixture request body exceeds its bound.");
          var json = JObject.Parse(Utf8.GetString(await ReadExactly(stream, count, stop.Token)));
          var prompt = (string)json["messages"]?[0]?["content"]; var target = Target(prompt, out var context); await Task.Delay(Delay(target), stop.Token);
          var body = Utf8.GetBytes(new JObject { ["choices"] = new JArray(new JObject { ["message"] = new JObject { ["role"] = "assistant", ["content"] = target.Replace("SOURCE_", "RU_") }, ["finish_reason"] = "stop" }) }.ToString(Formatting.None));
          var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
          var start = lines[0].Split(' ');
          lock (records) records.Add(new JObject { ["target"] = target, ["context"] = context, ["model"] = json["model"], ["method"] = start[0], ["path"] = start[1], ["start"] = started, ["end"] = DateTime.UtcNow.Ticks });
          await stream.WriteAsync(response, 0, response.Length, stop.Token); await stream.WriteAsync(body, 0, body.Length, stop.Token);
        }
        finally { Interlocked.Decrement(ref active); }
      }
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); lock (clients) foreach (var client in clients) client.Close(); }
  }
  private static async Task<byte[]> ReadExactly(Stream stream, int count, CancellationToken token)
  {
    var bytes = new byte[count]; var offset = 0;
    while (offset < count) { var read = await stream.ReadAsync(bytes, offset, count - offset, token); if (read == 0) throw new IOException("Fixture stream closed."); offset += read; }
    return bytes;
  }
}
