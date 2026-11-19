using System.IO;
using System.Reflection;

namespace AnotherMarkdown.Entities
{
  public class Settings
  {
    public const int MinimumZoomLevel = 80;
    public const int MaximumZoomLevel = 500;
    public const int DefaultZoomLevel = 130;
    private int zoomLevel = DefaultZoomLevel;

    public bool SyncViewWithCaretPosition { get; set; } = true;
    public bool SyncViewWithFirstVisibleLine { get; set; } = true;
    public string AssetsPath { get; set; }
    public string CssFileName { get; set; }
    public string CssDarkModeFileName { get; set; }
    public int ZoomLevel {
      get => zoomLevel;
      set => zoomLevel = System.Math.Max(MinimumZoomLevel, System.Math.Min(MaximumZoomLevel, value));
    }
    public bool IsDarkModeEnabled { get; set; }
    public bool PreserveEditorLineBreaks { get; set; }
    public string Theme { get; set; } = "auto";
    public bool ResolveDarkMode(bool applicationDarkMode) => Theme == "dark" || (Theme != "light" && applicationDarkMode);
    public bool ShowToolbar { get; set; }
    public bool ShowStatusbar { get; set; }
    public Translation.TranslationOptions Translation { get; set; } = new Translation.TranslationOptions();
    public string[] EnabledMarkdownPlugins { get; set; }
    public string PreProcessorCommandFilename { get; set; }
    public string PreProcessorArguments { get; set; }
    public string PostProcessorCommandFilename { get; set; }
    public string PostProcessorArguments { get; set; }

    public string DefaultAssetPath
    {
      get { return _defaultAssetPath; }
    }

    public string DefaultCssFile
    {
      get { return _defaultAssetPath + "/markdown/style.css"; }
    }

    public string DefaultDarkModeCssFile
    {
      get { return _defaultAssetPath + "/markdown/style-dark.css"; }
    }

    private string _defaultAssetPath => (Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "/assets").Replace("\\", "/");
  }
}
