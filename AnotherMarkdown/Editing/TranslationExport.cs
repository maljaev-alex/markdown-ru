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
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
      .UseAdvancedExtensions().UsePreciseSourceLocation().Build();

    // Rewrite URL spans only: formatting, code blocks and the original file are untouched.
    public static string Save(string sourcePath, string destinationPath, string markdown)
    {
      sourcePath = Path.GetFullPath(sourcePath);
      destinationPath = Path.GetFullPath(destinationPath);
      if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        throw new IOException("Выбери другой файл: перевод не должен перезаписывать оригинал.");
      var createdImages = new List<string>();
      try {
        var text = RewriteLinks(sourcePath, destinationPath, markdown, createdImages);
        WriteMarkdown(destinationPath, text);
        return text;
      }
      catch {
        foreach (var image in createdImages) File.Delete(image);
        throw;
      }
    }

    private static Uri DirectoryUri(string path)
    {
      var directory = Path.GetDirectoryName(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
      // An implicit Windows file URI treats percent escapes in relative URLs as literal text.
      return new Uri(new Uri(directory).AbsoluteUri);
    }

    private static string RewriteLinks(string sourcePath, string destinationPath, string markdown, List<string> createdImages)
    {
      var sourceRoot = DirectoryUri(sourcePath);
      var targetRoot = DirectoryUri(destinationPath);
      var document = Markdown.Parse(markdown, Pipeline);
      var replacements = new Dictionary<int, Tuple<int, string>>();
      var imageFiles = new Dictionary<string, string>();
      void Rewrite(string url, SourceSpan span, bool image) {
        if (string.IsNullOrEmpty(url) || span.Start < 0 || span.End < span.Start || span.End >= markdown.Length) return;
        string replacement = null;
        if (image && url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) {
          if (!imageFiles.TryGetValue(url, out replacement)) {
            replacement = ImageAsset.Save(destinationPath, url, out var file);
            createdImages.Add(file);
            imageFiles.Add(url, replacement);
          }
        }
        else if (sourceRoot != targetRoot && url[0] != '#' && url[0] != '?' && url[0] != '/' && !Uri.TryCreate(url, UriKind.Absolute, out _)) {
          var suffixIndex = url.IndexOfAny(new[] { '#', '?' });
          var path = suffixIndex < 0 ? url : url.Substring(0, suffixIndex);
          var suffix = suffixIndex < 0 ? "" : url.Substring(suffixIndex);
          replacement = targetRoot.MakeRelativeUri(new Uri(sourceRoot, path)).OriginalString + suffix;
        }
        if (replacement != null) replacements[span.Start] = Tuple.Create(span.End - span.Start + 1, replacement);
      }
      foreach (var node in document.Descendants()) {
        if (node is LinkInline link) Rewrite(link.Url, link.Reference?.UrlSpan ?? link.UrlSpan, link.IsImage);
        else if (node is LinkReferenceDefinition reference) Rewrite(reference.Url, reference.UrlSpan, false);
      }
      var definitions = document.GetLinkReferenceDefinitions(false);
      if (definitions != null)
        foreach (var reference in definitions.Links.Values) Rewrite(reference.Url, reference.UrlSpan, false);
      if (replacements.Count == 0) return markdown;
      var result = new StringBuilder(markdown.Length);
      var start = 0;
      foreach (var item in replacements.OrderBy(pair => pair.Key)) {
        result.Append(markdown, start, item.Key - start);
        result.Append(item.Value.Item2);
        start = item.Key + item.Value.Item1;
      }
      return result.Append(markdown, start, markdown.Length - start).ToString();
    }

    private static void WriteMarkdown(string destination, string text)
    {
      var pending = Path.Combine(Path.GetDirectoryName(destination), Path.GetRandomFileName());
      var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None);
      try {
        using (stream)
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(text);
        if (File.Exists(destination)) File.Replace(pending, destination, null);
        else File.Move(pending, destination);
      }
      catch { File.Delete(pending); throw; }
    }
  }
}
