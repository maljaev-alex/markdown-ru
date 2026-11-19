using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Kbg.NppPluginNET.PluginInfrastructure;

namespace AnotherMarkdown.Editing
{
  internal sealed class VisualEditBaseline
  {
    internal readonly string Original, Formatted;
    internal VisualEditBaseline(string original, string formatted) { Original = original; Formatted = formatted; }
    internal string OnExit(string current) => current == Formatted ? Original : current;
  }

  internal sealed class EditorSourceSession
  {
    internal string Id { get; } = Guid.NewGuid().ToString("N");
    internal string Path { get; private set; }
    private int saves;
    private string diskHash, observedText;
    private bool baselineKnown, observedModified;
    private VisualEditBaseline edit;
    private bool cleanAtEntry;
    private bool notifyOnActivation;
    private int savesAtEntry;
    internal bool VisualEditing => edit != null;
    internal bool NeedsRestore(string current) => edit != null && edit.OnExit(current) != current;

    internal void BeginEdit(string original, string formatted, bool wasModified)
    {
      if (edit != null) return;
      edit = new VisualEditBaseline(original, formatted);
      cleanAtEntry = !wasModified;
      savesAtEntry = saves;
    }

    internal string EndEdit(IScintillaGateway gateway, string current, bool auxiliaryView = false)
    {
      if (edit == null) return current;
      var result = edit.OnExit(current);
      if (result != current && gateway.GetReadOnly()) throw new IOException("Документ открыт только для чтения.");
      var restoreSavePoint = current == edit.Formatted && cleanAtEntry && savesAtEntry == saves &&
        gateway.GetModify() && CanSave();
      SourceTextEdit.Replace(gateway, current, result);
      if (restoreSavePoint) {
        gateway.SetSavePoint();
        notifyOnActivation = auxiliaryView;
      }
      edit = null;
      return result;
    }

    internal void Activated(IScintillaGateway gateway)
    {
      if (!notifyOnActivation) return;
      notifyOnActivation = false;
      // Notepad++ observes its own Scintilla views for tab/title dirty indicators.
      // Repeat the notification once the already-clean document is on such a view.
      if (!gateway.GetModify()) gateway.SetSavePoint();
    }

    internal void TrackPath(string path)
    {
      if (Path == path) return;
      Path = path;
      notifyOnActivation = false;
      observedText = null;
      baselineKnown = TryReadHash(out diskHash);
    }

    internal void FileSaved(string path)
    {
      saves++;
      notifyOnActivation = false;
      Path = path;
      baselineKnown = TryReadHash(out diskHash);
    }

    internal bool CanSave() => baselineKnown && TryReadHash(out var current) && current == diskHash;

    internal bool SaveAs(Action showNativeDialog)
    {
      // Notepad++ sends FILESAVED synchronously on success, even if the buffer was already clean.
      var before = saves;
      showNativeDialog();
      return saves != before;
    }

    internal void Observe(string text, bool modified, int codePage)
    {
      if (baselineKnown && observedText == text && observedModified == modified) return;
      observedText = text;
      observedModified = modified;
      if (modified || string.IsNullOrEmpty(Path)) return;
      // A native reload has no completion notification. Accept the new baseline
      // only if the clean buffer matches the bytes from which the hash was taken.
      try {
        using (var stream = File.Open(Path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
          var hash = Hash(stream);
          if (baselineKnown && hash == diskHash) return;
          stream.Position = 0;
          using (var reader = new StreamReader(stream, SourceTextEdit.EncodingFor(codePage), true)) {
            if (reader.ReadToEnd() == text) { diskHash = hash; baselineKnown = true; }
          }
        }
      }
      catch (IOException) { }
      catch (UnauthorizedAccessException) { }
      catch (DecoderFallbackException) { }
    }

    private bool TryReadHash(out string hash)
    {
      hash = null;
      if (string.IsNullOrEmpty(Path)) return true;
      try {
        using (var stream = File.Open(Path, FileMode.Open, FileAccess.Read, FileShare.Read)) hash = Hash(stream);
        return true;
      }
      catch (FileNotFoundException) { return true; }
      catch (DirectoryNotFoundException) { return true; }
      catch (IOException) { return false; }
      catch (UnauthorizedAccessException) { return false; }
    }

    private static string Hash(Stream input)
    {
      using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(input));
    }
  }
}
