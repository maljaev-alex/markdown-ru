namespace AnotherMarkdown.Forms
{
  partial class SettingsForm
  {
    private System.ComponentModel.IContainer components;

    protected override void Dispose(bool disposing)
    {
      if (disposing) { cliDiscoveryGeneration++; CancelModelDiscovery(); CancelApiDiscovery(); components?.Dispose(); }
      base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
      SuspendLayout();
      components = new System.ComponentModel.Container();
      AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
      AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
      Font = new System.Drawing.Font("Segoe UI", 9F);
      ClientSize = new System.Drawing.Size(840, 720);
      MinimumSize = new System.Drawing.Size(640, 480);
      StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
      Name = "SettingsForm";
      Text = "Настройки — " + PluginBranding.Name;
      Icon = PluginIcon.ApplicationIcon();
      MinimizeBox = false;
      ShowInTaskbar = false;
      var root = new System.Windows.Forms.TableLayoutPanel {
        Dock = System.Windows.Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new System.Windows.Forms.Padding(8)
      };
      root.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100));
      root.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 48));
      root.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100));
      root.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
      root.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
      Controls.Add(root);
      var heading = new System.Windows.Forms.FlowLayoutPanel { Dock = System.Windows.Forms.DockStyle.Fill, WrapContents = false };
      heading.Controls.Add(new PluginLogo { Size = new System.Drawing.Size(32, 32), Margin = new System.Windows.Forms.Padding(6, 4, 10, 4) });
      heading.Controls.Add(new System.Windows.Forms.Label {
        Text = PluginBranding.Name, AutoSize = true,
        Font = new System.Drawing.Font("Segoe UI", 11F, System.Drawing.FontStyle.Bold),
        Margin = new System.Windows.Forms.Padding(0, 10, 8, 8)
      });
      root.Controls.Add(heading, 0, 0);
      settingsTabs = new System.Windows.Forms.TabControl { Name = "settingsTabs", Dock = System.Windows.Forms.DockStyle.Fill, TabIndex = 0 };
      previewPage = new System.Windows.Forms.TabPage("Просмотр") { Name = "previewPage", Padding = new System.Windows.Forms.Padding(8), AutoScroll = true };
      translationPage = new System.Windows.Forms.TabPage("Перевод") { Name = "translationPage", Padding = new System.Windows.Forms.Padding(8) };
      settingsTabs.TabPages.AddRange(new[] { previewPage, translationPage });
      root.Controls.Add(settingsTabs, 0, 1);
      var general = new System.Windows.Forms.TableLayoutPanel {
        Name = "previewLayout", Dock = System.Windows.Forms.DockStyle.Fill, ColumnCount = 4, RowCount = 7, AutoScroll = true
      };
      general.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 150));
      general.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100));
      general.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 42));
      general.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 100));
      for (var row = 0; row < 6; row++) general.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
      general.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100));
      previewPage.Controls.Add(general);
      tbAssetsPath = MakeSettingsTextBox("tbAssetsPath", "Каталог ресурсов", 0);
      tbCssFile = MakeSettingsTextBox("tbCssFile", "CSS", 3);
      tbDarkmodeCssFile = MakeSettingsTextBox("tbDarkmodeCssFile", "CSS тёмной темы", 6);
      btnChooseAssetsDir = MakeSettingsButton("btnChooseAssetsDir", "…", 1, btnChooseAssetsDir_Click);
      btnDefaultAssetDir = MakeSettingsButton("btnDefaultAssetDir", "Сбросить", 2, btnDefaultAssetDir_Click);
      btnChooseCss = MakeSettingsButton("btnChooseCss", "…", 4, btnChooseCss_Click);
      btnDefaultCss = MakeSettingsButton("btnDefaultCss", "Сбросить", 5, button1_Click);
      btnChooseDarkmodeCss = MakeSettingsButton("btnChooseDarkmodeCss", "…", 7, btnChooseCss_Click);
      btnDefaultDarkmodeCss = MakeSettingsButton("btnDefaultDarkmodeCss", "Сбросить", 8, btnDefaultDarkmodeCss_Click);
      AddPreviewRow(general, 0, "Каталог ресурсов", tbAssetsPath, btnChooseAssetsDir, btnDefaultAssetDir);
      AddPreviewRow(general, 1, "CSS", tbCssFile, btnChooseCss, btnDefaultCss);
      AddPreviewRow(general, 2, "CSS тёмной темы", tbDarkmodeCssFile, btnChooseDarkmodeCss, btnDefaultDarkmodeCss);
      trackBar1 = new System.Windows.Forms.TrackBar {
        Name = "trackBar1", AccessibleName = "Масштаб", Minimum = 80, Maximum = 800,
        Value = 100, TickStyle = System.Windows.Forms.TickStyle.None, Dock = System.Windows.Forms.DockStyle.Fill, TabIndex = 9
      };
      trackBar1.ValueChanged += trackBar1_ValueChanged;
      lblZoomValue = new System.Windows.Forms.Label { AutoSize = true, Text = "100%", Margin = new System.Windows.Forms.Padding(3, 10, 3, 3) };
      general.Controls.Add(MakeSettingsLabel("Масштаб"), 0, 3);
      general.Controls.Add(trackBar1, 1, 3); general.SetColumnSpan(trackBar1, 2);
      general.Controls.Add(lblZoomValue, 3, 3);
      cbShowToolbar = new System.Windows.Forms.CheckBox { Name = "cbShowToolbar", Text = "Показывать панель инструментов", AutoSize = true, TabIndex = 10 };
      cbShowStatusbar = new System.Windows.Forms.CheckBox { Name = "cbShowStatusbar", Text = "Показывать адреса ссылок в строке состояния", AutoSize = true, TabIndex = 11 };
      general.Controls.Add(cbShowToolbar, 1, 4); general.SetColumnSpan(cbShowToolbar, 3);
      general.Controls.Add(cbShowStatusbar, 1, 5); general.SetColumnSpan(cbShowStatusbar, 3);
      MarkdownPlugins = new System.Windows.Forms.CheckedListBox {
        Name = "MarkdownPlugins", AccessibleName = "Расширения Markdown", Dock = System.Windows.Forms.DockStyle.Fill,
        CheckOnClick = true, IntegralHeight = false, TabIndex = 12
      };
      general.Controls.Add(MakeSettingsLabel("Расширения Markdown"), 0, 6);
      general.Controls.Add(MarkdownPlugins, 1, 6); general.SetColumnSpan(MarkdownPlugins, 3);
      var footer = new System.Windows.Forms.FlowLayoutPanel {
        Dock = System.Windows.Forms.DockStyle.Fill, AutoSize = true,
        FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft, Padding = new System.Windows.Forms.Padding(0, 8, 0, 0)
      };
      btnCancel = new System.Windows.Forms.Button { Name = "btnCancel", Text = "Отмена", AutoSize = true, MinimumSize = new System.Drawing.Size(110, 32), DialogResult = System.Windows.Forms.DialogResult.Cancel, TabIndex = 1 };
      btnSave = new System.Windows.Forms.Button { Name = "btnSave", Text = "Сохранить", AutoSize = true, MinimumSize = new System.Drawing.Size(110, 32), TabIndex = 0 };
      btnSave.Click += btnSave_Click; btnCancel.Click += btnCancel_Click;
      footer.Controls.Add(btnCancel); footer.Controls.Add(btnSave);
      root.Controls.Add(footer, 0, 2);
      AcceptButton = btnSave; CancelButton = btnCancel;
      statusStrip1 = new System.Windows.Forms.StatusStrip { Dock = System.Windows.Forms.DockStyle.Fill, SizingGrip = false };
      sblInvalidHtmlPath = new System.Windows.Forms.ToolStripStatusLabel { Spring = true, TextAlign = System.Drawing.ContentAlignment.MiddleLeft, ForeColor = System.Drawing.Color.Firebrick };
      statusStrip1.Items.Add(sblInvalidHtmlPath); root.Controls.Add(statusStrip1, 0, 3);
      ResumeLayout(true);
    }

    private static System.Windows.Forms.TextBox MakeSettingsTextBox(string name, string caption, int tabIndex) =>
      new System.Windows.Forms.TextBox { Name = name, AccessibleName = caption, Dock = System.Windows.Forms.DockStyle.Fill, TabIndex = tabIndex, Margin = new System.Windows.Forms.Padding(3, 5, 3, 8) };

    private static System.Windows.Forms.Label MakeSettingsLabel(string caption) =>
      new System.Windows.Forms.Label { Text = caption, AutoSize = true, Margin = new System.Windows.Forms.Padding(3, 8, 3, 8) };

    private static System.Windows.Forms.Button MakeSettingsButton(string name, string text, int tabIndex, System.EventHandler click)
    {
      var button = new System.Windows.Forms.Button { Name = name, Text = text, Dock = System.Windows.Forms.DockStyle.Top, Height = 28, TabIndex = tabIndex };
      button.Click += click;
      return button;
    }

    private static void AddPreviewRow(System.Windows.Forms.TableLayoutPanel layout, int row, string caption,
      System.Windows.Forms.Control value, System.Windows.Forms.Control browse, System.Windows.Forms.Control reset)
    {
      layout.Controls.Add(MakeSettingsLabel(caption), 0, row);
      layout.Controls.Add(value, 1, row); layout.Controls.Add(browse, 2, row); layout.Controls.Add(reset, 3, row);
    }

    private System.Windows.Forms.TabControl settingsTabs;
    private System.Windows.Forms.TabPage previewPage, translationPage;
    private System.Windows.Forms.Button btnSave, btnCancel, btnChooseAssetsDir, btnDefaultAssetDir;
    private System.Windows.Forms.Button btnChooseCss, btnDefaultCss, btnChooseDarkmodeCss, btnDefaultDarkmodeCss;
    private System.Windows.Forms.TextBox tbAssetsPath, tbCssFile, tbDarkmodeCssFile;
    private System.Windows.Forms.TrackBar trackBar1;
    private System.Windows.Forms.Label lblZoomValue;
    private System.Windows.Forms.CheckBox cbShowToolbar, cbShowStatusbar;
    private System.Windows.Forms.CheckedListBox MarkdownPlugins;
    private System.Windows.Forms.StatusStrip statusStrip1;
    private System.Windows.Forms.ToolStripStatusLabel sblInvalidHtmlPath;
  }
}
