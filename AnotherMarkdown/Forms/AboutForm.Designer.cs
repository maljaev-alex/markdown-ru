namespace AnotherMarkdown.Forms
{
  partial class AboutForm
  {
    private System.ComponentModel.IContainer components;
    protected override void Dispose(bool disposing)
    {
      if (disposing) components?.Dispose();
      base.Dispose(disposing);
    }
    private void InitializeComponent()
    {
      SuspendLayout();
      components = new System.ComponentModel.Container();
      AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
      AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
      Font = new System.Drawing.Font("Segoe UI", 9F);
      ClientSize = new System.Drawing.Size(720, 600);
      MinimumSize = new System.Drawing.Size(660, 520);
      Text = "О плагине — " + PluginBranding.Name;
      Icon = PluginIcon.ApplicationIcon();
      StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
      ShowInTaskbar = false; MaximizeBox = false;
      var root = new System.Windows.Forms.TableLayoutPanel {
        Dock = System.Windows.Forms.DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new System.Windows.Forms.Padding(16)
      };
      root.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100));
      root.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 56));
      root.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 30));
      root.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100));
      root.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 48));
      Controls.Add(root);
      var heading = new System.Windows.Forms.FlowLayoutPanel { Dock = System.Windows.Forms.DockStyle.Fill, WrapContents = false };
      heading.Controls.Add(new PluginLogo { Size = new System.Drawing.Size(40, 40), Margin = new System.Windows.Forms.Padding(0, 0, 12, 0) });
      heading.Controls.Add(new System.Windows.Forms.Label {
        Text = PluginBranding.Name, AutoSize = true, Font = new System.Drawing.Font("Segoe UI", 13F, System.Drawing.FontStyle.Bold),
        Margin = new System.Windows.Forms.Padding(0, 8, 0, 0)
      });
      root.Controls.Add(heading, 0, 0);
      var repository = new System.Windows.Forms.LinkLabel {
        Name = "repositoryLink", Text = PluginBranding.Repository, AutoSize = true, Margin = new System.Windows.Forms.Padding(0),
        LinkBehavior = System.Windows.Forms.LinkBehavior.HoverUnderline
      };
      repository.LinkClicked += OpenRepository;
      root.Controls.Add(repository, 0, 1);
      tbAbout = new System.Windows.Forms.TextBox {
        Name = "tbAbout", Multiline = true, ReadOnly = true, ScrollBars = System.Windows.Forms.ScrollBars.Vertical,
        Dock = System.Windows.Forms.DockStyle.Fill, Font = Font, BackColor = System.Drawing.SystemColors.Window,
        Margin = new System.Windows.Forms.Padding(0, 0, 0, 8)
      };
      root.Controls.Add(tbAbout, 0, 2);
      btnOk = new System.Windows.Forms.Button {
        Name = "btnOk", Text = "Закрыть", DialogResult = System.Windows.Forms.DialogResult.OK, Size = new System.Drawing.Size(112, 34),
        Anchor = System.Windows.Forms.AnchorStyles.Right, UseVisualStyleBackColor = true
      };
      root.Controls.Add(btnOk, 0, 3);
      AcceptButton = CancelButton = btnOk;
      Name = "AboutForm";
      ResumeLayout(false);
    }
    private System.Windows.Forms.TextBox tbAbout;
    private System.Windows.Forms.Button btnOk;
  }
}
