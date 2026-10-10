using System;
using System.IO;
using System.Linq;
using System.Text;
using AnotherMarkdown.Translation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class CodeAnnotationTests
{
  private static int checks;
  private static void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); checks++; }
  private static void Reject(Action action, string label)
  { try { action(); } catch (MarkdownProtectionException) { Check(true, label); return; } throw new Exception("FAIL: " + label); }
  private static string Reply(MarkdownCodeProtection protection, Func<string, string> translate = null)
  {
    var annotations = protection.AnnotationTargets;
    if (annotations.Count == 0) return protection.Target;
    foreach (var item in annotations) if (translate != null) item["text"] = translate((string)item["text"]);
    return new JObject { ["markdown"] = protection.Target, ["annotations"] = annotations }.ToString(Formatting.None);
  }
  private static int Main(string[] args)
  {
    try {
      RoundTrips(); InvalidOutput();
      if (args.Length == 2 && args[0] == "--document") Document(args[1]);
      Console.WriteLine("PASS code annotation protection: " + checks + " assertions"); return 0;
    } catch (Exception e) { Console.Error.WriteLine(e); return 1; }
  }
  private static void RoundTrips()
  {
    var examples = new[] {
      "```powershell\n$value = '# not a comment' # English note\n# English explanation with C:\\Work\\MCP_Distr and $env:TEMP\\MCP_Distr.zip\n```",
      "```powershell\n<#English note#>\nGet-Date # English note\n```",
      "```c\n/*English note*/\nint value = 1; // English note\n```",
      "```c\n/*\n * English note\n */\n```",
      "```python\nx = '# literal' # English note\n```",
      "```javascript\nconst a = 'https://test.invalid'; // English note\n```",
      "```bsl\nMessage(\"// literal\"); // English note\n```",
      "```\nMCP_Distr/\n\u251c\u2500\u2500 INSTALL.md     \u2190 English note\n\u2514\u2500\u2500 .env           \u2190 English note\n```",
      "```text\n<snapshot>(viewId=<viewId>) # English note /members/login\n<tabs>(action=\"list\") # English note disk.yandex.*\n```",
      "```markdown\n# English note\n\n- **English**: note with `KEEP_CODE` and [English note](https://test.invalid).\n\n[English ref]\n\n[English ref]: /destination\n```",
      "```markdown\n# English note\n\nKeep \\*literal\\* syntax and &amp; entity.\n```",
      "```powershell\n# English note\n",
      "    English literal without a known language\n",
      "```json\n{\"description\":\"English literal\"}\n```",
      "```unknown\n# English literal\n```",
      "```powershell\n#Requires -Version 5\n# English note\n# SIG # Begin signature block\n# signature data\n# SIG # End signature block\n```",
    };
    foreach (var example in examples) foreach (var newline in new[] { "\n", "\r\n", "\r" }) foreach (var container in new[] { "", "> ", "  " }) {
      var text = example;
      if (container.Length != 0) text = string.Join("\n", text.Split('\n').Select(l => container + l));
      if (container == "  ") text = "- Intro\n\n" + text;
      text = "\uFEFFIntro.\n\n" + text;
      text = text.Replace("\n", newline);
      var protection = new MarkdownCodeProtection(text);
      Check(protection.Restore(Reply(protection)) == text, "lossless source with container/newline/metadata: " + Array.IndexOf(examples, example));
      var output = protection.Restore(Reply(protection, s => s.Replace("English", "Русское").Replace("note", "пояснение")));
      if (protection.AnnotationTargets.Any(t => ((string)t["text"]).Contains("English")))
        Check(output.Contains("Русское"), "prose annotation was translated");
      Check(output.Contains("KEEP_CODE") == text.Contains("KEEP_CODE"), "inline code retained");
      Check(output.Contains("https://test.invalid") == text.Contains("https://test.invalid"), "destination retained");
    }
    var ps = new MarkdownCodeProtection("```powershell\nGet-Date # English note\n```");
    Check(ps.AnnotationTargets.Count == 1, "PowerShell annotation discovered via actual Markdig slices");
    Check(ps.Restore(Reply(ps, s => "Русское пояснение")).Contains("Get-Date # Русское пояснение"), "exact executable prefix retained");
    var prompt = ps.Prompt(CliTranslator.CreatePrompt);
    Check(prompt.Contains("OUTPUT CONTRACT") && prompt.Contains("untrusted document data"), "explicit structured data contract");
    var signed = new MarkdownCodeProtection("```powershell\n# English note\nGet-Date\n# SIG # Begin signature block\n# signature\n# SIG # End signature block\n```");
    Check(signed.AnnotationTargets.Count == 0, "entire signed script remains immutable including comments before its signature");
    var csharp = new MarkdownCodeProtection("```csharp\n// English path C:\\Temp\\\nint value = 1;\n```");
    Check(csharp.AnnotationTargets.Count == 1 && !csharp.AnnotationTargets.ToString().Contains("int value"), "CSharp backslash does not continue a line comment into executable code");
    Check(csharp.Restore(Reply(csharp, s => s.Replace("English", "Русский"))).Contains("\nint value = 1;\n"), "CSharp executable line after trailing backslash remains exact");
    foreach (var continuation in new[] { "\\", "\\  ", "??/", "??/\t" }) {
      var cppSource = "```cpp\n// English " + continuation + "\nCallMustStayCommented();\n```";
      var cpp = new MarkdownCodeProtection(cppSource);
      Check(cpp.AnnotationTargets.Count == 0 && cpp.Restore(Reply(cpp)) == cppSource, "C/C++ source splicing cannot turn a comment into executable code");
    }
  }
  private static void InvalidOutput()
  {
    var source = "```powershell\nGet-Date # English note C:\\Work\\MCP_Distr\n```";
    var ps = new MarkdownCodeProtection(source);
    Reject(() => ps.Restore(ps.Target), "missing annotation envelope");
    Reject(() => ps.Restore("{}"), "missing fields");
    Reject(() => ps.Restore(Reply(ps) + "{}"), "extra JSON object");
    foreach (var bad in new[] { "", "new\nline", "new\rline", "new\0line", "new\u2028line", "new\u2029line", "new\u0085line" })
      Reject(() => ps.Restore(Reply(ps, s => bad)), "unsafe line or lost identifier");
    var missing = JObject.Parse(Reply(ps)); missing["annotations"] = new JArray();
    Reject(() => ps.Restore(missing.ToString()), "missing annotation");
    var duplicate = JObject.Parse(Reply(ps)); ((JArray)duplicate["annotations"]).Add(duplicate["annotations"][0].DeepClone());
    Reject(() => ps.Restore(duplicate.ToString()), "duplicate annotation id");
    var unknown = JObject.Parse(Reply(ps)); unknown["annotations"][0]["id"] = "other";
    Reject(() => ps.Restore(unknown.ToString()), "unknown annotation id");
    Reject(() => ps.Restore(Reply(ps, s => s + " " + s)), "duplicated term marker");
    foreach (var pair in new[] {
      new[] { "```c\n/*English*/\nint x;\n```", "/ injected" },
      new[] { "```c\n/*English*/\nint x;\n```", "end */ injected /* again" },
      new[] { "```powershell\n<#English#>\nGet-Date\n```", "> injected" },
      new[] { "```powershell\n<#English#>\nGet-Date\n```", "end #> injected <# again" },
      new[] { "```c\n// English\nint x;\n```", "continued \\" },
      new[] { "```markdown\nEnglish\n```", "[injected](https://other.invalid)" },
      new[] { "```markdown\nEnglish\n```", "`injected`" },
    }) {
      var guard = new MarkdownCodeProtection(pair[0]);
      Reject(() => guard.Restore(Reply(guard, s => pair[1])), "injected syntax cannot expand the allowed prose range: " + pair[1]);
    }
    var terms = new AnnotationText("English e.g. C:\\Work\\MCP_Distr, $env:TEMP\\MCP_Distr.zip, /members/login, disk.yandex.*, <TARGET_DIR>, YYYY-MM-DD and `literal`.");
    Check(terms.Restore(terms.Target).StartsWith("English e.g."), "abbreviation stays translatable");
    Check(!terms.Target.Contains("C:\\Work") && !terms.Target.Contains("/members/login") && !terms.Target.Contains("YYYY-MM-DD"), "technical tokens hidden from translation");
    var identifiers = new AnnotationText("Set API_KEY and config_id before calling GraphMetadata with viewId from .env.");
    Check(!identifiers.Target.Contains("API_KEY") && !identifiers.Target.Contains("config_id") && !identifiers.Target.Contains("GraphMetadata") && !identifiers.Target.Contains("viewId"), "plain identifier shapes remain protected");
    Check(identifiers.Restore(identifiers.Target).Contains("API_KEY and config_id"), "plain identifiers restore exactly");
    var quotes = new AnnotationText("Run `` foo ` bar `` or `baz`, with \\`escaped and ` unmatched tick.");
    Check(!quotes.Target.Contains("foo") && !quotes.Target.Contains("bar") && !quotes.Target.Contains("baz"), "matching code-span delimiter runs hide the whole quotation");
    Check(quotes.Restore(quotes.Target) == "Run `` foo ` bar `` or `baz`, with \\`escaped and ` unmatched tick.", "quoted technical text restores without normalization");
  }
  private static void Document(string path)
  {
    var original = File.ReadAllText(path, new UTF8Encoding(false, true));
    var guard = new MarkdownCodeProtection(original);
    Check(guard.AnnotationTargets.Count >= 30, "real document exposes notes plus Markdown prose");
    Check(guard.Restore(Reply(guard)) == original, "real document reconstructs byte-equivalent text");
    Console.WriteLine("DOCUMENT annotation slots=" + guard.AnnotationTargets.Count);
  }
}
