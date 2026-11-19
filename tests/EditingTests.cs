using System;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Text;
using AnotherMarkdown.Editing;
using Kbg.NppPluginNET.PluginInfrastructure;

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
      var previousText = File.ReadAllText(target);
      var previousFiles = Directory.GetFiles(assetDirectory).OrderBy(p => p).ToArray();
      using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.None)) {
        rejected = false;
        try { TranslationExport.Save(original, target, "![Again](" + data + ")\n"); } catch (IOException) { rejected = true; }
        Check(rejected, "export reports a locked destination");
      }
      Check(File.ReadAllText(target) == previousText && Directory.GetFiles(assetDirectory).OrderBy(p => p).SequenceEqual(previousFiles),
        "failed replacement preserves the existing Markdown and removes only images created by this attempt");
      Check(TranslationExport.Save(original, target, "[Query](?view=full#part) / [File](one%20two.md?x=1#here)") ==
        "[Query](?view=full#part) / [File](../source/one%20two.md?x=1#here)", "query-only links stay local and escaped relative paths retain their suffix");
      var imageCount = Directory.GetFiles(assetDirectory).Length;
      TranslationExport.Save(original, target, "![one](" + data + ") ![two](" + data + ")");
      Check(Directory.GetFiles(assetDirectory).Length == imageCount + 1, "repeated images share a file in the same export");
      var tooLarge = "data:image/png;base64," + Convert.ToBase64String(new byte[20 * 1024 * 1024 + 1]);
      rejected = false;
      try { ImageAsset.Decode(tooLarge, out _); } catch (IOException) { rejected = true; }
      Check(rejected, "the image limit is measured in decoded bytes");
      SourceSessionChecks(original);
      FormatSessionChecks(original);
      Console.WriteLine("PASS editing: " + checks + " assertions"); return 0;
    } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
  }

  private sealed class TestBuffer : RealProxy
  {
    internal string Text;
    internal bool Modified;
    internal int SavePoints, UndoGroups;
    private int start, end;
    internal TestBuffer(string text, bool modified) : base(typeof(IScintillaGateway)) { Text = text; Modified = modified; }
    internal IScintillaGateway Gateway => (IScintillaGateway)GetTransparentProxy();
    public override IMessage Invoke(IMessage message)
    {
      var call = (IMethodCallMessage)message;
      object result = null;
      switch (call.MethodName) {
        case "GetModify": result = Modified; break;
        case "GetReadOnly": result = false; break;
        case "GetCodePage": result = 65001; break;
        case "GetLength": result = Encoding.UTF8.GetByteCount(Text); break;
        case "GetText": result = Text; break;
        case "BeginUndoAction": UndoGroups++; break;
        case "EndUndoAction": break;
        case "SetTargetStart": start = (int)call.Args[0]; break;
        case "SetTargetEnd": end = (int)call.Args[0]; break;
        case "ReplaceTarget":
          var bytes = Encoding.UTF8.GetBytes(Text);
          var replacement = Encoding.UTF8.GetBytes((string)call.Args[1]);
          Text = Encoding.UTF8.GetString(bytes.Take(start).Concat(replacement).Concat(bytes.Skip(end)).ToArray());
          Modified = true; result = replacement.Length; break;
        case "SetSavePoint": Modified = false; SavePoints++; break;
        default: throw new InvalidOperationException(call.MethodName);
      }
      return new ReturnMessage(result, null, 0, call.LogicalCallContext, call);
    }
  }

  private static void FormatSessionChecks(string path)
  {
    const string original = "+ __word__\r\n", formatted = "* **word**\r\n";
    File.WriteAllText(path, original);
    foreach (var dirty in new[] { false, true }) {
      var session = new EditorSourceSession(); session.TrackPath(path);
      var buffer = new TestBuffer(original, dirty);
      SourceTextEdit.Replace(buffer.Gateway, original, formatted);
      session.BeginEdit(original, formatted, dirty);
      Check(buffer.Modified && buffer.UndoGroups == 1, "formatting is one native undo action");
      Check(session.EndEdit(buffer.Gateway, buffer.Text) == original && buffer.Text == original && buffer.Modified == dirty,
        "unchanged exit restores exact bytes and the original native modified state");
    }
    var state = new EditorSourceSession(); state.TrackPath(path);
    var background = new TestBuffer(formatted, true);
    state.BeginEdit(original, formatted, false);
    state.EndEdit(background.Gateway, formatted, true);
    state.Activated(background.Gateway);
    state.Activated(background.Gateway);
    Check(background.Text == original && !background.Modified && background.SavePoints == 2,
      "a background restore publishes its clean save point exactly once on the owning Notepad++ view");
    var changed = new TestBuffer("native edit", true);
    state.BeginEdit(original, formatted, false);
    Check(state.EndEdit(changed.Gateway, changed.Text) == "native edit" && changed.SavePoints == 0 && changed.UndoGroups == 0,
      "an independent native change is never replaced or marked clean");
    state.BeginEdit(original, formatted, false);
    changed.Text = formatted; changed.Modified = false;
    File.WriteAllText(path, formatted); state.FileSaved(path);
    Check(state.EndEdit(changed.Gateway, changed.Text) == original && changed.Modified && changed.SavePoints == 0 &&
      File.ReadAllText(path) == formatted, "restoring after an explicit save leaves a genuinely dirty buffer and preserves the saved file");
  }

  private static void SourceSessionChecks(string path)
  {
    var session = new EditorSourceSession();
    File.WriteAllText(path, "before");
    session.TrackPath(path);
    session.Observe("before", false, 65001);
    Check(session.CanSave(), "a known source baseline permits saving");
    File.WriteAllText(path, "external", Encoding.UTF8);
    session.TrackPath(path);
    session.Observe("edited buffer", true, 65001);
    Check(!session.SaveAs(() => { }) && !session.CanSave(), "returning from a dialog without a file-saved notification cannot accept external bytes");
    session.Observe("stale", false, 65001);
    Check(!session.CanSave(), "a stale clean buffer cannot reset the baseline");
    session.Observe("external", false, 65001);
    Check(session.CanSave(), "a matching native reload accepts the disk version including its BOM");
    Check(session.SaveAs(() => session.FileSaved(path)), "a completed native save is observable even for a clean buffer");
    var locked = new EditorSourceSession();
    using (File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None)) {
      locked.TrackPath(path);
      locked.Observe("external", false, 65001);
      Check(!locked.CanSave(), "an unreadable file does not prevent preview or acquire a false baseline");
    }
    locked.Observe("external", false, 65001);
    Check(locked.CanSave(), "a clean buffer recovers after the lock is released");
  }
}
