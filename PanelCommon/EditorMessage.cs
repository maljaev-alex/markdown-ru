namespace PanelCommon
{
  // Offsets use UTF-16 code units, as in JavaScript. Anchor/focus preserve direction.
  public sealed class EditorNavigation
  {
    public string Session { get; set; }
    public long Revision { get; set; }
    public int Anchor { get; set; }
    public int Focus { get; set; }
    public int Top { get; set; }
    public bool Selection { get; set; }
    public bool Scroll { get; set; }
    public bool Reveal { get; set; }
    public bool Request { get; set; }
  }
  public sealed class EditorUiState
  {
    public string Session { get; set; }
    public string Status { get; set; }
    public bool Error { get; set; }
    public bool Editing { get; set; }
    public bool ReadOnly { get; set; }
    public bool Translated { get; set; }
    public bool HasConflict { get; set; }
    public bool CanRetry { get; set; }
  }

  public sealed class EditorSnapshot
  {
    public string Session { get; set; }
    public string Path { get; set; }
    public string AssetPath { get; set; }
    public string Text { get; set; }
    public long Revision { get; set; }
    public bool Translated { get; set; }
    public bool ReadOnly { get; set; }
    public bool Modified { get; set; }
    public bool PreserveLineBreaks { get; set; }
    public bool ShowLinkAddresses { get; set; }
    public bool Dark { get; set; }
    public bool SyncSelection { get; set; } = true;
    public bool SyncScroll { get; set; } = true;
    public string DocumentUrl { get; set; }
    public string BaseUrl { get; set; }
    public string DraftText { get; set; }
    public string DraftBase { get; set; }
  }
  public sealed class EditorRequest
  {
    public string RequestId { get; set; }
    public string Action { get; set; }
    public string Session { get; set; }
    public string BaseText { get; set; }
    public string Content { get; set; }
    public string Name { get; set; }
    public string Data { get; set; }
    public EditorNavigation Navigation { get; set; }
  }
  public sealed class EditorReply
  {
    public string RequestId { get; set; }
    public string Status { get; set; }
    public string Message { get; set; }
    public string Url { get; set; }
    public EditorSnapshot Snapshot { get; set; }
  }
}
