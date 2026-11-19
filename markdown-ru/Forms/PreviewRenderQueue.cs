using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MarkdownRu.Forms
{
  // All requests originate on the Notepad++ UI thread. A one-shot timer keeps
  // native reads out of the notification callback and coalesces typing bursts.
  internal sealed class PreviewRenderQueue : IDisposable
  {
    private readonly Timer timer = new Timer();
    private readonly Func<bool, Task> render;
    private bool? pending;
    private bool rendering;
    private bool disposed;

    internal PreviewRenderQueue(Func<bool, Task> render)
    {
      this.render = render;
      timer.Tick += OnTick;
    }

    internal void Request(bool force)
    {
      if (disposed || pending == true || (pending.HasValue && !force)) return;
      pending = force;
      if (!rendering) StartTimer();
    }

    private void StartTimer()
    {
      timer.Interval = pending == true ? 1 : 200;
      timer.Start();
    }

    private async void OnTick(object sender, EventArgs args)
    {
      timer.Stop();
      if (!pending.HasValue) return;
      var force = pending.Value;
      pending = null;
      rendering = true;
      try { await render(force); }
      catch (Exception error) { Trace.TraceError("Markdown preview update failed: {0}", error); }
      finally {
        rendering = false;
        if (pending.HasValue) StartTimer();
      }
    }

    internal void Clear()
    {
      pending = null;
      timer.Stop();
    }

    public void Dispose()
    {
      if (disposed) return;
      disposed = true;
      Clear();
      timer.Dispose();
    }
  }
}
