using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AnotherMarkdown.Editing
{
  public static class TranslationExport
  {
    // Rewrite URL spans only: formatting, code blocks and the original file are untouched.
    public static string Save(string sourcePath, string destinationPath, string markdown)
    {
      sourcePath = Path.GetFullPath(sourcePath);
      destinationPath = Path.GetFullPath(destinationPath);
      if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        throw new IOException("Выбери другой файл: перевод не должен перезаписывать оригинал.");
      var sourceRoot = new Uri(Path.GetDirectoryName(sourcePath) + Path.DirectorySeparatorChar);
      var targetRoot = new Uri(Path.GetDirectoryName(destinationPath) + Path.DirectorySeparatorChar);
      var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().UsePreciseSourceLocation().Build();
      var document = Markdown.Parse(markdown, pipeline);
      var replacements = new Dictionary<int, Tuple<int, string>>();
      var imageFiles = new Dictionary<string, string>();
      Action<string, SourceSpan, bool> rewrite = (url, span, image) => {
        if (string.IsNullOrEmpty(url) || span.Start < 0 || span.End < span.Start || span.End >= markdown.Length) return;
        string replacement = null;
        if (image && url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) {
          if (!imageFiles.TryGetValue(url, out replacement)) imageFiles[url] = replacement = ImageAsset.Save(destinationPath, url);
        }
        else if (sourceRoot != targetRoot && !url.StartsWith("#") && !url.StartsWith("/") && !Uri.TryCreate(url, UriKind.Absolute, out var _)) {
          var suffixIndex = url.IndexOfAny(new[] { '#', '?' });
          var path = suffixIndex < 0 ? url : url.Substring(0, suffixIndex);
          var suffix = suffixIndex < 0 ? "" : url.Substring(suffixIndex);
          replacement = targetRoot.MakeRelativeUri(new Uri(sourceRoot, path)).ToString() + suffix;
        }
        if (replacement != null) replacements[span.Start] = Tuple.Create(span.End - span.Start + 1, replacement);
      };
      foreach (var node in document.Descendants()) {
        if (node is LinkInline link) rewrite(link.Url, link.Reference?.UrlSpan ?? link.UrlSpan, link.IsImage);
        else if (node is LinkReferenceDefinition reference) rewrite(reference.Url, reference.UrlSpan, false);
      }
      var definitions = document.GetLinkReferenceDefinitions(false);
      if (definitions != null)
        foreach (var reference in definitions.Links.Values) rewrite(reference.Url, reference.UrlSpan, false);
      var result = new StringBuilder(markdown);
      foreach (var item in replacements.OrderByDescending(item => item.Key)) result.Remove(item.Key, item.Value.Item1).Insert(item.Key, item.Value.Item2);
      var text = result.ToString();
      File.WriteAllText(destinationPath, text, new UTF8Encoding(false));
      return text;
    }
  }
}
