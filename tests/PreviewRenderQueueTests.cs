using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using System.Windows.Forms;
using MarkdownRu;
using MarkdownRu.Forms;
using Kbg.NppPluginNET.PluginInfrastructure;

internal static class PreviewRenderQueueTests
{
  private static int checks;

  [STAThread]
  private static int Main()
  {
    Application.Idle += Run;
    Application.Run();
    return Environment.ExitCode;
  }

  private static async void Run(object sender, EventArgs args)
  {
    Application.Idle -= Run;
    try {
      var calls = new List<bool>();
      var first = new TaskCompletionSource<bool>();
      var second = new TaskCompletionSource<bool>();
      var release = new TaskCompletionSource<bool>();
      using (var queue = new PreviewRenderQueue(force => {
        calls.Add(force);
        if (calls.Count == 1) { first.SetResult(true); return release.Task; }
        second.TrySetResult(true);
        return Task.CompletedTask;
      })) {
        var type = typeof(MarkdownPanelController);
        var controller = (MarkdownPanelController)FormatterServices.GetUninitializedObject(type);
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        type.GetField("_renderQueue", fields).SetValue(controller, queue);
        type.GetField("_isPanelVisible", fields).SetValue(controller, true);
        Action<SciMsg> modified = flags => controller.OnNotification(new ScNotification {
          Header = new ScNotificationHeader { Code = (uint)SciMsg.SCN_MODIFIED }, ModificationType = (int)flags
        });
        modified(SciMsg.SC_MOD_CHANGESTYLE);
        await Task.Delay(300);
        Check(calls.Count == 0, "a Scintilla styling notification never reads the Markdown buffer");
        modified(SciMsg.SC_MOD_INSERTTEXT);
        controller.RenderMarkdown(true);
        await Started(first.Task);
        Check(calls.Count == 1 && calls[0], "a forced refresh promotes a pending edit");
        modified(SciMsg.SC_MOD_DELETETEXT);
        modified(SciMsg.SC_MOD_INSERTTEXT);
        await Task.Delay(300);
        Check(calls.Count == 1, "a slow renderer cannot overlap the next update");
        release.SetResult(true);
        await Started(second.Task);
        await Task.Delay(300);
        Check(calls.Count == 2 && !calls[1], "in-flight edits coalesce and an idle document stays idle");
        queue.Request(true);
        queue.Clear();
        await Task.Delay(80);
        Check(calls.Count == 2, "hiding the panel clears a pending refresh");
        queue.Request(false);
        queue.Dispose();
        queue.Request(true);
        await Task.Delay(300);
        Check(calls.Count == 2, "disposing the panel stops all scheduled work");
      }
      Console.WriteLine("PASS preview render queue: " + checks + " assertions");
    }
    catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    finally { Application.ExitThread(); }
  }

  private static async Task Started(Task signal)
  {
    if (await Task.WhenAny(signal, Task.Delay(5000)) != signal)
      throw new TimeoutException("The pending render did not start.");
  }

  private static void Check(bool condition, string label)
  {
    if (!condition) throw new Exception(label);
    checks++;
  }
}
