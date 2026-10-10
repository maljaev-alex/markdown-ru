using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AnotherMarkdown.Translation
{
  // Editable prose is returned separately from code. Only these source ranges may be
  // substituted; code, delimiters, container prefixes and line endings stay local.
  internal static class CodeBlockAnnotations
  {
    internal sealed class Span
    {
      internal int Start, Length;
    }

    internal static List<Span> Find(MarkdownStructure structure, CodeBlock block, int start, int length)
    {
      var body = new StringBuilder(); var offsets = new List<int>();
      for (var i = 0; i < block.Lines.Count; i++) {
        var slice = block.Lines.Lines[i].Slice;
        // Tab expansion may create a synthetic slice. Do not guess its source location.
        if (slice.Length > 0 && !ReferenceEquals(slice.Text, structure.ParseSource)) return new List<Span>();
        for (var j = slice.Start; j <= slice.End; j++) {
          body.Append(slice.Text[j]); offsets.Add(j + structure.Offset - start);
        }
        body.Append('\n'); offsets.Add(-1);
      }
      var text = body.ToString();
      var language = (block as FencedCodeBlock)?.Info?.Trim().Split(new[] { ' ', '\t', '{' }, 2)[0].ToLowerInvariant() ?? "";
      // Even translating an earlier comment invalidates an Authenticode signature.
      if ((language == "powershell" || language == "pwsh" || language == "ps1" || language == "ps") &&
          text.IndexOf("# SIG # Begin signature block", StringComparison.OrdinalIgnoreCase) >= 0) return new List<Span>();
      // C/C++ splice source lines before recognizing comments (including whitespace
      // in newer C++ and legacy trigraph backslashes). Keep ambiguous blocks intact.
      if ((language == "c" || language == "cpp" || language == "c++") &&
          Regex.IsMatch(text, @"(?:\\|\?\?/)[ \t]*(?:\r\n|\r|\n)", RegexOptions.CultureInvariant)) return new List<Span>();
      var candidates = language == "md" || language == "markdown"
        ? MarkdownSpans(text)
        : CodeCommentSpans.Find(text, language).Select(s => new Span { Start = s.Start, Length = s.Length }).ToList();
      var result = new List<Span>();
      foreach (var candidate in candidates) {
        var a = candidate.Start; var b = a + candidate.Length;
        if (a < 0 || b > offsets.Count || a == b || offsets[a] < 0 || offsets[b - 1] >= length) continue;
        if (Enumerable.Range(a, b - a).Any(p => offsets[p] != offsets[a] + p - a)) continue;
        result.Add(new Span { Start = offsets[a], Length = candidate.Length });
      }
      return result.OrderBy(s => s.Start).ToList();
    }

    private static List<Span> MarkdownSpans(string body)
    {
      var structure = new MarkdownStructure(body); var result = new List<Span>();
      foreach (var literal in structure.Nodes.OfType<LiteralInline>()) {
        // Reference labels are identifiers as well as display text. Leave those alone.
        if (Parents(literal).OfType<LinkInline>().Any(l => l.Reference != null || l.IsAutoLink)) continue;
        var a = literal.Span.Start + structure.Offset; var b = literal.Span.End + structure.Offset + 1;
        if (a < 0 || b <= a || b > body.Length) continue;
        // Keep each line independent: translations can never add or remove source lines.
        for (var p = a; p < b;) {
          var end = p; while (end < b && body[end] != '\r' && body[end] != '\n') end++;
          var left = p; var right = end;
          while (left < right && char.IsWhiteSpace(body[left])) left++;
          while (right > left && char.IsWhiteSpace(body[right - 1])) right--;
          if (right > left && body.Substring(left, right - left).Any(char.IsLetter))
            result.Add(new Span { Start = left, Length = right - left });
          p = end + 1;
        }
      }
      return result.OrderBy(s => s.Start).ToList();
    }

    private static IEnumerable<Inline> Parents(Inline item)
    { for (var parent = item.Parent; parent != null; parent = parent.Parent) yield return parent; }

    internal static string[] Scaffold(string original, IList<Span> spans)
    {
      var pieces = new List<string>(); var cursor = 0;
      foreach (var span in spans) { pieces.Add(original.Substring(cursor, span.Start - cursor)); cursor = span.Start + span.Length; }
      pieces.Add(original.Substring(cursor)); return pieces.ToArray();
    }
  }

  internal sealed class AnnotationText
  {
    // Paths, code quotations, placeholders, variables and common command names are
    // immutable even when they appear inside a human-readable comment.
    private static readonly Regex Technical = new Regex(
      @"(?:https?|ftp)://[^\s<>""'`]+|<[^<>\r\n]+>|" +
      @"(?:[A-Za-z]:\\|\\\\|\$[A-Za-z_][\w:]*[\\/])[^\s""'`<>|;,]+|" +
      @"(?<![\p{L}\p{N}])(?:\.?\.?/)?[\w.@*-]+(?:[/\\][\w.@*<>-]+)+|" +
      @"(?<![\p{L}\p{N}])(?:[\w-]+\.)+(?:mdc?|json|ya?ml|toml|env|zip|exe|ps1|cmd|bat|dll|ru|com|org|net|io)(?:\b|$)|" +
      @"disk\.yandex\.\*|\$[A-Za-z_][\w:]*|<[A-Z_]+>|\bYYYY-MM-DD\b|" +
      @"\b[A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_]+\b|\b[A-Za-z]*[a-z][A-Z][A-Za-z0-9]*\b|(?<![\w.])\.env\b|" +
      @"\b[A-Z][a-z]+-[A-Z][A-Za-z0-9]*\b|\\[\\`*_{}\[\]()#+.!<>|-]|&(?:#\d+|#x[0-9a-fA-F]+|[A-Za-z]+);",
      RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private readonly List<string> originals = new List<string>();
    private readonly string prefix;
    internal string Target { get; }

    internal AnnotationText(string text)
    {
      do { prefix = "AM_TERM_" + Guid.NewGuid().ToString("N").Substring(0, 12) + "_"; } while (text.Contains(prefix));
      var ranges = Technical.Matches(text).Cast<Match>().Select(m => new CodeBlockAnnotations.Span { Start = m.Index, Length = m.Length }).ToList();
      // CommonMark code spans require matching delimiter-run lengths. A regex would
      // expose the tail of ``code ` with a tick`` as editable prose.
      var structure = new MarkdownStructure(text);
      foreach (var node in structure.Nodes.Where(n => n is CodeInline || n is CodeBlock)) {
        var start = node.Span.Start + structure.Offset; var length = structure.SourceEnd(node) - start + 1;
        if (start >= 0 && length > 0 && start + length <= text.Length) ranges.Add(new CodeBlockAnnotations.Span { Start = start, Length = length });
      }
      var result = new StringBuilder(); var cursor = 0;
      foreach (var range in ranges.OrderBy(r => r.Start).ThenByDescending(r => r.Length)) {
        if (range.Start < cursor) continue;
        result.Append(text, cursor, range.Start - cursor).Append(prefix).Append(originals.Count).Append("_END");
        originals.Add(text.Substring(range.Start, range.Length)); cursor = range.Start + range.Length;
      }
      Target = result.Append(text, cursor, text.Length - cursor).ToString();
    }

    internal string Restore(string text)
    {
      if (string.IsNullOrWhiteSpace(text) || text.IndexOfAny(new[] { '\r', '\n', '\0', '\u0085', '\u2028', '\u2029' }) >= 0)
        throw new MarkdownProtectionException("Модель изменила строки комментария.");
      text = text.Trim(); var result = new StringBuilder(); var cursor = 0;
      for (var i = 0; i < originals.Count; i++) {
        var marker = prefix + i + "_END"; var position = text.IndexOf(prefix, cursor, StringComparison.Ordinal);
        if (position < 0 || !text.Substring(position).StartsWith(marker, StringComparison.Ordinal))
          throw new MarkdownProtectionException("Модель изменила путь или идентификатор в комментарии.");
        result.Append(text, cursor, position - cursor).Append(originals[i]); cursor = position + marker.Length;
      }
      if (text.IndexOf(prefix, cursor, StringComparison.Ordinal) >= 0)
        throw new MarkdownProtectionException("Модель повторила идентификатор в комментарии.");
      return result.Append(text, cursor, text.Length - cursor).ToString();
    }
  }
}
