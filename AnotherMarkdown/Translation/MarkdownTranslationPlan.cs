using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AnotherMarkdown.Translation
{
  public sealed class TranslationChunk
  {
    public int Index { get; }
    public string Markdown { get; }
    public string SeparatorAfter { get; }
    public string PrefixBefore { get; }
    public string ContextBefore { get; }
    public string ContextAfter { get; }
    public string DocumentContext { get; }
    internal TranslationChunk(int index, string markdown, string separator, string before, string after, string documentContext, string prefix = "")
    { Index = index; Markdown = markdown; SeparatorAfter = separator; ContextBefore = before; ContextAfter = after; DocumentContext = documentContext; PrefixBefore = prefix; }
  }

  public static class MarkdownTranslationPlan
  {
    public const int MinimumSplitCharacters = 8000;
    private const int ContextCharacters = 1200;
    private static readonly Regex Fence = new Regex(@"^[ \t]*(?:>[ \t]*)*(?:(?:[-+*]|[0-9]{1,9}[.)])[ \t]+)?(?<marker>`{3,}|~{3,})(?<rest>[^\r\n]*)$");
    private static readonly Regex List = new Regex(@"^ {0,3}(?:[-+*]|[0-9]{1,9}[.)])(?:[ \t]+|$)");
    private static readonly Regex Heading = new Regex(@"^ {0,3}#{1,6}(?:[ \t]+|$)");
    private static readonly Regex HtmlStart = new Regex(@"^ {0,3}<(?<tag>address|article|aside|blockquote|body|caption|center|colgroup|dd|details|dialog|dir|div|dl|dt|fieldset|figcaption|figure|footer|form|h[1-6]|head|header|hr|html|iframe|legend|li|link|main|menu|nav|ol|p|pre|script|section|style|summary|table|tbody|td|textarea|tfoot|th|thead|title|tr|ul)(?:[ \t>/]|$)", RegexOptions.IgnoreCase);
    private static readonly Regex Prefix = new Regex(@"\A(?:\uFEFF)?(?:[ \t]*(?:\r\n|\r|\n))+");

    public static IReadOnlyList<TranslationChunk> Create(string markdown, int parallelRequests)
    {
      if (markdown == null) throw new ArgumentNullException(nameof(markdown));
      if (parallelRequests < 1 || parallelRequests > 8) throw new ArgumentOutOfRangeException(nameof(parallelRequests));
      if (parallelRequests == 1 || markdown.Length < MinimumSplitCharacters)
        return new[] { new TranslationChunk(0, markdown, "", "", "", "") };
      var boundaries = SafeBoundaries(markdown);
      var target = Math.Max(4000, Math.Min(12000, (markdown.Length + parallelRequests - 1) / parallelRequests));
      var cuts = new List<int> { 0 };
      foreach (var boundary in boundaries)
        if (boundary - cuts[cuts.Count - 1] >= target && boundary < markdown.Length &&
            !string.IsNullOrWhiteSpace(markdown.Substring(cuts[cuts.Count - 1], boundary - cuts[cuts.Count - 1]).TrimStart('\uFEFF'))) cuts.Add(boundary);
      // Avoid a tiny last request; one atomic Markdown block may exceed the target size.
      if (cuts.Count > 1 && markdown.Length - cuts[cuts.Count - 1] < 2000) cuts.RemoveAt(cuts.Count - 1);
      cuts.Add(markdown.Length);
      if (cuts.Count == 2) return new[] { new TranslationChunk(0, markdown, "", "", "", "") };
      var documentContext = Outline(markdown);
      var chunks = new List<TranslationChunk>();
      for (var i = 0; i < cuts.Count - 1; i++) {
        var start = cuts[i]; var end = cuts[i + 1];
        var source = markdown.Substring(start, end - start);
        var prefix = i == 0 ? Prefix.Match(source).Value : "";
        var separator = TrailingSeparator(source);
        chunks.Add(new TranslationChunk(i, source.Substring(prefix.Length, source.Length - prefix.Length - separator.Length), separator,
          SliceContext(markdown, Math.Max(0, start - ContextCharacters), start),
          SliceContext(markdown, end, Math.Min(markdown.Length, end + ContextCharacters)), documentContext, prefix));
      }
      return chunks;
    }

    private static string SliceContext(string text, int start, int end)
    {
      if (start > 0 && start < text.Length && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1])) start++;
      if (end > start && end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end--;
      return text.Substring(start, end - start);
    }

    private static string Outline(string markdown)
    {
      var outline = new StringBuilder();
      foreach (var line in Lines(markdown)) {
        if (!Heading.IsMatch(line.Text) && !Regex.IsMatch(line.Text, @"^ {0,3}\[[^\r\n\]]+\]:")) continue;
        if (outline.Length + line.Text.Length + 1 > 2400) break;
        outline.Append(line.Text).Append('\n');
      }
      return outline.ToString();
    }

    private sealed class Line
    {
      public int Start;
      public string Text;
    }

    private static List<Line> Lines(string markdown)
    {
      var lines = new List<Line>();
      var start = 0;
      while (start < markdown.Length) {
        var end = markdown.IndexOfAny(new[] { '\r', '\n' }, start);
        if (end < 0) end = markdown.Length;
        lines.Add(new Line { Start = start, Text = markdown.Substring(start, end - start) });
        start = end;
        if (start < markdown.Length && markdown[start++] == '\r' && start < markdown.Length && markdown[start] == '\n') start++;
      }
      return lines;
    }

    private static IEnumerable<int> SafeBoundaries(string markdown)
    {
      var lines = Lines(markdown);
      var frontMatter = lines.Count > 0 && lines[0].Text.TrimStart('\uFEFF') == "---";
      var fenceCharacter = '\0'; var fenceLength = 0; var fenceBaseIndent = 0; var fenceQuoteDepth = 0;
      string htmlTag = null, htmlEnd = null, mathEnd = null;
      var htmlDepth = 0; var containerDepth = 0;
      var inList = false; var inQuote = false; var inIndentedCode = false; var listContentIndent = 0;
      var lastNonBlank = "";
      for (var i = 0; i < lines.Count; i++) {
        var text = lines[i].Text; var trimmed = text.Trim();
        if (frontMatter) { if (i > 0 && (text == "---" || text == "...")) frontMatter = false; continue; }
        if (fenceCharacter != '\0') {
          if (ClosesFence(text, fenceCharacter, fenceLength, fenceBaseIndent, fenceQuoteDepth)) fenceCharacter = '\0';
          lastNonBlank = text; continue;
        }
        if (htmlEnd != null) { if (text.Contains(htmlEnd)) htmlEnd = null; lastNonBlank = text; continue; }
        if (htmlTag != null) {
          htmlDepth += HtmlDepth(text, htmlTag);
          if (htmlDepth <= 0) htmlTag = null;
          lastNonBlank = text; continue;
        }
        if (mathEnd != null) { if (trimmed == mathEnd) mathEnd = null; lastNonBlank = text; continue; }
        // A list/quote may start with a protected code/HTML block instead of ordinary prose.
        if (List.IsMatch(text)) {
          inList = true; var itemPrefix = List.Match(text).Value;
          listContentIndent = IndentationColumns(itemPrefix) + (itemPrefix.EndsWith(" ", StringComparison.Ordinal) || itemPrefix.EndsWith("\t", StringComparison.Ordinal) ? 0 : 1);
        }
        if (Regex.IsMatch(text, @"^ {0,3}(?:\[\^[^\]]+\]:|:[ \t]+)")) inList = true;
        if (Regex.IsMatch(text, @"^ {0,3}>")) inQuote = true;
        if (!inList && LeadingIndent(text) >= 4) inIndentedCode = true;
        var opening = Fence.Match(text);
        if (opening.Success && (opening.Groups["marker"].Value[0] != '`' || !opening.Groups["rest"].Value.Contains("`")) &&
            FenceContainer(text, opening.Groups["marker"].Index, inList, listContentIndent, out fenceBaseIndent, out fenceQuoteDepth)) {
          fenceCharacter = opening.Groups["marker"].Value[0]; fenceLength = opening.Groups["marker"].Length;
          if (fenceBaseIndent > 0) { inList = true; listContentIndent = fenceBaseIndent; }
          lastNonBlank = text; continue;
        }
        if (trimmed.StartsWith("<!--", StringComparison.Ordinal) && !trimmed.Contains("-->")) { htmlEnd = "-->"; continue; }
        if (trimmed.StartsWith("<![CDATA[", StringComparison.Ordinal) && !trimmed.Contains("]]>")) { htmlEnd = "]]>"; continue; }
        if (trimmed.StartsWith("<?", StringComparison.Ordinal) && !trimmed.Contains("?>")) { htmlEnd = "?>"; continue; }
        if (Regex.IsMatch(trimmed, @"^<![A-Z]") && !trimmed.Contains(">")) { htmlEnd = ">"; continue; }
        var html = HtmlStart.Match(text);
        if (html.Success) {
          var tag = html.Groups["tag"].Value.ToLowerInvariant();
          if (tag != "hr" && tag != "link" && !trimmed.EndsWith("/>", StringComparison.Ordinal)) {
            htmlDepth = text.IndexOf('>') < 0 ? 1 : HtmlDepth(text, tag); if (htmlDepth > 0) htmlTag = tag;
          }
          lastNonBlank = text; continue;
        }
        if (trimmed == "$$" || trimmed == @"\[") { mathEnd = trimmed == "$$" ? "$$" : @"\]"; lastNonBlank = text; continue; }
        if (Regex.IsMatch(trimmed, @"^:{3,}")) {
          if (Regex.IsMatch(trimmed, @"^:{3,}$")) containerDepth = Math.Max(0, containerDepth - 1); else containerDepth++;
          lastNonBlank = text; continue;
        }
        if (IsBlank(text)) {
          var next = i + 1;
          while (next < lines.Count && IsBlank(lines[next].Text)) next++;
          if (next == lines.Count) break;
          var following = lines[next].Text;
          var indented = LeadingIndent(following) >= 4;
          var headingNeedsBody = Heading.IsMatch(lastNonBlank) || Regex.IsMatch(lastNonBlank, @"^ {0,3}(?:=+|-+)[ \t]*$");
          if (containerDepth == 0 && !headingNeedsBody && !Regex.IsMatch(following, @"^ {0,3}:[ \t]+") && !(inList && (following.StartsWith(" ", StringComparison.Ordinal) || following.StartsWith("\t", StringComparison.Ordinal) || List.IsMatch(following))) &&
              !(inQuote && Regex.IsMatch(following, @"^ {0,3}>")) && !(inIndentedCode && indented)) {
            yield return lines[next].Start;
            inList = inQuote = inIndentedCode = false;
          }
          i = next - 1; continue;
        }
        lastNonBlank = text;
      }
      yield return markdown.Length;
    }

    private static int HtmlDepth(string text, string tag)
    {
      var depth = 0; var position = 0;
      while ((position = text.IndexOf('<', position)) >= 0) {
        var start = position + 1;
        var closing = start < text.Length && text[start] == '/'; if (closing) start++;
        var endName = start + tag.Length;
        if (endName > text.Length || string.Compare(text, start, tag, 0, tag.Length, StringComparison.OrdinalIgnoreCase) != 0 ||
            (endName < text.Length && text[endName] != ' ' && text[endName] != '\t' && text[endName] != '/' && text[endName] != '>')) { position++; continue; }
        var end = text.IndexOf('>', endName);
        if (end < 0) { if (!closing) depth++; break; }
        if (closing) depth--; else if (text[end - 1] != '/') depth++;
        position = end + 1;
      }
      return depth;
    }

    private static int IndentationColumns(string text)
    {
      var columns = 0;
      foreach (var character in text) columns = character == '\t' ? columns + 4 - columns % 4 : columns + 1;
      return columns;
    }

    private static int LeadingIndent(string text)
    {
      var characters = 0; while (characters < text.Length && (text[characters] == ' ' || text[characters] == '\t')) characters++;
      return IndentationColumns(text.Substring(0, characters));
    }

    private static bool StripQuotes(string text, int depth, out string logical)
    {
      var position = 0;
      for (var i = 0; i < depth; i++) {
        var start = position;
        while (position < text.Length && (text[position] == ' ' || text[position] == '\t')) position++;
        if (IndentationColumns(text.Substring(start, position - start)) > 3 || position == text.Length || text[position++] != '>') { logical = ""; return false; }
        if (position < text.Length && (text[position] == ' ' || text[position] == '\t')) position++;
      }
      logical = text.Substring(position); return true;
    }

    private static bool FenceContainer(string text, int markerStart, bool inList, int listIndent, out int baseIndent, out int quoteDepth)
    {
      quoteDepth = text.Substring(0, markerStart).Count(c => c == '>'); baseIndent = 0;
      if (!StripQuotes(text, quoteDepth, out var logical)) return false;
      var item = Regex.Match(logical, @"^[ \t]*(?:[-+*]|[0-9]{1,9}[.)])[ \t]+");
      var indent = LeadingIndent(logical);
      if (item.Success) {
        if (!inList && indent > 3) return false;
        baseIndent = IndentationColumns(item.Value); return true;
      }
      if (inList && indent >= listIndent) baseIndent = listIndent;
      return indent - baseIndent <= 3;
    }

    private static bool ClosesFence(string text, char marker, int length, int baseIndent, int quoteDepth)
    {
      if (!StripQuotes(text, quoteDepth, out var logical)) return false;
      var indent = LeadingIndent(logical);
      if (indent < baseIndent || indent - baseIndent > 3) return false;
      var position = 0; while (position < logical.Length && (logical[position] == ' ' || logical[position] == '\t')) position++;
      var start = position; while (position < logical.Length && logical[position] == marker) position++;
      return position - start >= length && IsBlank(logical.Substring(position));
    }

    private static bool IsBlank(string text) => text.All(c => c == ' ' || c == '\t');

    private static string TrailingSeparator(string text)
    {
      var cursor = text.Length;
      while (cursor > 0 && (text[cursor - 1] == ' ' || text[cursor - 1] == '\t')) cursor--;
      if (cursor == 0) return text;
      if (text[cursor - 1] != '\r' && text[cursor - 1] != '\n') return "";
      var start = cursor;
      while (cursor > 0 && (text[cursor - 1] == '\r' || text[cursor - 1] == '\n')) {
        var newline = text[--cursor];
        if (newline == '\n' && cursor > 0 && text[cursor - 1] == '\r') cursor--;
        start = cursor;
        var blank = cursor;
        while (blank > 0 && (text[blank - 1] == ' ' || text[blank - 1] == '\t')) blank--;
        if (blank == 0) { start = 0; break; }
        if (text[blank - 1] != '\r' && text[blank - 1] != '\n') break;
        cursor = blank;
      }
      return text.Substring(start);
    }

  }
}
