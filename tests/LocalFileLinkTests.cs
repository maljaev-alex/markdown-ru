using System;
using System.IO;
using Webview2Viewer.Web;

internal static class LocalFileLinkTests
{
  private static int checks;
  private static int Main()
  {
    try {
      foreach (var extension in new[] { "md", "bsl", "json", "xml", "txt", "cs", "sql", "ini" }) {
        var path = "D:/notes/a+b #23% " + "\u041c\u043e\u0434\u0443\u043b\u044c" + "." + extension;
        foreach (var address in new[] { new Uri(path).AbsoluteUri, "http://local.example/" + HttpUtility2.PathToUri(path) }) {
          foreach (var suffix in new[] { "", ":6531", ":6531:7" }) {
            Check(LocalFileLink.TryParse(new Uri(address + suffix), out var target), "local target accepted: " + extension + suffix);
            Check(Path.GetFullPath(target.Filename) == Path.GetFullPath(path), "exact decoded path retained: " + extension);
            Check(target.Line == (suffix.Length == 0 ? 0 : 6531) && target.Column == (suffix.EndsWith(":7") ? 7 : 0), "one-based source location retained");
          }
        }
      }
      Check(LocalFileLink.TryParse(new Uri("http://local.example/diskD/a+b.json"), out var plus) && plus.Filename == "D:/a+b.json", "literal plus is not decoded as a space");
      Check(LocalFileLink.TryParse(new Uri("file://server/share/a%20b.xml:12"), out var unc) && unc.Filename == @"\\server\share\a b.xml" && unc.Line == 12, "UNC target retained");
      foreach (var address in new[] { "https://example.test/file.json", "http://local.example.evil/diskD/file.xml", "http://local.example/assets/file.xml", "javascript:alert(1)", "data:text/plain,test", "file:///D:/file.bsl:0", "file:///D:/file.bsl:2147483648", "file:///D:/file.bsl:4:0" })
        Check(!LocalFileLink.TryParse(new Uri(address), out var target), "external or invalid target rejected: " + address);
      Console.WriteLine("PASS local file links: " + checks + " assertions"); return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
  }
  private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
}
