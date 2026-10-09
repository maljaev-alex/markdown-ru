using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.Footnotes;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Syntax;

namespace AnotherMarkdown.Translation
{
  // Parse the original source, never render it back: spans retain its exact bytes/line endings.
  // This is a structural guard for translation, independent of the preview's enabled extensions.
  internal sealed class MarkdownStructure
  {
    private static readonly object FootnotesKey = new object(), CaptureKey = new object();
    private static readonly MarkdownPipeline Pipeline = BuildPipeline(false), YamlPipeline = BuildPipeline(true);
    internal readonly MarkdownDocument Document;
    internal readonly int Offset;
    internal readonly bool HasFrontMatter;
    internal readonly HashSet<int> HeadingStarts = new HashSet<int>();
    private readonly Dictionary<int, Block> blocks = new Dictionary<int, Block>();
    private readonly Dictionary<int, Block> previousBlocks = new Dictionary<int, Block>();
    private readonly HashSet<int> safeStarts = new HashSet<int>();
    private readonly string source;
    private readonly List<int> lineStarts = new List<int> { 0 };
    internal IEnumerable<MarkdownObject> Nodes => Document.Descendants().Concat(SourceFootnotes.SelectMany(n =>
      new[] { (MarkdownObject)n }.Concat(n.Descendants()))).Distinct();
    private IEnumerable<Footnote> SourceFootnotes => Document.GetData(FootnotesKey) as Footnote[] ?? new Footnote[0];

    internal MarkdownStructure(string markdown)
    {
      source = markdown;
      for (var i = 0; i < markdown.Length; i++) {
        if (markdown[i] == '\r') { if (i + 1 < markdown.Length && markdown[i + 1] == '\n') i++; lineStarts.Add(i + 1); }
        else if (markdown[i] == '\n') lineStarts.Add(i + 1);
      }
      Offset = markdown.StartsWith("\uFEFF", StringComparison.Ordinal) ? 1 : 0;
      var parseSource = Offset == 0 ? markdown : markdown.Substring(Offset);
      HasFrontMatter = LooksLikeFrontMatter(parseSource);
      Document = Markdown.Parse(parseSource, HasFrontMatter ? YamlPipeline : Pipeline);
      Block previous = null; var protectedThrough = -1;
      // Definition groups are global indexes with aggregate (overlapping) spans,
      // not source containers. Their individual definitions remain in Nodes/Outline.
      var roots = Document.Where(b => !(b is FootnoteGroup) && !(b is LinkReferenceDefinitionGroup)).Concat(SourceFootnotes.Cast<Block>())
        .Where(b => b.Span.Start >= 0 && b.Span.End >= b.Span.Start)
        .GroupBy(b => LineStart(b.Span.Start + Offset)).OrderBy(g => g.Key);
      foreach (var group in roots) {
        var start = group.Key;
        var block = group.OrderBy(b => b is ParagraphBlock ? 1 : 0).ThenByDescending(b => b.Span.End).First();
        // Link definitions may be grouped by the parser; only concrete source blocks
        // delimit translation units. The older extension guard is intersected with this map.
        blocks[start] = block;
        previousBlocks[start] = previous;
        if (start > protectedThrough) safeStarts.Add(start);
        protectedThrough = Math.Max(protectedThrough, group.Max(b => b.Span.End + Offset));
        if (block is HeadingBlock) HeadingStarts.Add(start);
        previous = block;
      }
    }

    internal bool IsBoundary(int position) => safeStarts.Contains(position);
    internal int SourceEnd(MarkdownObject node)
    {
      // Markdig leaves Span.End near the opening marker for an unclosed fence.
      // StringLine.Line still records the exact last consumed source line, including
      // code blank lines and a container ending before EOF. Do not infer it from text.
      if (node is FencedCodeBlock fence && fence.ClosingFencedCharCount == 0) {
        var lastLine = fence.Lines.Count == 0 ? fence.Line : fence.Lines.Lines[fence.Lines.Count - 1].Line;
        return lastLine + 1 < lineStarts.Count ? lineStarts[lastLine + 1] - 1 : source.Length - 1;
      }
      return node.Span.End + Offset;
    }
    internal bool BetweenParagraphs(int position) => blocks.TryGetValue(position, out var block)
      && block is ParagraphBlock && previousBlocks.TryGetValue(position, out var before) && before is ParagraphBlock;

    internal string Outline()
    {
      var text = new StringBuilder();
      foreach (var node in Nodes.Where(n => n is HeadingBlock || n is LinkReferenceDefinition || n is Footnote).OrderBy(n => n.Span.Start)) {
        // Only real headings reach here; examples inside code/HTML cannot become outline entries.
        var start = node.Span.Start + Offset; var length = node.Span.End - node.Span.Start + 1;
        if (start < 0 || length < 0 || start + length > source.Length) continue;
        if (text.Length + length + 1 > 2400) continue;
        text.Append(source, start, length).Append('\n');
      }
      return text.ToString();
    }

    private int LineStart(int position)
    {
      while (position > 0 && source[position - 1] != '\r' && source[position - 1] != '\n') position--;
      return position;
    }

    private static bool LooksLikeFrontMatter(string text)
    {
      // A leading thematic break is valid CommonMark too. Treat it as YAML only
      // when the preamble has mapping metadata (the .md/.mdc convention we support).
      var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
      if (lines.Length == 0 || lines[0] != "---") return false;
      for (var i = 1; i < lines.Length; i++) {
        if (lines[i] == "---" || lines[i] == "...") break;
        if (Regex.IsMatch(lines[i], @"^(?:[\p{L}_][\p{L}\p{N}_.-]*|""[^""\r\n]+""|'[^'\r\n]+')[ \t]*:")) return true;
      }
      return false;
    }

    private static MarkdownPipeline BuildPipeline(bool yaml)
    {
      var builder = new MarkdownPipelineBuilder()
        .UsePipeTables(new PipeTableOptions { UseGfmRules = true }).UseFootnotes()
        .UseDefinitionLists().UseCustomContainers().UseMathematics()
        .UseGenericAttributes().UsePreciseSourceLocation();
      builder.Extensions.Add(new PreserveSourceFootnotesExtension());
      if (yaml) builder.UseYamlFrontMatter();
      return builder.Build();
    }

    // Markdig removes unused footnotes after inline parsing for rendering. Translation
    // must retain those source nodes too, including when a reference is in another chunk.
    private sealed class PreserveSourceFootnotesExtension : IMarkdownExtension
    {
      public void Setup(MarkdownPipelineBuilder builder)
      {
        for (var i = 0; i < builder.BlockParsers.Count; i++)
          if (builder.BlockParsers[i] is FootnoteParser) builder.BlockParsers[i] = new SourceFootnoteParser();
      }
      public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer) { }
    }
    private sealed class SourceFootnoteParser : FootnoteParser
    {
      public override BlockState TryOpen(BlockProcessor processor)
      {
        if (!processor.Document.ContainsData(CaptureKey)) {
          processor.Document.SetData(CaptureKey, true);
          processor.Document.ProcessInlinesBegin += (state, inline) => state.Document.SetData(FootnotesKey,
            state.Document.OfType<FootnoteGroup>().SelectMany(g => g.OfType<Footnote>()).ToArray());
        }
        return base.TryOpen(processor);
      }
    }
  }
}
