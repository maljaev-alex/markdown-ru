using System;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Runtime.Serialization;
using System.Text;
using MarkdownRu;
using MarkdownRu.Entities;
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
    public int Anchor, Focus, FirstVisible, RevealCount, XOffset, FirstVisibleAtReveal;
    public Wrap WrapMode = Wrap.WORD;
    public Scintilla(string text, Encoding encoding) : base(typeof(IScintillaGateway)) { Text = text; Encoding = encoding; }
    public int Bytes(int units) => Encoding.GetByteCount(Text.Substring(0, units));
    public int Units(int bytes) => Encoding.GetString(Encoding.GetBytes(Text), 0, bytes).Length;
    private int Line(int position) { var prefix = Text.Substring(0, Units(position)); return prefix.Split('\n').Length - 1; }
    private int LineStart(int line) { int at = 0; while (line-- > 0) at = Text.IndexOf('\n', at) + 1; return Bytes(at); }
    private int DisplayLine(int position) {
      var line = Line(position);
      return WrapMode == Wrap.NONE ? line : 2 * line + (Units(position) - Units(LineStart(line)) >= 6 ? 1 : 0);
    }
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
        case "DocLineFromVisible": result = (int)a[0] / (WrapMode == Wrap.NONE ? 1 : 2); break;
        case "VisibleFromDocLine": result = (int)a[0] * (WrapMode == Wrap.NONE ? 1 : 2); break;
        case "GetWrapMode": result = WrapMode; break;
        case "WrapCount": result = WrapMode == Wrap.NONE ? 1 : 2; break;
        case "PointYFromPosition": result = (DisplayLine((int)a[0]) - FirstVisible) * 16; break;
        case "PointXFromPosition": result = 32 + (Units((int)a[0]) - Units(LineStart(Line((int)a[0])))) * 8 - XOffset; break;
        case "TextHeight": result = 16; break;
        case "LinesOnScreen": result = 10; break;
        case "GetXOffset": result = XOffset; break;
        case "SetXOffset": XOffset = (int)a[0]; break;
        case "GetMarginLeft": result = 2; break;
        case "GetMargins": result = 1; break;
        case "GetMarginWidthN": result = 30; break;
        case "PositionFromPoint": {
          var line = WrapMode == Wrap.NONE ? FirstVisible : FirstVisible / 2;
          var column = (XOffset + (int)a[0] - 32) / 8 + (WrapMode == Wrap.NONE ? 0 : FirstVisible % 2 * 6);
          result = Bytes(Math.Min(Text.Length, Units(LineStart(line)) + column)); break;
        }
        case "EnsureVisible": break;
        case "ScrollRange": FirstVisibleAtReveal = FirstVisible; RevealCount++; break;
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
      state.Top += 7; run();
      var last = (EditorNavigation)type.GetField("_lastNavigation", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
      Check(native.FirstVisible == 7 && last.Top == state.Top - 1, "the second visual row is placed and sampled inside the same document line");
      state.Selection = true; state.Reveal = true; state.Top -= 7; run();
      Check(native.FirstVisibleAtReveal == 6, "a simultaneous viewport update is applied before revealing the active selection end");
      settings.SyncViewWithCaretPosition = false; state.Selection = true; state.Anchor = 0; state.Focus = 0; run();
      Check(native.Focus == native.Bytes(start), "disabled selection sync leaves caret unchanged while scroll is enabled");
      settings.SyncViewWithCaretPosition = true; settings.SyncViewWithFirstVisibleLine = false; state.Top = 0; state.Anchor = -100; state.Focus = int.MaxValue; run();
      Check(native.Anchor == 0 && native.Focus == native.Bytes(text.Length) && native.FirstVisible == 6, "offsets clamp safely and selection works with scroll disabled");
      native.WrapMode = Wrap.NONE; native.FirstVisible = 2; native.XOffset = 32; run();
      last = (EditorNavigation)type.GetField("_lastNavigation", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
      Check(last.Top == start + 4, "an unwrapped horizontally panned viewport reports its first visible character");
      settings.SyncViewWithFirstVisibleLine = true;
      int reveals = native.RevealCount, caret = native.Focus;
      state.Selection = false; state.Scroll = true; state.Reveal = false;
      state.Top = start; state.NextTop = text.IndexOf("Last", StringComparison.Ordinal); state.TopFraction = .75; run();
      Check(native.FirstVisible == 2 && native.XOffset == 32 && native.RevealCount == reveals && native.Focus == caret,
        "vertical viewport updates preserve a manually panned X offset and never reveal text or move the caret");
      state.Scroll = false; state.Left = start + 7; run();
      Check(native.XOffset == 56 && native.FirstVisible == 2 && native.Focus == caret, "explicit horizontal panning changes only X");
      state.Left = null; state.Scroll = true; native.WrapMode = Wrap.WORD; native.XOffset = 0;
      state.TopFraction = .25; run(); int before = native.FirstVisible;
      state.TopFraction = .75; run();
      Check(before == 4 && native.FirstVisible == 5 && native.RevealCount == reveals,
        "progress within a rendered row advances through wrapped source lines without revealing the caret");
    }
    var emoji = new Scintilla("A\ud83d\ude00B", Encoding.UTF8);
    apply.Invoke(controller, new object[] { emoji.GetTransparentProxy(), new EditorSnapshot { Session = "emoji", Text = emoji.Text },
      new EditorNavigation { Anchor = 2, Focus = 2, Selection = true } });
    Check(emoji.Anchor == 1 && emoji.Focus == 1, "a malformed source offset never splits a surrogate pair");
    Console.WriteLine("PASS native navigation: " + checks + " assertions");
  }
}
