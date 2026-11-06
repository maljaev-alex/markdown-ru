using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace Webview2Viewer
{
  internal sealed class ImageWindowPlacement
  {
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }

    internal static string DefaultFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnotherMarkdown", "image-window.json");

    internal static ImageWindowPlacement Load(string path)
    {
      try {
        var saved = JsonConvert.DeserializeObject<ImageWindowPlacement>(File.ReadAllText(path, Encoding.UTF8));
        return saved != null && saved.Width >= 360 && saved.Height >= 240 && saved.Width <= 100000 && saved.Height <= 100000
          && Math.Abs((long)saved.Left) <= 1000000 && Math.Abs((long)saved.Top) <= 1000000 ? saved : null;
      }
      catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException || error is System.Security.SecurityException) { return null; }
    }

    internal Rectangle VisibleBounds(Rectangle[] workAreas, Rectangle fallback)
    {
      var bounds = new Rectangle(Left, Top, Width, Height);
      var area = workAreas.OrderByDescending(item => {
        var intersection = Rectangle.Intersect(item, bounds);
        return (long)intersection.Width * intersection.Height;
      }).FirstOrDefault();
      if (area.IsEmpty || !area.IntersectsWith(bounds)) area = fallback;
      var width = Math.Min(Width, area.Width);
      var height = Math.Min(Height, area.Height);
      return new Rectangle(Math.Max(area.Left, Math.Min(Left, area.Right - width)),
        Math.Max(area.Top, Math.Min(Top, area.Bottom - height)), width, height);
    }

    internal static void Save(string path, Form window)
    {
      if (window.WindowState == FormWindowState.Minimized) return;
      var bounds = window.WindowState == FormWindowState.Normal ? window.Bounds : window.RestoreBounds;
      if (bounds.Width < 360 || bounds.Height < 240) return;
      var saved = new ImageWindowPlacement { Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height,
        Maximized = window.WindowState == FormWindowState.Maximized };
      var pending = path + "." + Guid.NewGuid().ToString("N") + ".pending";
      try {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var json = JsonConvert.SerializeObject(saved);
        if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == json) return;
        File.WriteAllText(pending, json, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(pending, path, null);
        else File.Move(pending, path);
      }
      catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException) { }
      finally {
        try { if (File.Exists(pending)) File.Delete(pending); }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is System.Security.SecurityException) { }
      }
    }
  }
}
