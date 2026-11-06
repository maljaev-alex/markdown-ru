using System;
using System.Text.RegularExpressions;
using PanelCommon;

namespace Webview2Viewer.Web
{
  internal static class LocalFileLink
  {
    public static bool IsLocal(Uri uri) => uri != null && (uri.IsFile ||
      (uri.Scheme == Uri.UriSchemeHttp && uri.Host.Equals("local.example", StringComparison.OrdinalIgnoreCase)));

    public static bool TryParse(Uri uri, out NavigateToEvent target)
    {
      target = default(NavigateToEvent);
      if (!IsLocal(uri)) return false;
      string path;
      if (uri.IsFile) path = uri.LocalPath;
      else {
        var disk = Regex.Match(uri.AbsolutePath, @"^/disk([a-z])/", RegexOptions.IgnoreCase);
        if (!disk.Success) return false;
        // URI paths use literal +, unlike form-encoded query strings.
        path = disk.Groups[1].Value + ":/" + Uri.UnescapeDataString(uri.AbsolutePath.Substring(disk.Length));
      }
      var location = Regex.Match(path, @":([0-9]+)(?::([0-9]+))?$");
      if (location.Success) {
        if (!int.TryParse(location.Groups[1].Value, out target.Line) || target.Line < 1) return false;
        if (location.Groups[2].Success &&
            (!int.TryParse(location.Groups[2].Value, out target.Column) || target.Column < 1)) return false;
        path = path.Substring(0, location.Index);
      }
      if (string.IsNullOrWhiteSpace(path)) return false;
      target.Filename = path;
      return true;
    }
  }
}
