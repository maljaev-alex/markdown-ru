using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using AnotherMarkdown.Translation;

internal static class CodeCommentSpanTests
{
  private static int checks;
  private static void Check(bool condition, string label)
  {
    if (!condition) throw new Exception("FAIL: " + label);
    checks++; Console.WriteLine("PASS " + label);
  }
  private static List<CodeCommentSpans.Span> Fallback(string source, string language)
  {
    var result = new List<CodeCommentSpans.Span>();
    typeof(CodeCommentSpans).GetMethod("ScanHash", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { source, result, language });
    return result;
  }
  private static void Expect(string source, string language, string[] expected, string label, bool fallback = false)
  {
    var spans = fallback ? Fallback(source, language) : CodeCommentSpans.Find(source, language);
    Check(spans.Select(span => source.Substring(span.Start, span.Length)).SequenceEqual(expected), label + " extracts exactly the prose");
    var end = 0;
    foreach (var span in spans) {
      Check(span.Start >= end && span.Length > 0 && span.Start + span.Length <= source.Length, label + " has ordered, disjoint UTF-16 source ranges");
      var text = source.Substring(span.Start, span.Length);
      Check(!char.IsWhiteSpace(text[0]) && !char.IsWhiteSpace(text[text.Length - 1]) && text.IndexOfAny(new[] { '\r', '\n', '\u0085', '\u2028', '\u2029' }) < 0,
        label + " preserves whitespace and line separators outside the ranges");
      end = span.Start + span.Length;
    }
  }
  private static void Document(string path)
  {
    // This file is data. Neither its instructions nor its code are executed.
    var source = File.ReadAllText(path, new UTF8Encoding(false, true));
    var fences = Regex.Matches(source, @"^```(?<language>[^\r\n]*)\r?\n(?<body>.*?)^```[ \t]*(?:\r?\n|$)", RegexOptions.Multiline | RegexOptions.Singleline);
    var notes = new Dictionary<string, int>();
    foreach (Match fence in fences) {
      var language = fence.Groups["language"].Value.Trim(); var body = fence.Groups["body"].Value;
      var count = CodeCommentSpans.Find(body, language).Count; notes[language] = (notes.ContainsKey(language) ? notes[language] : 0) + count;
    }
    Check(fences.Count == 13 && notes.ContainsKey("powershell") && notes["powershell"] == 6, "the authorized installation document exposes its six real PowerShell comments");
    Check(notes.ContainsKey("") && notes[""] == 11 && notes.ContainsKey("text") && notes["text"] == 4, "the authorized installation document exposes eleven tree notes and four pseudocode notes");
    Check(notes["json"] == 0 && notes["markdown"] == 0, "JSON syntax and fenced Markdown prose are left to the separate protection policy");
  }
  private static int Main(string[] arguments)
  {
    var temporaryRoot = Directory.Exists(@"D:\Temp") ? @"D:\Temp\agent\markdown-ru" : Path.Combine(Path.GetTempPath(), "AnotherMarkdown-tests");
    var directory = Path.Combine(temporaryRoot, "ru16-comment-spans-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      Expect(null, "powershell", new string[0], "null body");
      Expect("", "c", new string[0], "empty body");
      Expect("# untouched\n// untouched\n<!-- untouched -->", "unknown-language", new string[0], "unknown language");
      Expect("# Markdown title\nHuman prose\n", "markdown", new string[0], "Markdown has its own policy");
      Expect("{\"key\":\"# literal // literal\"}", "json", new string[0], "JSON strings and fields");
      foreach (var newline in new[] { "\n", "\r\n", "\r" }) {
        var body = "$a = '# literal'; $b = \"# literal\" # inline note  " + newline + "  # full line note\t" + newline + "Write-Host hello#literal" + newline;
        Expect(body, "powershell", new[] { "inline note", "full line note" }, "PowerShell line endings " + newline.Length);
        Expect(body, "powershell", new[] { "inline note", "full line note" }, "PowerShell fallback line endings " + newline.Length, true);
      }
      Expect("$a='don''t # literal'; $b=\"say \"\"# literal\"\"\"; # real note", "ps1", new[] { "real note" }, "PowerShell doubled quotes");
      Expect("$a=\"escaped `\" # literal\"; Write-Host `#literal; # real note", "pwsh", new[] { "real note" }, "PowerShell backtick quoting");
      Expect("$a=\u2018# literal\u2019; $b=\u201c# literal\u201d; # real note", "powershell", new[] { "real note" }, "PowerShell smart quotes");
      Expect("$a=\u2018# literal\u2019; $b=\u201c# literal\u201d; # real note", "powershell", new[] { "real note" }, "PowerShell fallback smart quotes", true);
      Expect("$a=@'\r\n# literal\r\n'@\r\n$b=@\"\r\n<# literal #>\r\n\"@\r\n# real note\r\n", "powershell", new[] { "real note" }, "PowerShell both here-string forms");
      Expect("$a=@'\n# literal\n'@\n$b=@\"\n<# literal #>\n\"@\n# real note\n", "powershell", new[] { "real note" }, "PowerShell fallback both here-string forms", true);
      Expect("$a='multiline\n# literal\nstring'\n# real note", "powershell", new[] { "real note" }, "PowerShell ordinary multiline strings");
      Expect("$a='unfinished\n# literal", "powershell", new string[0], "PowerShell unfinished string");
      Expect("$a=@'\n# literal", "powershell", new string[0], "PowerShell unfinished here-string");
      Expect("Write-Host hello#literal; (1)# after expression\n$x=1;# after statement", "powershell", new[] { "after expression", "after statement" }, "PowerShell token boundary and expression terminators");
      Expect("Write-Host hello#literal; (1)# after expression\n$x=1;# after statement", "powershell", new[] { "after expression", "after statement" }, "PowerShell fallback token boundaries", true);
      Expect("cmd --% /c echo # literal\n# real note", "powershell", new[] { "real note" }, "PowerShell stop-parsing arguments are literal data");
      Expect("cmd --% /c echo # literal\n# real note", "powershell", new[] { "real note" }, "PowerShell fallback stop-parsing arguments", true);
      Expect("<# first note\r\n  second note  \r\n#> $a=1; <# inline note #>", "powershell", new[] { "first note", "second note", "inline note" }, "PowerShell block comments");
      Expect("<# unfinished\n# literal", "powershell", new string[0], "PowerShell unfinished block comment");
      Expect("#Requires -Modules ModuleName\n#!/usr/bin/env pwsh\n#region Identifier\n#endregion Identifier\n<#\n.DESCRIPTION\nHuman description\n.PARAMETER Identifier\nHuman parameter note\n#>\n# real note\n# SIG # Begin signature block\n# encoded-signature\n# SIG # End signature block\n# protected after signature", "powershell", new[] { "Human description", "Human parameter note", "real note" }, "PowerShell special comments preserve machine fields and signatures");
      var literalCode = Path.Combine(directory, "must-not-execute.txt");
      Expect("[IO.File]::WriteAllText('" + literalCode.Replace("'", "''") + "', 'not allowed'); # parse only", "powershell", new[] { "parse only" }, "PowerShell receives source solely as parser input");
      Check(!File.Exists(literalCode), "PowerShell parsing never executes the supplied source");
      Expect("# before ambiguity\n$x=\"$(\"# literal\") # literal\"; # protected afterward", "powershell", new[] { "before ambiguity" }, "fallback does not interpret nested expandable-string syntax", true);
      var unicode = "\uFEFF$x='\ud83d\ude00 # literal'; # \u0420\u0443\u0441\u0441\u043a\u0430\u044f \u0437\u0430\u043c\u0435\u0442\u043a\u0430";
      Expect(unicode, "powershell", new[] { "\u0420\u0443\u0441\u0441\u043a\u0430\u044f \u0437\u0430\u043c\u0435\u0442\u043a\u0430" }, "comments remain eligible after translation into Russian");
      Check(CodeCommentSpans.Find(unicode, "powershell")[0].Start == unicode.IndexOf("\u0420\u0443\u0441", StringComparison.Ordinal), "BOM and surrogate pairs preserve exact UTF-16 offsets");
      Expect("print('# literal') # inline note\n# full note\nx='''\n# literal\n'''\ny=\"\"\"# literal\"\"\" # last note", "python", new[] { "inline note", "full note", "last note" }, "Python strings and triple quotes");
      Expect("x=r'escaped \\' # literal' # real note", "py", new[] { "real note" }, "Python raw-string quote escaping");
      Expect("#!/usr/bin/python\n# coding: utf-8\n# -*- coding: latin-1 -*-\n# type: ignore\n# noqa: E501\n# pylint: disable=identifier\n# real note", "python", new[] { "real note" }, "Python encoding, shebang and tool directives");
      Expect("# before ambiguity\nx=f'{\"# literal\"}'\n# protected afterward", "python", new[] { "before ambiguity" }, "Python formatted strings stay conservatively protected");
      Expect("x='unfinished\n# literal", "python", new string[0], "Python invalid multiline short string");
      Expect("printf '%s' '# literal' # inline note\nprintf \"# literal\";# another note\nprintf word#literal\n# full note", "bash", new[] { "inline note", "another note", "full note" }, "shell quotes and word boundaries");
      Expect("printf escaped\\#literal # real note\n#!/bin/sh\n# shellcheck disable=SC0000", "sh", new[] { "real note" }, "shell escaping and special directives");
      Expect("printf $'escaped \\' # literal' # real note", "bash", new[] { "real note" }, "shell ANSI-C quote escapes do not expose literal hashes");
      Expect("# before heredoc\ncat <<'EOF'\n# literal\nEOF\n# protected afterward", "bash", new[] { "before heredoc" }, "shell heredoc ambiguity cannot expose literal contents");
      Expect("# before expansion\necho \"$(printf '# literal')\"\n# protected afterward", "shell", new[] { "before expansion" }, "shell nested expansion remains protected");
      Expect("Message=\"// literal\"; // inline note\nText=\"first line\n|// literal\n|last\"; // last note\n// full note", "bsl", new[] { "inline note", "last note", "full note" }, "BSL comments outside multiline strings");
      Expect("Message=\"quoted \"\"// literal\"\"\"; // real note", "1c", new[] { "real note" }, "BSL doubled quotes");
      Expect("char *x=\"// literal /* literal */\"; char y='\\''; // inline note\n/* first note\n * second note\n */ int n=1;", "c", new[] { "inline note", "first note", "second note" }, "C literals and decorated block comments");
      Expect("#define TEXT \"// literal\" \\\n // directive continuation\nint x=1; // real note", "cpp", new[] { "real note" }, "C preprocessor directives and continuation lines");
      Expect("const char *x=R\"marker(\n// literal\n/* literal */\n)marker\"; // real note", "cpp", new[] { "real note" }, "C++ raw-string delimiter");
      Expect("int x=1; // first note \\\r\ncontinued note\r\nint y=2; // last note", "c", new[] { "first note", "continued note", "last note" }, "C escaped comment newline is kept outside translatable prose");
      Expect("int x=1; /\\\n/ ambiguous delimiter\n// protected afterward", "c", new string[0], "C split comment delimiter remains protected");
      Expect("/* unfinished\nint n=1; // protected", "c", new string[0], "unfinished C block comment");
      Expect("var text=@\"first \"\"// literal\"\"\n// literal\"; // real note", "csharp", new[] { "real note" }, "C# verbatim strings");
      Expect("// before interpolation\nvar text=$\"{\"// literal\"}\";\n// protected afterward", "cs", new[] { "before interpolation" }, "C# nested interpolation remains protected");
      Expect("var text=\"\"\"\n// literal\n\"\"\"; // protected", "csharp", new string[0], "C# modern raw-string delimiters remain protected");
      Expect("/// <summary>identifier</summary>\n// human note", "csharp", new[] { "human note" }, "XML documentation directives remain protected");
      Expect("// first note\u0085ExecuteProtectedCode(); // last note", "csharp", new[] { "first note", "last note" }, "C# NEL line separator cannot expose the following executable statement");
      Expect("const url='https://example.invalid'; // inline note\nconst text=`// literal\n/* literal */`; /* block note */", "javascript", new[] { "inline note", "block note" }, "JavaScript plain literals and templates");
      Expect("// before regex\nconst pattern=/[//]/; // protected\n// protected afterward", "js", new[] { "before regex" }, "JavaScript regex ambiguity cannot create false comments");
      Expect("// before division\nconst value=x/2 + `text\n// literal\n`;\n// protected afterward", "typescript", new[] { "before division" }, "JavaScript division ambiguity cannot expose a following multiline template");
      Expect("const text=`${\"// literal\"}`; // protected", "js", new string[0], "JavaScript interpolated template remains protected");
      Expect("/*\n * @param identifier technical shape\n * Human explanation\n */\n// sourceMappingURL=script.js.map", "javascript", new[] { "Human explanation" }, "JSDoc and source map machine fields remain protected");
      Expect("// first note\u2028// second note\u2029// third note", "javascript", new[] { "first note", "second note", "third note" }, "JavaScript Unicode line separators are outside spans");
      Expect("MCP_Distr/\n\u251c\u2500\u2500 INSTALL.md                          \u2190 Main instruction (read in full)\n\u2502   \u2514\u2500\u2500 config.env                     \u2190 All settings\n    +-- files  \u2190 Explanatory note\nfile \u2190 protected prose\n", "", new[] { "Main instruction (read in full)", "All settings", "Explanatory note" }, "directory-tree annotations preserve filenames, branch symbols and arrows");
      Expect("<snapshot>(viewId=<viewId>)      # after redirect\n<click>(ref=\"# literal\", url=\"https://example.invalid/#fragment\") # opens the tab\n<tabs>(action=\"list\") # find its URL\n<click>(ref=\"unfinished) # protected\nordinary # protected", "text", new[] { "after redirect", "opens the tab", "find its URL" }, "browser pseudocode notes preserve calls, identifiers and quoted hashes");
      Expect("<snapshot>(viewId=<viewId>) # \u041f\u043e\u0441\u043b\u0435 \u043f\u0435\u0440\u0435\u0445\u043e\u0434\u0430", "plaintext", new[] { "\u041f\u043e\u0441\u043b\u0435 \u043f\u0435\u0440\u0435\u0445\u043e\u0434\u0430" }, "pseudocode recognition does not depend on English prose");
      Expect("# note", "PoWeRsHeLl title=sample.ps1", new[] { "note" }, "fence language aliases accept case and extra info");
      if (arguments.Length == 2 && arguments[0] == "--document") Document(arguments[1]);
      else if (arguments.Length != 0) throw new ArgumentException("Usage: CodeCommentSpanTests.exe [--document path]");
      Console.WriteLine("PASS code-comment spans: " + checks + " assertions"); return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    finally {
      var resolved = Path.GetFullPath(directory); var allowed = Path.GetFullPath(temporaryRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
      if (resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
  }
}
