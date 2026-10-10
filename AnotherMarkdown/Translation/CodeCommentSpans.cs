using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace AnotherMarkdown.Translation
{
  // Offsets refer to the original UTF-16 body, including any CRLFs/BOM.
  // This helper never evaluates source code or loads a PowerShell runspace.
  internal static class CodeCommentSpans
  {
    internal sealed class Span
    {
      internal int Start { get; private set; }
      internal int Length { get; private set; }
      internal Span(int start, int length) { Start = start; Length = length; }
    }
    private static readonly Lazy<MethodInfo> PowerShellParser = new Lazy<MethodInfo>(FindPowerShellParser);
    private static readonly Regex TreeNote = new Regex(@"^[ \t\u2502|]*(?:[\u251c\u2514]\u2500{2}|[+|\\]--)[ \t]+\S.*?[ \t]{2,}\u2190[ \t]+(?<note>.+)$", RegexOptions.CultureInvariant);
    private static readonly Regex PseudoCall = new Regex(@"^[ \t]*<[A-Za-z][A-Za-z0-9_-]*>[ \t]*\(", RegexOptions.CultureInvariant);
    private static readonly Regex Directive = new Regex(@"^(?:!|requires\b|region\b|endregion\b|SIG[ \t]*#|\.([A-Za-z]+)\b|@[A-Za-z]+\b|coding[=:]|.*?coding[=:][ \t]*[-\w]+|type[ \t]*:|noqa\b|(?:pylint|pyright|mypy|ruff|shellcheck|checkov|pragma|sourceMappingURL|sourceURL|eslint|ts-ignore|ts-expect-error|SPDX-License-Identifier)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static List<Span> Find(string body, string language)
    {
      var result = new List<Span>();
      if (string.IsNullOrEmpty(body)) return result;
      var name = (language ?? "").Trim().ToLowerInvariant().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
      if (name == "powershell" || name == "pwsh" || name == "ps1" || name == "ps") {
        if (!NativePowerShell(body, result)) ScanHash(body, result, "powershell");
      }
      else if (name == "python" || name == "py" || name == "python3") ScanHash(body, result, "python");
      else if (name == "shell" || name == "sh" || name == "bash" || name == "zsh") ScanHash(body, result, "shell");
      else if (name == "bsl" || name == "1c" || name == "onec" || name == "1c-enterprise") ScanSlash(body, result, "bsl");
      else if (name == "c" || name == "cpp" || name == "c++" || name == "csharp" || name == "cs" || name == "c#") ScanSlash(body, result, name == "csharp" || name == "cs" || name == "c#" ? "csharp" : "c");
      else if (name == "javascript" || name == "js" || name == "typescript" || name == "ts") ScanSlash(body, result, "javascript");
      else if (name == "" || name == "text" || name == "plaintext" || name == "plain") ScanAnnotations(body, result);
      return result;
    }

    private static MethodInfo FindPowerShellParser()
    {
      try {
        // Strong-name binding selects the installed Windows PowerShell engine,
        // rather than a project-local DLL or a PowerShell 7/.NET Core assembly.
        var assembly = Assembly.Load("System.Management.Automation, Version=3.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
        var parser = assembly.GetType("System.Management.Automation.Language.Parser", false);
        return parser?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "ParseInput"
          && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == typeof(string));
      }
      catch (Exception error) when (error is FileNotFoundException || error is FileLoadException || error is BadImageFormatException
        || error is TypeLoadException || error is ReflectionTypeLoadException || error is System.Security.SecurityException || error is NotSupportedException) { return null; }
    }
    private static bool NativePowerShell(string body, List<Span> result)
    {
      try {
        var parser = PowerShellParser.Value; if (parser == null) return false;
        var arguments = new object[] { body, null, null };
        parser.Invoke(null, arguments);
        var tokens = arguments[1] as IEnumerable; if (tokens == null) return false;
        var collected = new List<Span>(); var signature = false;
        foreach (var token in tokens) {
          var type = token.GetType(); if (type.GetProperty("Kind")?.GetValue(token, null)?.ToString() != "Comment") continue;
          var extent = type.GetProperty("Extent")?.GetValue(token, null); if (extent == null) continue;
          var extentType = extent.GetType(); var start = (int)extentType.GetProperty("StartOffset").GetValue(extent, null);
          var end = (int)extentType.GetProperty("EndOffset").GetValue(extent, null);
          if (start < 0 || end > body.Length || start >= end) continue;
          if (body.Substring(start, end - start).IndexOf("# SIG # Begin signature block", StringComparison.OrdinalIgnoreCase) == 0) signature = true;
          if (signature) continue;
          if (At(body, start, "<#") && end - start >= 4 && At(body, end - 2, "#>")) AddLines(body, start + 2, end - 2, collected, false);
          else if (body[start] == '#' && !At(body, start, "<#")) AddLines(body, start + 1, end, collected, false);
        }
        // Some runtimes include nested tokens. Keep only disjoint original spans.
        var ordered = collected.OrderBy(span => span.Start); var previousEnd = -1;
        foreach (var span in ordered) if (span.Start >= previousEnd) { result.Add(span); previousEnd = span.Start + span.Length; }
        return true;
      }
      catch (Exception error) when (error is TargetInvocationException || error is MemberAccessException || error is ArgumentException
        || error is TypeLoadException || error is InvalidCastException || error is NullReferenceException || error is TypeInitializationException) { result.Clear(); return false; }
    }

    private static void ScanHash(string body, List<Span> result, string kind)
    {
      var powershell = kind == "powershell"; var python = kind == "python"; var shell = kind == "shell";
      for (var i = 0; i < body.Length;) {
        var ch = body[i];
        if (powershell && At(body, i, "<#") && HashBoundary(body, i, true)) {
          var closing = body.IndexOf("#>", i + 2, StringComparison.Ordinal); if (closing < 0) return;
          AddLines(body, i + 2, closing, result, false); i = closing + 2; continue;
        }
        if (powershell && ch == '@' && i + 1 < body.Length && QuoteKind(body[i + 1]) != '\0') {
          var after = i + 2;
          while (after < body.Length && (body[after] == ' ' || body[after] == '\t')) after++;
          if (after < body.Length && IsNewline(body[after])) {
            var quote = body[i + 1]; var line = NextLine(body, after); var found = false;
            while (line < body.Length) {
              if (body[line] == quote && line + 1 < body.Length && body[line + 1] == '@') { i = line + 2; found = true; break; }
              line = NextLine(body, LineEnd(body, line));
            }
            if (!found) return; continue;
          }
        }
        var normalizedQuote = powershell ? QuoteKind(ch) : ch == '\'' || ch == '"' ? ch : '\0';
        if (normalizedQuote != '\0') {
          if (python && PythonFormattedPrefix(body, i)) return;
          if ((shell || powershell) && normalizedQuote == '"' && ContainsExpansion(body, i, powershell ? '`' : '\\')) return;
          var triple = python && At(body, i, new string(ch, 3));
          var end = SkipQuote(body, i, ch, powershell ? '`' : '\\', powershell, triple, shell && ch == '\'' && !(i > 0 && body[i - 1] == '$'), powershell || shell || triple);
          if (end < 0) return; i = end; continue;
        }
        if ((powershell && ch == '`') || (!powershell && ch == '\\')) { i = Math.Min(body.Length, i + 2); continue; }
        if (powershell && At(body, i, "--%") && HashBoundary(body, i, true)) { i = LineEnd(body, i); continue; }
        // Shell expansions/heredocs require a complete shell parser. Protect the
        // remainder instead of mistaking their literal # text for comments.
        if (shell && (At(body, i, "$(") || At(body, i, "${") || ch == '`' || At(body, i, "<<"))) return;
        if (ch == '#' && (python || HashBoundary(body, i, powershell))) {
          var end = LineEnd(body, i);
          if (powershell && AtIgnoreCase(body, i, "# SIG # Begin signature block")) return;
          AddLines(body, i + 1, end, result, false); i = end; continue;
        }
        i++;
      }
    }
    private static bool HashBoundary(string body, int start, bool powershell)
    {
      if (start == 0 || char.IsWhiteSpace(body[start - 1]) || body[start - 1] == '\uFEFF') return true;
      return (powershell ? ")}]'\";" : ";|&()").IndexOf(body[start - 1]) >= 0;
    }
    private static char QuoteKind(char ch) => ch == '\'' || ch == '\u2018' || ch == '\u2019' ? '\''
      : ch == '"' || ch == '\u201c' || ch == '\u201d' ? '"' : '\0';
    private static bool PythonFormattedPrefix(string body, int quote)
    {
      var start = quote;
      while (start > 0 && char.IsLetter(body[start - 1])) start--;
      var prefix = body.Substring(start, quote - start);
      return prefix.IndexOf('f') >= 0 || prefix.IndexOf('F') >= 0 || prefix.IndexOf('t') >= 0 || prefix.IndexOf('T') >= 0;
    }
    private static bool ContainsExpansion(string body, int start, char escape)
    {
      for (var i = start + 1; i < body.Length; i++) {
        if (body[i] == escape) { i++; continue; }
        if (QuoteKind(body[i]) == '"') return false;
        if (At(body, i, "$(") || At(body, i, "${") || body[i] == '`') return true;
      }
      return false;
    }
    private static int SkipQuote(string body, int start, char quote, char escape, bool doubled, bool triple, bool literal, bool allowMultiline)
    {
      var width = triple ? 3 : 1;
      for (var i = start + width; i < body.Length; i++) {
        if (!literal && body[i] == escape && !(doubled && QuoteKind(quote) == '\'')) {
          i += i + 2 < body.Length && body[i + 1] == '\r' && body[i + 2] == '\n' ? 2 : 1; continue;
        }
        if (!allowMultiline && IsNewline(body[i])) return -1;
        var matches = doubled ? QuoteKind(body[i]) == QuoteKind(quote) : body[i] == quote;
        if (!matches) continue;
        if (triple) { if (At(body, i, new string(quote, 3))) return i + 3; continue; }
        if (doubled && i + 1 < body.Length && QuoteKind(body[i + 1]) == QuoteKind(quote)) { i++; continue; }
        return i + 1;
      }
      return -1;
    }

    private static void ScanSlash(string body, List<Span> result, string kind)
    {
      var bsl = kind == "bsl"; var javascript = kind == "javascript";
      for (var i = 0; i < body.Length;) {
        if (!bsl && !javascript && body[i] == '#' && FirstOnLine(body, i)) {
          var end = LineEnd(body, i); while (kind == "c" && end > i && body[end - 1] == '\\' && end < body.Length) end = LineEnd(body, NextLine(body, end));
          i = end; continue;
        }
        if (At(body, i, "//")) {
          if (!bsl && At(body, i, "///")) { i = LineEnd(body, i); continue; }
          var start = i + 2; var end = LineEnd(body, i);
          while (kind == "c" && end > start && body[end - 1] == '\\' && end < body.Length) end = LineEnd(body, NextLine(body, end));
          AddLines(body, start, end, result, false, kind == "c"); i = end; continue;
        }
        if (!bsl && At(body, i, "/*")) {
          var closing = body.IndexOf("*/", i + 2, StringComparison.Ordinal); if (closing < 0) return;
          AddLines(body, i + 2, closing, result, true); i = closing + 2; continue;
        }
        // A regex literal versus division needs JS parser context. The rest of
        // this block stays protected, including strings/templates after a slash.
        if (javascript && body[i] == '/') return;
        if (javascript && body[i] == '`') {
          var end = SkipJsTemplate(body, i); if (end < 0) return; i = end; continue;
        }
        if (kind == "csharp" && (At(body, i, "$\"") || At(body, i, "$@\"") || At(body, i, "@$\""))) return;
        if (kind == "csharp" && At(body, i, "@\"")) {
          var end = SkipQuote(body, i + 1, '"', '\\', true, false, true, true); if (end < 0) return; i = end; continue;
        }
        if (kind == "c" && (At(body, i, "R\"") || At(body, i, "u8R\"") || At(body, i, "uR\"") || At(body, i, "UR\"") || At(body, i, "LR\""))) {
          var quote = body.IndexOf('"', i); var opening = body.IndexOf('(', quote + 1);
          if (opening < 0 || opening - quote > 17) return;
          var delimiter = body.Substring(quote + 1, opening - quote - 1);
          var closing = body.IndexOf(")" + delimiter + "\"", opening + 1, StringComparison.Ordinal); if (closing < 0) return;
          i = closing + delimiter.Length + 2; continue;
        }
        if (body[i] == '"' || body[i] == '\'') {
          var triple = kind == "csharp" && At(body, i, "\"\"\"");
          if (triple) return; // Modern raw strings have variable delimiter widths.
          var end = SkipQuote(body, i, body[i], '\\', bsl, false, bsl, bsl);
          if (end < 0) return; i = end; continue;
        }
        // C/C++ translation phases join escaped physical newlines before
        // recognizing comments. Ambiguous split delimiters remain protected.
        if (kind == "c" && body[i] == '\\' && i + 1 < body.Length && IsNewline(body[i + 1])) return;
        i++;
      }
    }
    private static int SkipJsTemplate(string body, int start)
    {
      for (var i = start + 1; i < body.Length; i++) {
        if (body[i] == '\\') { i++; continue; }
        if (At(body, i, "${")) return -1;
        if (body[i] == '`') return i + 1;
      }
      return -1;
    }
    private static bool FirstOnLine(string body, int start)
    {
      for (var i = start - 1; i >= 0 && !IsNewline(body[i]); i--) if (body[i] != ' ' && body[i] != '\t' && body[i] != '\uFEFF') return false;
      return true;
    }

    private static void ScanAnnotations(string body, List<Span> result)
    {
      for (var line = 0; line < body.Length;) {
        var end = LineEnd(body, line); var text = body.Substring(line, end - line);
        var tree = TreeNote.Match(text);
        if (tree.Success) { var note = tree.Groups["note"]; AddLines(body, line + note.Index, line + note.Index + note.Length, result, false); }
        else if (PseudoCall.IsMatch(text)) {
          var depth = 0; var closed = false; var quote = '\0'; var escaped = false;
          for (var i = line; i < end; i++) {
            var ch = body[i];
            if (quote != '\0') {
              if (escaped) escaped = false; else if (ch == '\\') escaped = true; else if (ch == quote) quote = '\0';
              continue;
            }
            if (ch == '"' || ch == '\'') { quote = ch; continue; }
            if (ch == '(') { depth++; closed = false; }
            else if (ch == ')') { depth--; if (depth < 0) break; closed = depth == 0; }
            else if (ch == '#' && closed && depth == 0 && i > line && char.IsWhiteSpace(body[i - 1])) { AddLines(body, i + 1, end, result, false); break; }
          }
        }
        line = NextLine(body, end);
      }
    }
    private static void AddLines(string body, int start, int end, List<Span> result, bool stars, bool continuations = false)
    {
      while (start < end) {
        var stop = Math.Min(end, LineEnd(body, start)); var first = start; var last = stop;
        while (first < last && char.IsWhiteSpace(body[first])) first++;
        while (last > first && char.IsWhiteSpace(body[last - 1])) last--;
        if (continuations && stop < body.Length && IsNewline(body[stop]) && last == stop && last > first && body[last - 1] == '\\') {
          last--; while (last > first && char.IsWhiteSpace(body[last - 1])) last--;
        }
        if (stars && first < last && body[first] == '*') { first++; while (first < last && char.IsWhiteSpace(body[first])) first++; }
        if (first < last && !Directive.IsMatch(body.Substring(first, last - first))) result.Add(new Span(first, last - first));
        start = NextLine(body, stop);
      }
    }
    private static int LineEnd(string body, int start)
    {
      while (start < body.Length && !IsNewline(body[start])) start++;
      return start;
    }
    private static int NextLine(string body, int end)
    {
      if (end < body.Length && body[end] == '\r') end++;
      if (end < body.Length && (body[end] == '\n' || body[end] == '\u0085' || body[end] == '\u2028' || body[end] == '\u2029')) end++;
      return end;
    }
    private static bool IsNewline(char ch) => ch == '\r' || ch == '\n' || ch == '\u0085' || ch == '\u2028' || ch == '\u2029';
    private static bool At(string body, int position, string token) => position >= 0 && position + token.Length <= body.Length
      && string.CompareOrdinal(body, position, token, 0, token.Length) == 0;
    private static bool AtIgnoreCase(string body, int position, string token) => position >= 0 && position + token.Length <= body.Length
      && string.Compare(body, position, token, 0, token.Length, StringComparison.OrdinalIgnoreCase) == 0;
  }
}
