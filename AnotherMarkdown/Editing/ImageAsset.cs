using System;
using System.IO;
using System.Text.RegularExpressions;

namespace AnotherMarkdown.Editing
{
  public static class ImageAsset
  {
    private const int MaxBytes = 20 * 1024 * 1024;
    private const string SizeMessage = "Допустимы PNG, JPEG, GIF, WebP и BMP размером до 20 МБ.";

    public static byte[] Decode(string data, out string extension)
    {
      var match = Regex.Match(data ?? "", @"\Adata:image/(png|jpeg|gif|webp|bmp);base64,", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
      if (!match.Success || data.Length > 28 * 1024 * 1024) throw new IOException(SizeMessage);
      extension = match.Groups[1].Value.ToLowerInvariant().Replace("jpeg", "jpg");
      var bytes = Convert.FromBase64String(data.Substring(match.Length));
      if (bytes.Length > MaxBytes) throw new IOException(SizeMessage);
      return bytes;
    }

    public static string Save(string documentPath, string data) => Save(documentPath, data, out _);

    internal static string Save(string documentPath, string data, out string imagePath)
    {
      var bytes = Decode(data, out var extension);
      var root = Path.GetDirectoryName(documentPath);
      if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) throw new IOException("Сначала сохрани документ, чтобы выбрать папку для изображений.");
      var directory = Path.Combine(root, Path.GetFileNameWithoutExtension(documentPath) + ".assets");
      Directory.CreateDirectory(directory);
      var file = Path.Combine(directory, "image-" + Guid.NewGuid().ToString("N") + "." + extension);
      var relativeUrl = new Uri(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
        .MakeRelativeUri(new Uri(file)).OriginalString;
      var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
      try { using (output) output.Write(bytes, 0, bytes.Length); }
      catch { File.Delete(file); throw; }
      imagePath = file;
      return relativeUrl;
    }
  }
}
