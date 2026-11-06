using System;
using System.IO;
using System.Text.RegularExpressions;

namespace AnotherMarkdown.Editing
{
  public static class ImageAsset
  {
    public static byte[] Decode(string data, out string extension)
    {
      var match = Regex.Match(data ?? "", @"\Adata:image/(png|jpeg|gif|webp|bmp);base64,", RegexOptions.IgnoreCase);
      if (!match.Success || data.Length > 28 * 1024 * 1024) throw new IOException("Допустимы PNG, JPEG, GIF, WebP и BMP размером до 20 МБ.");
      extension = match.Groups[1].Value.ToLowerInvariant().Replace("jpeg", "jpg");
      return Convert.FromBase64String(data.Substring(match.Length));
    }
    public static string Save(string documentPath, string data)
    {
      var bytes = Decode(data, out var extension);
      var root = Path.GetDirectoryName(documentPath);
      if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) throw new IOException("Сначала сохрани документ, чтобы выбрать папку для изображений.");
      var directory = Path.Combine(root, Path.GetFileNameWithoutExtension(documentPath) + ".assets");
      Directory.CreateDirectory(directory);
      var file = Path.Combine(directory, "image-" + Guid.NewGuid().ToString("N") + "." + extension);
      File.WriteAllBytes(file, bytes);
      return new Uri(root + Path.DirectorySeparatorChar).MakeRelativeUri(new Uri(file)).ToString();
    }
  }
}
