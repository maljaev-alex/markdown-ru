using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Newtonsoft.Json;
using Webview2Viewer;

internal static class ImageWindowPlacementTests
{
  private static int checks;

  [STAThread]
  private static int Main(string[] args)
  {
    try {
      if (args.Length == 6 && args[0] == "verify") {
        var saved = ImageWindowPlacement.Load(args[1]);
        Check(saved != null && saved.Maximized, "new process reads maximized state from disk");
        Check(saved.Left == int.Parse(args[2], CultureInfo.InvariantCulture) && saved.Top == int.Parse(args[3], CultureInfo.InvariantCulture)
          && saved.Width == int.Parse(args[4], CultureInfo.InvariantCulture) && saved.Height == int.Parse(args[5], CultureInfo.InvariantCulture), "new process reads normal size and location");
        return 0;
      }
      if (args.Length != 1) throw new Exception("Usage: ImageWindowPlacementTests work-directory");
      Directory.CreateDirectory(args[0]);
      var path = Path.Combine(args[0], Guid.NewGuid().ToString("N") + ".json");
      Check(ImageWindowPlacement.Load(path) == null, "missing preferences use defaults");
      Application.EnableVisualStyles();
      using (var window = new PlacementForm()) {
        window.Bounds = new Rectangle(80, 70, 800, 600);
        window.Show();
        var normal = window.Bounds;
        ImageWindowPlacement.Save(path, window);
        var saved = ImageWindowPlacement.Load(path);
        Check(saved != null && !saved.Maximized, "normal state is saved");
        Check(new Rectangle(saved.Left, saved.Top, saved.Width, saved.Height) == normal, "normal position and size are saved");
        window.WindowState = FormWindowState.Maximized;
        ImageWindowPlacement.Save(path, window);
        saved = ImageWindowPlacement.Load(path);
        Check(saved.Maximized, "maximized state is saved");
        Check(new Rectangle(saved.Left, saved.Top, saved.Width, saved.Height) == normal, "maximize preserves the normal restore bounds");
        var arguments = "verify \"" + path + "\" " + normal.X + " " + normal.Y + " " + normal.Width + " " + normal.Height;
        using (var child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, arguments) {
          UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        })) {
          if (!child.WaitForExit(10000)) throw new TimeoutException("Placement verification child process");
          Check(child.ExitCode == 0, "separate process restores the persisted settings");
        }
        window.WindowState = FormWindowState.Minimized;
        ImageWindowPlacement.Save(path, window);
        Check(ImageWindowPlacement.Load(path).Maximized, "minimizing does not overwrite the last useful state");
        window.WindowState = FormWindowState.Normal;
        window.Bounds = normal;
        ImageWindowPlacement.Save(path, window);
        Check(!ImageWindowPlacement.Load(path).Maximized, "restoring normal state is saved");
        Check(Directory.GetFiles(args[0], "*.pending").Length == 0, "atomic saves leave no pending files");
      }
      var primary = new Rectangle(0, 0, 1920, 1040);
      var secondary = new Rectangle(-1280, 0, 1280, 984);
      var placement = new ImageWindowPlacement { Left = -1100, Top = 40, Width = 800, Height = 600 };
      Check(placement.VisibleBounds(new[] { primary, secondary }, primary) == new Rectangle(-1100, 40, 800, 600), "negative monitor coordinates are retained");
      Check(primary.Contains(placement.VisibleBounds(new[] { primary }, primary)), "disconnected monitor placement returns to a visible screen");
      placement = new ImageWindowPlacement { Left = 9000, Top = 9000, Width = 4000, Height = 3000 };
      Check(placement.VisibleBounds(new[] { primary }, primary) == primary, "oversized offscreen window is clamped to the current display");
      File.WriteAllText(path, "{broken");
      Check(ImageWindowPlacement.Load(path) == null, "corrupt settings do not prevent opening images");
      File.WriteAllText(path, JsonConvert.SerializeObject(new ImageWindowPlacement { Width = 0, Height = 0 }));
      Check(ImageWindowPlacement.Load(path) == null, "invalid dimensions use default placement");
      Console.WriteLine("PASS image window placement: " + checks + " assertions");
      return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
  }

  private static void Check(bool condition, string label)
  {
    if (!condition) throw new Exception(label);
    checks++;
  }

  private sealed class PlacementForm : Form
  {
    public PlacementForm() { Opacity = 0; ShowInTaskbar = false; }
    protected override bool ShowWithoutActivation => true;
  }
}
