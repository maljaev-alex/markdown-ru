using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Webview2Viewer.Web
{
  internal static class HttpUtility2
  {
    public static string UriToPath(string path)
    {
      if (path == string.Empty) {
        return path;
      }
      return Regex.Replace(Uri.UnescapeDataString(path), @"^/disk([a-z])/", "$1:/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static string PathToUri(string path)
    {
      if (path == string.Empty) {
        return path;
      }
      path = Path.GetFullPath(path).Replace("\\", "/");
      return UrlPathEncode(Regex.Replace(path, @"^(\w):\/", "disk$1/"));
    }

    public static string UrlPathEncode(string path)
    {
      // Encode each filesystem segment so #, % and ? cannot become URL syntax.
      return string.Join("/", Array.ConvertAll(path.Split('/'), Uri.EscapeDataString));
    }
  }
}
