using System.Drawing;
using System.Windows.Forms;

namespace AnotherMarkdown.Forms
{
  internal sealed class PluginLogo : Control
  {
    public PluginLogo()
    {
      SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
      BackColor = Color.Transparent;
      TabStop = false;
      AccessibleName = PluginBranding.Name;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
      base.OnPaint(e);
      PluginIcon.Draw(e.Graphics, ClientRectangle);
    }
  }
}
