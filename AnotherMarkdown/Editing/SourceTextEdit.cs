using System;
using System.Text;

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
      return new SourceTextEdit {
        Start = encoding.GetByteCount(before.Substring(0, start)),
        End = encoding.GetByteCount(before.Substring(0, oldEnd)),
        Length = encoding.GetByteCount(text), Text = text
      };
    }
  }
}
