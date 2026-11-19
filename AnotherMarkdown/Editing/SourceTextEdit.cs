using System;
using System.Text;
using Kbg.NppPluginNET.PluginInfrastructure;

namespace AnotherMarkdown.Editing
{
  // Scintilla positions are bytes; .NET string positions are UTF-16 code units.
  public sealed class SourceTextEdit
  {
    public int Start { get; private set; }
    public int End { get; private set; }
    public int Length { get; private set; }
    public string Text { get; private set; }
    public static Encoding EncodingFor(int codePage) => Encoding.GetEncoding(
      codePage == 0 ? Encoding.Default.CodePage : codePage,
      EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

    public static SourceTextEdit Between(string before, string after, Encoding encoding)
    {
      if (before == after) return null;
      var start = 0;
      while (start < before.Length && start < after.Length && before[start] == after[start]) start++;
      if (start > 0 && char.IsHighSurrogate(before[start - 1])) start--;
      var oldEnd = before.Length;
      var newEnd = after.Length;
      while (oldEnd > start && newEnd > start && before[oldEnd - 1] == after[newEnd - 1]) { oldEnd--; newEnd--; }
      if (oldEnd < before.Length && oldEnd > start && char.IsLowSurrogate(before[oldEnd])) { oldEnd++; newEnd++; }
      var text = after.Substring(start, newEnd - start);
      var startBytes = ByteCount(encoding, before, 0, start);
      return new SourceTextEdit {
        Start = startBytes,
        End = startBytes + ByteCount(encoding, before, start, oldEnd - start),
        Length = encoding.GetByteCount(text), Text = text
      };
    }

    private static unsafe int ByteCount(Encoding encoding, string text, int start, int length)
    {
      if (length == 0) return 0;
      fixed (char* chars = text) return encoding.GetByteCount(chars + start, length);
    }

    internal void Apply(IScintillaGateway gateway)
    {
      gateway.BeginUndoAction();
      try {
        gateway.SetTargetStart(Start);
        gateway.SetTargetEnd(End);
        gateway.ReplaceTarget(Length, Text);
      }
      finally { gateway.EndUndoAction(); }
    }

    internal static void Replace(IScintillaGateway gateway, string current, string target)
    {
      if (current == target) return;
      Between(current, target, EncodingFor(gateway.GetCodePage())).Apply(gateway);
      if (gateway.GetText(gateway.GetLength() + 1) != target)
        throw new System.IO.IOException("Notepad++ не подтвердил изменение текста.");
    }
  }
}
