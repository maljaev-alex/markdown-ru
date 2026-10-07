using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace AnotherMarkdown
{
  // Draw at the requested pixel size; never enlarge the old 16-pixel artwork.
  internal static class PluginIcon
  {
    public static Bitmap Render(int size, bool dark = false)
    {
      var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
      using (var graphics = Graphics.FromImage(bitmap)) Draw(graphics, new Rectangle(0, 0, size, size), dark);
      return bitmap;
    }

    public static void Draw(Graphics graphics, Rectangle bounds, bool dark = false)
    {
      var state = graphics.Save();
      try {
        var side = Math.Min(bounds.Width, bounds.Height);
        graphics.TranslateTransform(bounds.X + (bounds.Width - side) / 2f, bounds.Y + (bounds.Height - side) / 2f);
        graphics.ScaleTransform(side / 32f, side / 32f);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var tile = new GraphicsPath()) {
          tile.AddArc(1, 1, 12, 12, 180, 90); tile.AddArc(19, 1, 12, 12, 270, 90);
          tile.AddArc(19, 19, 12, 12, 0, 90); tile.AddArc(1, 19, 12, 12, 90, 90); tile.CloseFigure();
          using (var blue = new SolidBrush(ColorTranslator.FromHtml(dark ? "#2563EB" : "#1D4ED8"))) graphics.FillPath(blue, tile);
        }
        var letter = new[] { new PointF(5, 22), new PointF(5, 9), new PointF(8, 9), new PointF(12, 15), new PointF(16, 9), new PointF(19, 9), new PointF(19, 22), new PointF(16, 22), new PointF(16, 14), new PointF(12, 20), new PointF(8, 14), new PointF(8, 22) };
        graphics.FillPolygon(Brushes.White, letter);
        using (var mint = new SolidBrush(ColorTranslator.FromHtml(dark ? "#5EEAD4" : "#67F2DB"))) {
          graphics.FillRectangle(mint, 22, 9, 4, 2);
          graphics.FillPolygon(mint, new[] { new PointF(25, 6), new PointF(29, 10), new PointF(25, 14) });
          graphics.FillRectangle(mint, 24, 21, 5, 2);
          graphics.FillPolygon(mint, new[] { new PointF(25, 18), new PointF(21, 22), new PointF(25, 26) });
        }
      }
      finally { graphics.Restore(state); }
    }

    public static Icon Create(int size, bool dark = false)
    {
      using (var bitmap = Render(size, dark)) {
        var handle = bitmap.GetHicon();
        try { using (var borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
      }
    }

    public static Icon ApplicationIcon()
    {
      using (var stream = typeof(PluginIcon).Assembly.GetManifestResourceStream("AnotherMarkdown.Resources.translate-ru.ico")) {
        if (stream == null) return Create(32);
        using (var icon = new Icon(stream)) return (Icon)icon.Clone();
      }
    }

    public static int ScaleForWindow(int logicalSize, IntPtr window)
    {
      try { var dpi = GetDpiForWindow(window); if (dpi != 0) return Math.Max(16, (int)((logicalSize * dpi + 48) / 96)); }
      catch (EntryPointNotFoundException) { }
      return logicalSize;
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
  }
}
