using System;
using System.IO;
using System.Linq;
using System.Text;
using AnotherMarkdown.Editing;

internal static class EditingTests
{
  private static int checks;
  private static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
  private static void Edit(string before, string after, Encoding encoding)
  {
    var edit = SourceTextEdit.Between(before, after, encoding);
    if (before == after) { Check(edit == null, "no-op edit"); return; }
    var bytes = encoding.GetBytes(before);
    var result = bytes.Take(edit.Start).Concat(encoding.GetBytes(edit.Text)).Concat(bytes.Skip(edit.End)).ToArray();
    Check(encoding.GetString(result) == after, "byte-correct replacement");
    Check(edit.Length == encoding.GetByteCount(edit.Text), "replacement byte count");
  }
  private static int Main(string[] args)
  {
    try {
      var utf8 = SourceTextEdit.EncodingFor(65001);
      foreach (var pair in new[] {
        new[] { "same", "same" }, new[] { "", "insert" }, new[] { "delete", "" },
        new[] { "A\r\n\u041f\u0440\u0438\u0432\u0435\u0442\r\nZ", "A\r\n\u041c\u0438\u0440\r\nZ" },
        new[] { "A\ud83d\ude00Z", "A\ud83d\ude03Z" }, new[] { "\ud83d\ude00", "\ud83d\ude00\ud83d\ude03" },
        new[] { "e\u0301 x", "e\u0301 y" }, new[] { "A\nB", "A\nB\n" }
      }) Edit(pair[0], pair[1], utf8);
      Edit("\u041f\u0440\u0438\u0432\u0435\u0442", "\u041c\u0438\u0440", SourceTextEdit.EncodingFor(1251));
      var rejected = false;
      try { SourceTextEdit.Between("a", "\ud83d\ude00", SourceTextEdit.EncodingFor(1251)); } catch (EncoderFallbackException) { rejected = true; }
      Check(rejected, "unrepresentable characters are rejected before mutation");
      var root = Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
      var originalDirectory = Path.Combine(root, "source"); var targetDirectory = Path.Combine(root, "translated");
      Directory.CreateDirectory(originalDirectory); Directory.CreateDirectory(targetDirectory);
      var original = Path.Combine(originalDirectory, "original.md"); var target = Path.Combine(targetDirectory, "result.md");
      File.WriteAllText(original, "ORIGINAL");
      rejected = false;
      try { TranslationExport.Save(original, original, "translated"); } catch (IOException) { rejected = true; }
      Check(rejected && File.ReadAllText(original) == "ORIGINAL", "translation cannot overwrite original");
      var markdown = "# Result\r\n\r\n![Image](images/a.png)\r\n\r\n[Document](other.md#anchor)\r\n\r\n[Remote](https://example.org/a)\r\n\r\n`![literal](images/a.png)`\r\n";
      var saved = TranslationExport.Save(original, target, markdown);
      Check(saved.Contains("../source/images/a.png"), "relative image rebased on Save As");
      Check(saved.Contains("../source/other.md#anchor"), "relative document link rebased");
      Check(saved.Contains("https://example.org/a") && saved.Contains("`![literal](images/a.png)`"), "URLs and code preserved");
      Check(saved.Contains("\r\n\r\n") && File.ReadAllText(original) == "ORIGINAL", "EOL and source preserved");
      var sameFolder = Path.Combine(originalDirectory, "copy.md");
      Check(TranslationExport.Save(original, sameFolder, markdown) == markdown, "same-directory Save As preserves source text");
      var reference = "![ref][image]\n\n[image]: images/a.png \"title\"\n";
      Check(TranslationExport.Save(original, target, reference).Contains("../source/images/a.png"), "reference URL rebased");
      var data = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jzXkAAAAASUVORK5CYII=";
      var assetDirectory = Path.Combine(targetDirectory, "result.assets");
      var previousImages = Directory.Exists(assetDirectory) ? Directory.GetFiles(assetDirectory, "*.png").Length : 0;
      saved = TranslationExport.Save(original, target, "![Pasted](" + data + ")\n");
      Check(!saved.Contains("base64") && saved.Contains("result.assets/image-"), "translated clipboard image materialized only on Save As");
      Check(Directory.GetFiles(assetDirectory, "*.png").Length == previousImages + 1, "one image file created");
      Console.WriteLine("PASS editing: " + checks + " assertions"); return 0;
    } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
  }
}
