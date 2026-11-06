namespace PanelCommon
{
  public struct NavigateToEvent
  {
    public string Filename;
    // One-based positions; zero means no position was specified.
    public int Line;
    public int Column;
  }
}
