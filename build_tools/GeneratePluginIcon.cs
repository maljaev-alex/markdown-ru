// Rebuild the committed native assets from the same geometry used at runtime.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using AnotherMarkdown;

internal static class GeneratePluginIcon
{
  private static int Main(string[] args)
  {
    var folder = args[0];
    var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
    var frames = new byte[sizes.Length][];
    for (var i = 0; i < sizes.Length; i++) using (var image = PluginIcon.Render(sizes[i])) using (var stream = new MemoryStream()) {
      image.Save(stream, ImageFormat.Png); frames[i] = stream.ToArray();
    }
    using (var writer = new BinaryWriter(File.Create(Path.Combine(folder, "translate-ru.ico")))) {
      writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
      var offset = 6 + 16 * sizes.Length;
      for (var i = 0; i < sizes.Length; i++) {
        writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
        writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write(frames[i].Length); writer.Write(offset); offset += frames[i].Length;
      }
      foreach (var frame in frames) writer.Write(frame);
    }
    using (var image = PluginIcon.Render(128)) image.Save(Path.Combine(folder, "translate-ru.png"), ImageFormat.Png);
    Console.WriteLine("Generated nine native icon sizes and PNG from vector geometry.");
    return 0;
  }
}
