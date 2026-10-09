using System;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AnotherMarkdown.Translation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Independently authored fixtures for the cited grammar rules; not just user documents.
// CommonMark 0.31.2: https://spec.commonmark.org/0.31.2/
// GFM tables: https://github.github.com/gfm/#tables-extension-
// @mdit extensions: https://mdit-plugins.github.io/attrs.html and /dl.html
internal static class MarkdownStructureTests
{
  private static int checks;
  private static void Check(bool condition, string label)
  { if (!condition) throw new Exception("FAIL: " + label); checks++; }
  private static string Join(System.Collections.Generic.IReadOnlyList<TranslationChunk> plan) =>
    string.Concat(plan.Select(c => c.PrefixBefore + c.Markdown + c.SeparatorAfter));
  private static void Atomic(string label, string block)
  {
    foreach (var newline in new[] { "\n", "\r\n", "\r" }) {
      var atom = block.Replace("\n", newline);
      var source = ("Opening independent paragraph.\n\n" + block + "\n\nTrailing independent paragraph.\n\nAnother paragraph.").Replace("\n", newline);
      var plan = MarkdownTranslationPlan.Create(source, 8, 0);
      Check(Join(plan) == source && plan.Any(p => p.Markdown.Contains(atom)), label + " is indivisible: " + newline.Length);
    }
  }
  private static void MustReject(Action action, string label)
  {
    try { action(); } catch (InvalidOperationException) { Check(true, label); return; }
    throw new Exception("FAIL: did not reject " + label);
  }
  private static int Main(string[] args)
  {
    if (args.Length == 3 && args[0] == "--spec-cases") return RunCorpus(args[1], args[2]);
    try { Structure(); Protection(); Console.WriteLine("PASS Markdown grammar/protection: " + checks + " assertions"); return 0; }
    catch (Exception e) { Console.Error.WriteLine(e); return 1; }
  }
  private static int RunCorpus(string input, string outputPath)
  {
    var output = new JArray();
    foreach (JObject test in JArray.Parse(File.ReadAllText(input, Encoding.UTF8))) {
      var text = (string)test["source"];
      var row = new JObject { ["id"] = test["id"] };
      try {
        var protection = new MarkdownCodeProtection(text);
        if (protection.Restore(protection.Target) != text) throw new Exception("code_roundtrip");
        var plans = new JArray();
        foreach (var count in new[] { 2, 4, 8 }) {
          var plan = MarkdownTranslationPlan.Create(text, count, 0);
          if (Join(plan) != text) throw new Exception("source_reconstruction");
          var position = 0; var cuts = new JArray();
          foreach (var part in plan) { if (position > 0) cuts.Add(position); position += part.PrefixBefore.Length + part.Markdown.Length + part.SeparatorAfter.Length; }
          plans.Add(new JObject { ["requested"] = count, ["parts"] = plan.Count, ["cuts"] = cuts });
        }
        row["plans"] = plans;
      }
      catch (Exception error) { row["error"] = error.GetType().Name + ": " + error.Message; }
      output.Add(row);
    }
    File.WriteAllText(outputPath, output.ToString(Formatting.None), new UTF8Encoding(false));
    Console.WriteLine("CORPUS records=" + output.Count + " errors=" + output.Count(r => r["error"] != null));
    return output.Any(r => r["error"] != null) ? 1 : 0;
  }
  private static void Structure()
  {
    // CommonMark 4.6: types 6 and 7 end at a blank line, not at a closing tag.
    Atomic("HTML type 6 closing tag is not its block boundary", "<div>\n</div>\n# Still literal HTML");
    Atomic("HTML type 6 closing-tag opener", "</section>\n## Still literal HTML");
    Atomic("HTML type 7 custom tag", "<custom-panel>\n## Still literal HTML");
    Atomic("HTML multiline attributes", "<section\n class=\"example\">\n\n# Still HTML until close\n</section>");
    Atomic("HTML script type 1", "<script>\n\n# Not a heading\n\n</script>");
    Atomic("HTML comment type 2", "<!--\n\n# Not a heading\n-->");
    Atomic("HTML processing type 3", "<?example\n\n# Not a heading\n?>");
    Atomic("HTML declaration type 4", "<!EXAMPLE\n\n# Not a heading\n>");
    Atomic("HTML CDATA type 5", "<![CDATA[\n\n# Not a heading\n]]>");
    // CommonMark 5: lazy continuations, tabs and nested containers belong to their owner.
    Atomic("quote lazy continuation", "> Opening text\nlazy continuation\n>\n> Second paragraph");
    Atomic("nested list heading", "- Item\n\t## Nested heading\n\n\tContinuation\n- Next item");
    Atomic("ordered list with nested fence", "10. Intro\n\n    ```text\n    # Not a heading\n    ```\n\n    Explanation");
    Atomic("GFM task list", "- [x] First task\n\n  Continued explanation\n- [ ] Second task");
    // GFM 4.10 + @mdit attrs/dl extensions.
    Atomic("GFM escaped pipe", "| Label | Value |\n| :--- | ---: |\n| a\\|b | `c\\|d` |");
    Atomic("table attributes across blank line", "| Label | Value |\n| --- | --- |\n| x | y |\n\n{.table border=1}");
    Atomic("list attributes across blank line", "- First\n- Second\n\n{.list #items}");
    Atomic("colon definition", "Term\n\n: Definition\n\n  Continued definition");
    Atomic("tilde definition", "Term\n\n~ Definition\n\n  Continued definition\n\n~ Another definition");
    Atomic("tab extension container", "::: tabs#group\n\n@tab First\n\n# Child heading\n\n@tab Second\n\nContents\n\n:::");
    Atomic("nested custom containers", "::: warning\n\n::: note\n\n## Child\n\nText\n\n:::\n\n:::");
    Atomic("bracket math extension", "\\[\n\n# Not a heading\n\nx + y\n\\]");
    Atomic("dollar math extension", "$$\n\n# Not a heading\n\nx + y\n$$");
    Atomic("footnote continuation", "[^note]: Explanation\n\n    Second paragraph\n\n    ## Nested heading");
    Atomic("multiline setext", "First heading line\nSecond heading line\n-------------------\n\nSection body.");
    var source = "# Intro\n\nOpening.\n\n## Output\n\nExplanation.\n\n```markdown\n# Example\n```\n\nAfterword.\n\n## End\n\nFinish.";
    var plan = MarkdownTranslationPlan.Create(source, 8, 0);
    Check(plan.Count == 3 && plan[1].Markdown.Contains("Explanation.") && plan[1].Markdown.Contains("Afterword."), "worker count cannot separate a compact section's explanation and code");
    Check(Join(plan) == source, "semantic grouping retains every original separator");
    var setext = "One\n===\n\nFirst paragraph.\n\nAnother paragraph.\n\nTwo\n---\n\nBody.";
    var setextPlan = MarkdownTranslationPlan.Create(setext, 8, 0);
    Check(setextPlan.Count == 2 && setextPlan[1].Markdown.StartsWith("Two\n---"), "setext headings define complete semantic sections");
    var longSection = "# Large section\n\n" + string.Concat(Enumerable.Range(0, 20).Select(i => "Paragraph " + i + " " + new string('x', 1000) + "\n\n"));
    Check(MarkdownTranslationPlan.Create(longSection, 8, 0).Count > 1, "a long section can still use several independent paragraph groups");
    var outline = new MarkdownStructure("# Real\n\n```md\n# Fake\n[false]: /fake\n```\n\n[label]\n\n> [label]: /real\n\n[other]:\n  /multiline\n  \"caption\"\n\nSetext\nTitle\n---\n").Outline();
    Check(outline.Contains("# Real") && !outline.Contains("# Fake") && !outline.Contains("/fake"), "outline excludes headings and definitions inside examples");
    Check(outline.Contains("/real") && outline.Contains("/multiline"), "global and multiline reference definitions remain available as context");
    Check(outline.Contains("Setext\nTitle\n---"), "outline retains multiline setext heading text");
  }
  private static void Protection()
  {
    var examples = new[] {
      "Translate me with `Keep English` and ``tick ` inside``.",
      "Translate me with ``  first\nsecond  `` and escaped \\`literal tick.",
      "Translate me.\n\n```markdown\n# Keep English\n\n- Item\n```\n\nTranslate me again.",
      "Translate me.\n\n~~~~text\nKeep English\n~~~\n~~~~",
      "Translate me.\n\n    Keep English\n\n    Next line\n",
      "Translate me.\n\n> ```text\n> Keep English\n>\n> Next line\n> ```\n",
      "- Translate me.\n\n  ```text\n  Keep English\n  ```\n",
      "Translate me.\n\n```text\nUnclosed code at EOF\n",
      "---\n\n# Translate me\n\n```text\nKeep English\n```\n",
      "```text\nUnclosed code at EOF\n\n\n",
      "[^unused]: Translate me with `Keep English`.\n\n    ```text\n    Keep English\n    ```\n",
      "[^unused]: `Keep English`\n",
      "Translate me: $x + y$.\n\n$$\nx + y\n$$\n",
      "\uFEFF---\nname: identifier\ndescription: Translate me\n---\n\nTranslate me with `Keep English`."
    };
    foreach (var example in examples)
      foreach (var newline in new[] { "\n", "\r\n", "\r" }) {
        var source = example.Replace("\n", newline);
        var protectedText = new MarkdownCodeProtection(source);
        Check(protectedText.Target.Contains("AM_KEEP_"), "code/formulas use exact-source markers");
        Check(!protectedText.Target.Contains("Keep English") && !protectedText.Target.Contains("Unclosed code"), "the whole protected content is removed from the translation target");
        Check(protectedText.Restore(protectedText.Target) == source, "identity restoration is lossless including delimiters indentation BOM and line endings");
        var output = protectedText.Restore(protectedText.Target.Replace("Translate me", "RU_prose"));
        Check(output == source.Replace("Translate me", "RU_prose"), "only prose changes while every protected source byte is restored");
      }
    var protection = new MarkdownCodeProtection("Translate me with `one` then `two`.");
    var markers = Regex.Matches(protection.Target, @"AM_KEEP_[a-f0-9]+_[0-9]+_END").Cast<Match>().Select(m => m.Value).ToArray();
    MustReject(() => protection.Restore(protection.Target.Replace(markers[0], "")), "missing protected marker");
    MustReject(() => protection.Restore(protection.Target + markers[0]), "duplicated protected marker");
    MustReject(() => protection.Restore(protection.Target.Replace(markers[0], "SWAP").Replace(markers[1], markers[0]).Replace("SWAP", markers[1])), "reordered protected markers");
    MustReject(() => protection.Restore(protection.Target.Replace(markers[0], "`" + markers[0] + "`")), "new code wrapper around a protected marker");
    MustReject(() => protection.Restore("```markdown\n" + protection.Target + "\n```"), "outer code fence around the translation");
    Check(protection.Prompt(CliTranslator.CreatePrompt).Contains("READ-ONLY CONTEXT") && protection.Prompt(CliTranslator.CreatePrompt).Contains("`one`"), "protected originals are visible only as explicitly untrusted readonly context");
    var literal = "Escaped \\`tick, unmatched ` tick and ordinary prose.";
    Check(!new MarkdownStructure("---\n\n# Title\n\nBody").HasFrontMatter && new MarkdownStructure("---\nname: fixture\n---\n\nBody").HasFrontMatter,
      "a leading thematic break is not mistaken for YAML metadata");
    Check(new MarkdownCodeProtection(literal).Restore(literal) == literal, "literal unmatched and escaped backticks are ordinary text");
    var tail = "```text\nLiteral content\n\n\n";
    var tailProtection = new MarkdownCodeProtection(tail);
    var normalized = tailProtection.Target.TrimStart('\uFEFF', '\r', '\n').TrimEnd('\r', '\n');
    var stitched = TranslationBatch.RunAsync(MarkdownTranslationPlan.Create(tail, 1), 1,
      (chunk, token) => System.Threading.Tasks.Task.FromResult(tailProtection.Restore(normalized)),
      System.Threading.CancellationToken.None, preserveTranslatedWhitespace: true).GetAwaiter().GetResult();
    Check(stitched == tail, "batch assembly preserves protected trailing blank lines of an unclosed code block");
  }
}
