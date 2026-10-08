using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AnotherMarkdown.Translation
{
  // .NET Framework's HTTP handler does not implement SOCKS. Use only Windows'
  // curl, with a fixed argv and all request data in its quoted stdin config.
  internal sealed class CurlApiHandler : HttpMessageHandler
  {
    private const int MaximumResponseBytes = 32000000;
    private const string TrailerEnd = "__AM_END__";
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly Uri proxyAddress;
    private readonly string username, password;
    private readonly int timeoutSeconds;

    internal CurlApiHandler(Uri proxyAddress, string username, string password, int timeoutSeconds)
    {
      this.proxyAddress = proxyAddress;
      this.username = username ?? "";
      this.password = password ?? "";
      this.timeoutSeconds = timeoutSeconds;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      cancellationToken.ThrowIfCancellationRequested();
      var executable = Path.Combine(Environment.SystemDirectory, "curl.exe");
      if (!File.Exists(executable)) throw new InvalidOperationException("Для SOCKS5 требуется системный curl.exe Windows. Он не найден в системном каталоге.");
      var marker = "__AM_HTTP_" + Guid.NewGuid().ToString("N") + "__";
      var body = request.Content == null ? null : await request.Content.ReadAsStringAsync().ConfigureAwait(false);
      cancellationToken.ThrowIfCancellationRequested();
      var config = CreateConfig(request, body, marker);
      CliCommandResult result;
      try {
        result = await CliCommand.RunAsync(executable, "--disable --config -", config, timeoutSeconds, cancellationToken,
          workingDirectory: Environment.SystemDirectory, providerId: "curl-api", maximumOutputCharacters: MaximumResponseBytes + 128, detectOutputEncoding: false).ConfigureAwait(false);
      }
      catch (OperationCanceledException) { throw; }
      catch (TimeoutException) { throw new TimeoutException("API через SOCKS5 не ответил за " + timeoutSeconds + " секунд."); }
      catch (DecoderFallbackException) { throw new InvalidOperationException("API вернул ответ в недопустимой кодировке UTF-8."); }
      catch (Exception error) when (error is Win32Exception || error is IOException || error is InvalidOperationException || error is UnauthorizedAccessException) {
        throw new InvalidOperationException("Не удалось выполнить запрос API через системный curl. Проверьте SOCKS5-прокси и размер ответа.");
      }
      cancellationToken.ThrowIfCancellationRequested();
      if (result.ExitCode != 0) throw CurlError(result.ExitCode);
      var output = result.StandardOutput ?? "";
      var trailerLength = marker.Length + 3 + TrailerEnd.Length;
      var offset = output.Length - trailerLength;
      int status;
      if (offset < 0 || string.CompareOrdinal(output, offset, marker, 0, marker.Length) != 0 || !output.EndsWith(TrailerEnd, StringComparison.Ordinal)
          || !int.TryParse(output.Substring(offset + marker.Length, 3), NumberStyles.None, CultureInfo.InvariantCulture, out status) || status < 100 || status > 599)
        throw new InvalidOperationException("Системный curl не вернул корректный статус HTTP API.");
      var responseBody = output.Substring(0, offset);
      if (Utf8.GetByteCount(responseBody) > MaximumResponseBytes) throw new InvalidOperationException("Ответ API превышает допустимый размер.");
      return new HttpResponseMessage((HttpStatusCode)status) { RequestMessage = request, Content = new ByteArrayContent(Utf8.GetBytes(responseBody)) };
    }

    private string CreateConfig(HttpRequestMessage request, string body, string marker)
    {
      if (request.RequestUri == null || !request.RequestUri.IsAbsoluteUri
          || request.RequestUri.Scheme != Uri.UriSchemeHttp && request.RequestUri.Scheme != Uri.UriSchemeHttps)
        throw new ArgumentException("Для SOCKS5 требуется абсолютный HTTP(S) URL API.");
      var config = new StringBuilder();
      config.Append("silent\nshow-error\ngloboff\ncompressed\nsocks5-basic\nno-location\nno-netrc\nno-insecure\n");
      Option(config, "proxy", proxyAddress.GetLeftPart(UriPartial.Authority));
      Option(config, "noproxy", "");
      Option(config, "proto", "=http,https");
      Option(config, "proto-redir", "=http,https");
      Option(config, "max-redirs", "0");
      Option(config, "max-time", timeoutSeconds.ToString(CultureInfo.InvariantCulture));
      Option(config, "max-filesize", MaximumResponseBytes.ToString(CultureInfo.InvariantCulture));
      Option(config, "request", request.Method.Method);
      Option(config, "url", request.RequestUri.AbsoluteUri);
      if (username.Length != 0) Option(config, "proxy-user", username + ":" + password);
      foreach (var header in request.Headers) Option(config, "header", header.Key + ": " + string.Join(", ", header.Value));
      if (request.Content != null) foreach (var header in request.Content.Headers) Option(config, "header", header.Key + ": " + string.Join(", ", header.Value));
      if (!request.Headers.Contains("Expect")) Option(config, "header", "Expect:");
      // data-raw never interprets a leading '@' as a local filename.
      if (body != null) Option(config, "data-raw", body);
      Option(config, "write-out", marker + "%{http_code}" + TrailerEnd);
      return config.ToString();
    }

    private static void Option(StringBuilder config, string name, string value)
    {
      config.Append(name).Append(" = \"");
      foreach (var character in value) {
        switch (character) {
          case '\\': config.Append("\\\\"); break;
          case '"': config.Append("\\\""); break;
          case '\r': config.Append("\\r"); break;
          case '\n': config.Append("\\n"); break;
          case '\t': config.Append("\\t"); break;
          case '\v': config.Append("\\v"); break;
          case '\0': throw new ArgumentException("Параметры SOCKS5-запроса не должны содержать нулевой символ.");
          default: config.Append(character); break;
        }
      }
      config.Append("\"\n");
    }

    private static Exception CurlError(int code)
    {
      if (code == 28) return new TimeoutException("Истекло время ожидания API через SOCKS5.");
      if (code == 63) return new InvalidOperationException("Ответ API превышает допустимый размер.");
      if (code == 1 || code == 2 || code == 4) return new InvalidOperationException("Системный curl не поддерживает необходимые параметры SOCKS5.");
      if (code == 35 || code == 51 || code == 58 || code == 59 || code == 60 || code == 64 || code == 66 || code == 77 || code == 80 || code == 82 || code == 83 || code == 90 || code == 91)
        return new InvalidOperationException("Не удалось установить проверенное TLS-соединение с API через SOCKS5. Проверьте сертификат сервера.");
      if (code == 67 || code == 97) return new InvalidOperationException("SOCKS5-прокси отклонил подключение. Проверьте адрес, порт и авторизацию прокси.");
      return new InvalidOperationException("Не удалось подключиться к API через SOCKS5. Проверьте адреса, сеть и параметры прокси.");
    }
  }
}
