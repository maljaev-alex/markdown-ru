using AnotherMarkdown.Entities;

namespace AnotherMarkdown.Forms
{
  partial class MarkdownPreviewForm
  {
    public System.Windows.Forms.Panel panel1;

    #region Windows Form Designer generated code

    /// <summary>
    /// Required method for Designer support - do not modify the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
      this.panelPreview = new System.Windows.Forms.Panel();
      this.toolStripContainer1 = new System.Windows.Forms.ToolStripContainer();
      this.panel1 = new System.Windows.Forms.Panel();
      this.tbPreview = new System.Windows.Forms.ToolStrip();
      this.panelPreview.SuspendLayout();
      this.toolStripContainer1.ContentPanel.SuspendLayout();
      this.toolStripContainer1.TopToolStripPanel.SuspendLayout();
      this.toolStripContainer1.SuspendLayout();
      this.SuspendLayout();
      // 
      // panelPreview
      // 
      this.panelPreview.BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle;
      this.panelPreview.Controls.Add(this.toolStripContainer1);
      this.panelPreview.Dock = System.Windows.Forms.DockStyle.Fill;
      this.panelPreview.Location = new System.Drawing.Point(0, 0);
      this.panelPreview.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
      this.panelPreview.Name = "panelPreview";
      this.panelPreview.Size = new System.Drawing.Size(813, 602);
      this.panelPreview.TabIndex = 0;
      // 
      // toolStripContainer1
      // 
      this.toolStripContainer1.BottomToolStripPanelVisible = false;
      // 
      // toolStripContainer1.ContentPanel
      // 
      this.toolStripContainer1.ContentPanel.Controls.Add(this.panel1);
      this.toolStripContainer1.ContentPanel.Size = new System.Drawing.Size(811, 575);
      this.toolStripContainer1.Dock = System.Windows.Forms.DockStyle.Fill;
      this.toolStripContainer1.LeftToolStripPanelVisible = false;
      this.toolStripContainer1.Location = new System.Drawing.Point(0, 0);
      this.toolStripContainer1.Name = "toolStripContainer1";
      this.toolStripContainer1.RightToolStripPanelVisible = false;
      this.toolStripContainer1.Size = new System.Drawing.Size(811, 600);
      this.toolStripContainer1.TabIndex = 2;
      this.toolStripContainer1.Text = "toolStripContainer1";
      // 
      // toolStripContainer1.TopToolStripPanel
      // 
      this.toolStripContainer1.TopToolStripPanel.Controls.Add(this.tbPreview);
      // 
      // panel1
      // 
      this.panel1.Dock = System.Windows.Forms.DockStyle.Fill;
      this.panel1.Location = new System.Drawing.Point(0, 0);
      this.panel1.Name = "panel1";
      this.panel1.Size = new System.Drawing.Size(811, 575);
      this.panel1.TabIndex = 2;
      // 
      // tbPreview
      // 
      this.tbPreview.Dock = System.Windows.Forms.DockStyle.None;
      this.tbPreview.ImageScalingSize = new System.Drawing.Size(20, 20);
      this.tbPreview.Location = new System.Drawing.Point(0, 0);
      this.tbPreview.Name = "tbPreview";
      this.tbPreview.Size = new System.Drawing.Size(811, 25);
      this.tbPreview.Stretch = true;
      this.tbPreview.TabIndex = 0;
      // 
      // MarkdownPreviewForm
      // 
      this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Inherit;
      this.ClientSize = new System.Drawing.Size(813, 602);
      this.Controls.Add(this.panelPreview);
      this.Font = new System.Drawing.Font("Segoe UI", 7.8F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
      this.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
      this.Name = "MarkdownPreviewForm";
      this.Text = "MarkdownPreviewForm";
      this.panelPreview.ResumeLayout(false);
      this.toolStripContainer1.ContentPanel.ResumeLayout(false);
      this.toolStripContainer1.TopToolStripPanel.ResumeLayout(false);
      this.toolStripContainer1.TopToolStripPanel.PerformLayout();
      this.toolStripContainer1.ResumeLayout(false);
      this.toolStripContainer1.PerformLayout();
      this.ResumeLayout(false);
      this.PerformLayout();

    }
        #endregion

    /// <summary>
    /// Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;
    private System.Windows.Forms.Panel panelPreview;
    private System.Windows.Forms.ToolStripContainer toolStripContainer1;
    private System.Windows.Forms.ToolStrip tbPreview;
  }
}