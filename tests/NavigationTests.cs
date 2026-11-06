using System;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Runtime.Serialization;
using System.Text;
using AnotherMarkdown;
using AnotherMarkdown.Entities;
using Kbg.NppPluginNET.PluginInfrastructure;
using PanelCommon;

internal static class NavigationTests
{
  private static int checks;
  private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
  private sealed class Scintilla : RealProxy
  {
    public readonly string Text;
    public readonly Encoding Encoding;
    public int Anchor, Focus, FirstVisible, RevealCount;
    public Scintilla(string text, Encoding encoding) : base(typeof(IScintillaGateway)) { Text = text; Encoding = encoding; }
    public int Bytes(int units) => Encoding.GetByteCount(Text.Substring(0, units));
    public int Units(int bytes) => Encoding.GetString(Encoding.GetBytes(Text), 0, bytes).Length;
    private int Line(int position) { var prefix = Text.Substring(0, Units(position)); return prefix.Split('\n').Length - 1; }
    private int LineStart(int line) { int at = 0; while (line-- > 0) at = Text.IndexOf('\n', at) + 1; return Bytes(at); }
    public override IMessage Invoke(IMessage message)
    {
      var call = (IMethodCallMessage)message; var a = call.Args; object result = null;
      switch (call.MethodName) {
        case "PositionRelativeCodeUnits": result = Bytes((int)a[1]); break;
        case "CountCodeUnits": result = Units((int)a[1]) - Units((int)a[0]); break;
        case "SetAnchor": Anchor = (int)a[0]; break;
        case "SetCurrentPos": Focus = (int)a[0]; break;
        case "GetAnchor": result = Anchor; break;
        case "GetCurrentPos": result = Focus; break;
        case "GetFirstVisibleLine": result = FirstVisible; break;
        case "SetFirstVisibleLine": FirstVisible = (int)a[0]; break;
        case "LineFromPosition": result = Line((int)a[0]); break;
        case "PositionFromLine": result = LineStart((int)a[0]); break;
        case "DocLineFromVisible": result = (int)a[0] / 2; break;
        case "VisibleFromDocLine": result = (int)a[0] * 2; break;
        case "EnsureVisible": break;
        case "ScrollCaret": RevealCount++; break;
        default: throw new Exception("Navigation must not call " + call.MethodName);
      }
      return new ReturnMessage(result, null, 0, call.LogicalCallContext, call);
    }
  }
  private static void Main()
  {
    var type = typeof(MarkdownPanelController);
    var controller = (MarkdownPanelController)FormatterServices.GetUninitializedObject(type);
    var settings = new Settings();
    type.GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(controller, settings);
    var apply = type.GetMethod("ApplyNavigation", BindingFlags.Instance | BindingFlags.NonPublic);
    foreach (var encoding in new[] { Encoding.UTF8, Encoding.GetEncoding(1251) }) {
      var text = "# Title\r\n\r\n\u041f\u0440\u0438\u0432\u0435\u0442 text.\r\nLast line.\r\n";
      var native = new Scintilla(text, encoding);
      var snapshot = new EditorSnapshot { Session = "one", Revision = 1, Text = text, ReadOnly = true };
      var start = text.IndexOf("\u041f\u0440\u0438\u0432\u0435\u0442", StringComparison.Ordinal);
      var state = new EditorNavigation { Session = snapshot.Session, Anchor = start + 6, Focus = start, Selection = true, Reveal = true };
      Action run = () => apply.Invoke(controller, new object[] { native.GetTransparentProxy(), snapshot, state });
      run();
      Check(native.Anchor == native.Bytes(start + 6) && native.Focus == native.Bytes(start), "reverse UTF-16 selection converts to Scintilla offsets in " + encoding.WebName);
      Check(native.RevealCount == 1, "cursor navigation can reveal a read-only source without editing it");
      state.Selection = false; state.Scroll = true; state.Top = text.IndexOf("Last", StringComparison.Ordinal); run();
      Check(native.FirstVisible == 6 && native.Focus == native.Bytes(start), "scroll maps document lines to wrapped display lines without moving the caret");
      settings.SyncViewWithCaretPosition = false; state.Selection = true; state.Anchor = 0; state.Focus = 0; run();
      Check(native.Focus == native.Bytes(start), "disabled selection sync leaves caret unchanged while scroll is enabled");
      settings.SyncViewWithCaretPosition = true; settings.SyncViewWithFirstVisibleLine = false; state.Top = 0; state.Anchor = -100; state.Focus = int.MaxValue; run();
      Check(native.Anchor == 0 && native.Focus == native.Bytes(text.Length) && native.FirstVisible == 6, "offsets clamp safely and selection works with scroll disabled");
      settings.SyncViewWithFirstVisibleLine = true;
    }
    var emoji = new Scintilla("A\ud83d\ude00B", Encoding.UTF8);
    apply.Invoke(controller, new object[] { emoji.GetTransparentProxy(), new EditorSnapshot { Session = "emoji", Text = emoji.Text },
      new EditorNavigation { Anchor = 2, Focus = 2, Selection = true } });
    Check(emoji.Anchor == 1 && emoji.Focus == 1, "a malformed source offset never splits a surrogate pair");
    Console.WriteLine("PASS native navigation: " + checks + " assertions");
  }
}
