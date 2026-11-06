using System;
using System.IO;
using Webview2Viewer.Web;

internal static class LocalFilePathTests
{
  private static int checks;

  private static int Main()
  {
    try {
      foreach (var path in new[] {
        @"D:\notes\Review #45.md",
        @"D:\notes\Review ##96.MD",
        @"D:\notes #100%\a+b %23 final.md",
        @"D:\notes\literal%20space.md",
        @"D:\notes\rules #45.mdc",
        "D:\\notes\\\u0420\u0435\u0432\u044c\u044e \u0437\u0430\u0434\u0430\u0447\u0438 #45.md",
        @"D:\notes\ordinary.md"
      }) {
        var documentUri = "/" + HttpUtility2.PathToUri(path);
        var request = new Uri("http://local.example" + documentUri);
        Check(request.Fragment == "" && request.Query == "", "filename remains entirely in the request path: " + Path.GetFileName(path));
        Check(request.AbsolutePath == documentUri, "request matches the in-memory document URI: " + Path.GetFileName(path));
        Check(HttpUtility2.UriToPath(request.AbsolutePath) == path.Replace('\\', '/'), "request resolves to the original file: " + Path.GetFileName(path));
        Check(Path.GetExtension(request.AbsolutePath).Equals(Path.GetExtension(path), StringComparison.OrdinalIgnoreCase), "renderer and MIME lookup retain the file extension");
      }
      Check(HttpUtility2.UrlPathEncode("themes/100%/dark #1+.css") == "themes/100%25/dark%20%231%2B.css", "CSS and base paths escape reserved characters while preserving separators");
      Check(HttpUtility2.PathToUri("") == "" && HttpUtility2.UriToPath("") == "", "empty paths retain their existing behavior");
      Console.WriteLine("PASS local file paths: " + checks + " assertions");
      return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
  }

  private static void Check(bool condition, string label)
  {
    if (!condition) throw new Exception("FAIL " + label);
    checks++;
  }
}
