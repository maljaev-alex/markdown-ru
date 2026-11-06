namespace PanelCommon
{
  public interface ISettings
  {
    bool SyncViewWithCaretPosition { get; }
    bool SyncViewWithFirstVisibleLine { get; }
    string AssetsPath { get; }
    string CssFileName { get; }
    string CssDarkModeFileName { get; }
    int ZoomLevel { get; }
    bool ShowLinkAddresses { get; }
    bool PreserveEditorLineBreaks { get; }
    bool IsDarkModeEnabled { get; }
    string DefaultAssetPath { get; }
    string DefaultCssFile { get; }
    string DefaultDarkModeCssFile { get; }

    string[] EnabledMarkdownPlugins { get; }
  }
}
