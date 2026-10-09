using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using AnotherMarkdown.Entities;
using AnotherMarkdown.Translation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnotherMarkdown.Forms
{
  public partial class SettingsForm : Form
  {
    public int ZoomLevel { get; set; }
    public string AssetsPath { get; set; }
    public string CssFileName { get; set; }
    public string CssDarkModeFileName { get; set; }
    public bool ShowToolbar { get; set; }
    public bool ShowStatusbar { get; set; }

    public string[] AllowedMarkdownPlugins { get; set; }
    private readonly List<Control> initialLayouts = new List<Control>();
    private readonly SettingsDiscoveryCache settingsDiscovery;
    private ToolTip settingsToolTips;

    private sealed class SettingsLayoutPanel : TableLayoutPanel
    {
      public SettingsLayoutPanel() { DoubleBuffered = true; ResizeRedraw = true; }
    }

    // Discovery messages must not resize the form's rows while the user reads them.
    private sealed class SettingsStatusLabel : Label
    {
      public SettingsStatusLabel() { AutoSize = false; AutoEllipsis = true; Dock = DockStyle.Fill; Height = Font.Height + 4; }
      protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Height = Font.Height + 4; }
      public override Size GetPreferredSize(Size proposedSize) => new Size(1, Font.Height + 4);
    }

    private Label CreateSettingsStatus(string name, string text)
    {
      var status = new SettingsStatusLabel { Name = name, Text = text, Margin = new Padding(3, 4, 3, 8) };
      status.TextChanged += (_, __) => { settingsToolTips.SetToolTip(status, status.Text); status.AccessibleDescription = status.Text; };
      settingsToolTips.SetToolTip(status, text);
      return status;
    }

    private T HoldInitialLayout<T>(T control) where T : Control
    {
      control.SuspendLayout(); initialLayouts.Add(control); return control;
    }

    private void CompleteInitialLayout()
    {
      for (var i = initialLayouts.Count - 1; i >= 0; i--) initialLayouts[i].ResumeLayout(true);
      initialLayouts.Clear(); ResumeLayout(true);
    }

    public SettingsForm(Settings settings) : this(settings, SettingsDiscoveryCache.Shared) { }

    internal SettingsForm(Settings settings, SettingsDiscoveryCache discovery)
    {
      settingsDiscovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
      DoubleBuffered = true;
      SuspendLayout();
      try {
      _defaultAssetPath = settings.DefaultAssetPath;

      AssetsPath = settings.AssetsPath;
      ZoomLevel = settings.ZoomLevel;
      CssFileName = settings.CssFileName;
      CssDarkModeFileName = settings.CssDarkModeFileName;

      ShowToolbar = settings.ShowToolbar;
      ShowStatusbar = settings.ShowStatusbar;

      InitializeComponent();
      InitializeTranslationSettings(settings);

      tbAssetsPath.Text = AssetsPath;
      trackBar1.Value = Math.Max(trackBar1.Minimum, Math.Min(trackBar1.Maximum, ZoomLevel));
      lblZoomValue.Text = $"{ZoomLevel}%";
      tbCssFile.Text = CssFileName;
      tbDarkmodeCssFile.Text = CssDarkModeFileName;
      cbShowToolbar.Checked = ShowToolbar;
      cbShowStatusbar.Checked = ShowStatusbar;

      _originalPlugins = settings.EnabledMarkdownPlugins ?? new string[0];
      try {
      var pluginConfig = File.ReadAllText(settings.DefaultAssetPath + "/markdown/md.extensions.json");
      var plugins = JsonConvert.DeserializeObject<JObject>(pluginConfig)
        ?? throw new JsonException("Expected a Markdown extension object.");
      var pluginItems = plugins
        .Properties()
        .Select(li => {          
          var plugin = li.Value.ToObject<MarkdownPlugin>() ?? throw new JsonException("Invalid Markdown extension.");
          plugin.Id = li.Name;
          return plugin;
        })
        .OrderBy(li => li.Id)
        .ToArray();

      MarkdownPlugins.BeginUpdate();
      try {
        MarkdownPlugins.Items.Clear();
        foreach (var plugin in pluginItems) MarkdownPlugins.Items.Add(plugin, _originalPlugins.Contains(plugin.Id));
      }
      finally { MarkdownPlugins.EndUpdate(); }
      _pluginsLoaded = true;
      }
      catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException) {
        MarkdownPlugins.Enabled = false;
        sblInvalidHtmlPath.Text = "Не удалось прочитать расширения Markdown. Текущий выбор будет сохранён.";
      }
      }
      finally { CompleteInitialLayout(); }
    }

    protected override void OnLoad(EventArgs e)
    {
      base.OnLoad(e);
      ClampToWorkingArea(Screen.FromControl(this).WorkingArea);
    }

    internal void ClampToWorkingArea(Rectangle workingArea)
    {
      if (workingArea.Width <= 0 || workingArea.Height <= 0) return;
      MinimumSize = new Size(Math.Min(MinimumSize.Width, workingArea.Width), Math.Min(MinimumSize.Height, workingArea.Height));
      var width = Math.Min(Width, workingArea.Width);
      var height = Math.Min(Height, workingArea.Height);
      var left = Math.Max(workingArea.Left, Math.Min(Left, workingArea.Right - width));
      var top = Math.Max(workingArea.Top, Math.Min(Top, workingArea.Bottom - height));
      Bounds = new Rectangle(left, top, width, height);
      PerformLayout();
    }

    private void trackBar1_ValueChanged(object sender, EventArgs e)
    {
      ZoomLevel = trackBar1.Value;
      lblZoomValue.Text = $"{ZoomLevel}%";
    }

    private void tbCssFile_TextChanged(object sender, EventArgs e)
    {
      CssFileName = tbCssFile.Text;
    }

    private void tbDarkmodeCssFile_TextChanged(object sender, EventArgs e)
    {
      CssDarkModeFileName = tbDarkmodeCssFile.Text;
    }

    private void btnSave_Click(object sender, EventArgs e)
    {
      var assetsPath = Environment.ExpandEnvironmentVariables(tbAssetsPath.Text.Trim());
      if (assetsPath.Length != 0 && !Directory.Exists(assetsPath)) {
        settingsTabs.SelectedTab = previewPage;
        sblInvalidHtmlPath.Text = "Каталог ресурсов не найден.";
        tbAssetsPath.Focus();
        return;
      }
      if (!ValidateTranslationSettings()) return;
      {
        AssetsPath = assetsPath;
        CssFileName = tbCssFile.Text.Trim();
        CssDarkModeFileName = tbDarkmodeCssFile.Text.Trim();
        ZoomLevel = trackBar1.Value;
        ShowToolbar = cbShowToolbar.Checked;
        ShowStatusbar = cbShowStatusbar.Checked;
        List<string> plugins = new List<string>();
        foreach(MarkdownPlugin item in MarkdownPlugins.CheckedItems) {
          plugins.Add(item.Id);
        }
        AllowedMarkdownPlugins = _pluginsLoaded ? plugins.ToArray() : _originalPlugins;
        DialogResult = DialogResult.OK;
      }
    }

    private void btnCancel_Click(object sender, EventArgs e)
    {
      DialogResult = DialogResult.Cancel;
    }

    private void btnChooseCss_Click(object sender, EventArgs e)
    {
      using (OpenFileDialog openFileDialog = new OpenFileDialog()) {
        openFileDialog.Filter = "css files (*.css)|*.css|All files (*.*)|*.*";
        openFileDialog.RestoreDirectory = true;
        var current = (sender as Button).Name == "btnChooseCss" ? tbCssFile.Text : tbDarkmodeCssFile.Text;
        if (File.Exists(current)) { openFileDialog.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(current)); openFileDialog.FileName = Path.GetFullPath(current); }
        else openFileDialog.InitialDirectory = _defaultAssetPath;
        if (openFileDialog.ShowDialog(this) == DialogResult.OK) {
          if ((sender as Button).Name == "btnChooseCss") {
            CssFileName = openFileDialog.FileName;
            tbCssFile.Text = CssFileName;
          }
          else if ((sender as Button).Name == "btnChooseDarkmodeCss") {
            CssDarkModeFileName = openFileDialog.FileName;
            tbDarkmodeCssFile.Text = CssDarkModeFileName;
          }
        }
      }
    }

    private void button1_Click(object sender, EventArgs e)
    {
      tbCssFile.Text = "";
    }

    private void btnDefaultDarkmodeCss_Click(object sender, EventArgs e)
    {
      tbDarkmodeCssFile.Text = "";
    }

    #region Show Toolbar
    private void cbShowToolbar_Changed(object sender, EventArgs e)
    {
      ShowToolbar = cbShowToolbar.Checked;
    }
        #endregion

    private void cbShowStatusbar_CheckedChanged(object sender, EventArgs e)
    {
      ShowStatusbar = cbShowStatusbar.Checked;
    }

    private void btnDefaultAssetDir_Click(object sender, EventArgs e)
    {
      tbAssetsPath.Text = "";
      AssetsPath = "";
    }

    private void btnChooseAssetsDir_Click(object sender, EventArgs e)
    {
      using (var folderOpenDialog = new FolderBrowserDialog()) {
        folderOpenDialog.SelectedPath = !string.IsNullOrEmpty(AssetsPath) ? AssetsPath : Path.GetFullPath(_defaultAssetPath);

        if (folderOpenDialog.ShowDialog(this) == DialogResult.OK) {
          if (folderOpenDialog.SelectedPath.Replace("\\", "/") == _defaultAssetPath) {
            AssetsPath = string.Empty;
          }
          else {
            AssetsPath = folderOpenDialog.SelectedPath;
          }
          tbAssetsPath.Text = AssetsPath;
        }
      }
    }

    private void tbAssetsPath_Leave(object sender, EventArgs e)
    {
      if (tbAssetsPath.Text != "") {
        if (Directory.Exists(tbAssetsPath.Text)) {
          AssetsPath = tbAssetsPath.Text;
        }
      }
      else {
        AssetsPath = "";
      }
    }

    private class MarkdownPlugin
    {
      public string Id { get; set; }

      [JsonProperty("title")]
      public string Title { get; set; }

      [JsonProperty("description")]
      public string Description { get; set; }

      public override string ToString()
      {
        return (!string.IsNullOrEmpty(Title)) ? Title : Id;
      }
    }

    private string _defaultAssetPath;
    private string[] _originalPlugins;
    private bool _pluginsLoaded;
  }
}
