using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnotherMarkdown.Translation
{
  public sealed class ApiTranslator
  {
    private const int MaximumResponseBytes = 32000000;
    private const int MaximumConfigurationCharacters = 128000;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Regex HeaderName = new Regex(@"^[!#$%&'*+.^_`|~0-9A-Za-z-]+$", RegexOptions.Compiled);
    private static readonly HashSet<string> ProtectedParameters = new HashSet<string>(new[] {
      "model", "messages", "input", "contents", "instructions", "system", "systemInstruction",
      "tools", "tool_choice", "toolChoice", "tool_config", "toolConfig", "functions", "function_call",
      "stream", "stream_options", "store", "background", "previous_response_id", "conversation", "prompt",
      "max_tokens", "max_completion_tokens", "max_output_tokens"
    }, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ToolParameters = new HashSet<string>(new[] {
      "tools", "tool_choice", "toolChoice", "tool_config", "toolConfig", "functions", "function_call", "stream", "stream_options"
    }, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ManagedHeaders = new HashSet<string>(new[] {
      "Authorization", "Proxy-Authorization", "x-api-key", "x-goog-api-key", "Host", "Content-Length",
      "Content-Type", "Transfer-Encoding", "Connection", "Cookie", "Set-Cookie"
    }, StringComparer.OrdinalIgnoreCase);

    private sealed class PreparedConnection
    {
      public string Protocol, Model, Effort, ApiKey, AuthHeader, AuthPrefix, TokenLimitParameter;
      public Uri Endpoint;
      public string BasePath;
      public JObject Headers, Parameters;
      public int MaxOutputTokens;
      public double? Temperature;
      public PreparedProxy Proxy;
    }

    private sealed class PreparedProxy
    {
      public string Mode, Username, Password;
      public Uri Address;
      public bool UseDefaultCredentials;
    }

    // System resolution is delegated, while credentials belong to this
    // connection, never the global proxy. Framework's unconditional loopback
    // bypass is rejected during validation for an explicitly selected proxy.
    private sealed class ConnectionProxy : IWebProxy
    {
      private readonly IWebProxy resolver;
      private readonly Uri fixedAddress;
      public ICredentials Credentials { get; set; }
      public ConnectionProxy(IWebProxy resolver, Uri fixedAddress, ICredentials credentials)
      {
        this.resolver = resolver; this.fixedAddress = fixedAddress; Credentials = credentials;
      }
      public Uri GetProxy(Uri destination) => fixedAddress ?? resolver?.GetProxy(destination) ?? destination;
      public bool IsBypassed(Uri destination) => fixedAddress == null && (resolver?.IsBypassed(destination) ?? true);
    }

    public static void Validate(ApiConnection connection) { ValidateOutputBudget(Prepare(connection)); }

    internal static void ValidateDraft(ApiConnection connection)
    {
      if (connection == null) return;
      var draft = connection.Copy();
      if (string.IsNullOrWhiteSpace(draft.Endpoint)) draft.Endpoint = "https://example.invalid/v1";
      Prepare(draft);
    }

    internal static void ValidateProxyDraft(ApiConnection connection)
    {
      if (connection != null) PrepareProxy(connection);
    }

    private static PreparedProxy PrepareProxy(ApiConnection connection)
    {
      if (connection == null) throw new ArgumentException("Выберите подключение API.");
      var mode = (connection.ProxyMode ?? "system").Trim().ToLowerInvariant();
      if (mode != "system" && mode != "direct" && mode != "custom")
        throw new ArgumentException("Выберите системный прокси, прямое подключение или заданный HTTP-прокси.");
      var address = (connection.ProxyAddress ?? "").Trim();
      ValidateEndpointCredentials(address);
      Uri proxy = null;
      if (mode == "custom" && address.Length != 0 && (ContainsControl(address) || !Uri.TryCreate(address, UriKind.Absolute, out proxy)
          || proxy.Scheme != Uri.UriSchemeHttp || string.IsNullOrEmpty(proxy.Host) || proxy.Port < 1 || proxy.Port > 65535
          || proxy.UserInfo.Length != 0 || proxy.AbsolutePath != "/" || address.IndexOfAny(new[] { '?', '#' }) >= 0))
        throw new ArgumentException("Поддерживается только HTTP-прокси вида http://сервер:порт без логина, пароля, пути, query и fragment. Авторизацию задайте в отдельных полях. SOCKS и HTTPS-прокси не поддерживаются.");
      if (mode == "custom" && proxy == null) throw new ArgumentException("Укажите адрес заданного HTTP-прокси вида http://сервер:порт.");
      Uri destination;
      if (mode == "custom" && Uri.TryCreate((connection.Endpoint ?? "").Trim(), UriKind.Absolute, out destination)
          && (destination.Scheme == Uri.UriSchemeHttp || destination.Scheme == Uri.UriSchemeHttps) && destination.IsLoopback)
        throw new ArgumentException(".NET Framework обходит заданный прокси для локальных API (localhost/loopback). Выберите прямое подключение для локального API: запрос через заданный прокси не отправлен.");
      return new PreparedProxy { Mode = mode, Address = proxy, Username = connection.ProxyUsername ?? "", Password = connection.ProxyPassword ?? "",
        UseDefaultCredentials = connection.ProxyUseDefaultCredentials };
    }

    internal static HttpClientHandler CreateHttpHandler(ApiConnection connection) => CreateHttpHandler(PrepareProxy(connection));

    private static HttpClientHandler CreateHttpHandler(PreparedProxy proxy)
    {
      var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, SslProtocols = SslProtocols.Tls12,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate, UseProxy = proxy.Mode != "direct",
        UseDefaultCredentials = false, Credentials = null, DefaultProxyCredentials = null };
      if (proxy.Mode == "system") {
        handler.Proxy = new ConnectionProxy(WebRequest.DefaultWebProxy, null, proxy.UseDefaultCredentials ? CredentialCache.DefaultCredentials : null);
      }
      else if (proxy.Mode == "custom") {
        var credentials = proxy.UseDefaultCredentials ? CredentialCache.DefaultCredentials
          : proxy.Username.Length == 0 ? null : new NetworkCredential(proxy.Username, proxy.Password);
        var configured = new WebProxy(proxy.Address, false) { Credentials = credentials };
        handler.Proxy = new ConnectionProxy(configured, configured.Address, configured.Credentials);
      }
      return handler;
    }

    public async Task<string> TranslateAsync(string markdown, ApiConnection connection, int timeoutSeconds, CancellationToken token)
    {
      token.ThrowIfCancellationRequested();
      RequireCredentialsAvailable(connection);
      var prepared = Prepare(connection);
      ValidateTimeout(timeoutSeconds);
      ValidateOutputBudget(prepared);
      if (string.IsNullOrWhiteSpace(prepared.Model)) throw new ArgumentException("Укажите модель API.");
      if (string.IsNullOrWhiteSpace(markdown)) throw new ArgumentException("Документ пуст.");
      if (markdown.Length > 1000000) throw new ArgumentException("Документ превышает 1 млн символов. Разделите его на части.");
      var body = CreateBody(prepared, CliTranslator.CreatePrompt(markdown));
      var response = await SendAsync(prepared, HttpMethod.Post, OperationUri(prepared), body, timeoutSeconds, token).ConfigureAwait(false);
      var translated = ExtractTranslation(response, prepared.Protocol).TrimStart('\uFEFF').Trim('\r', '\n');
      if (string.IsNullOrWhiteSpace(translated)) throw new InvalidOperationException("API вернул пустой перевод.");
      return translated;
    }

    public async Task<CliModelCatalog> LoadModelsAsync(ApiConnection connection, int timeoutSeconds, CancellationToken token)
    {
      token.ThrowIfCancellationRequested();
      RequireCredentialsAvailable(connection);
      var prepared = Prepare(connection);
      ValidateTimeout(timeoutSeconds);
      var catalog = new CliModelCatalog();
      var known = new HashSet<string>(StringComparer.Ordinal);
      var cursors = new HashSet<string>(StringComparer.Ordinal);
      using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token)) {
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try {
          string cursor = null;
          for (var page = 0; ; page++) {
            if (page >= 100) throw new InvalidOperationException("Список моделей API содержит слишком много страниц.");
            var uri = BuildUri(prepared, "/models", cursor == null ? null : prepared.Protocol == "gemini" ? "pageToken" : "after_id", cursor);
            var root = await SendAsync(prepared, HttpMethod.Get, uri, null, timeoutSeconds, timeout.Token).ConfigureAwait(false);
            var entries = root[prepared.Protocol == "gemini" ? "models" : "data"] as JArray;
            if (entries == null) throw new InvalidOperationException("API не вернул распознаваемый список моделей.");
            if (entries.Count > 20000) throw new InvalidOperationException("Страница списка моделей API слишком большая.");
            foreach (var entry in entries.OfType<JObject>()) {
              timeout.Token.ThrowIfCancellationRequested();
              if (prepared.Protocol == "gemini" && !(entry["supportedGenerationMethods"] is JArray methods && methods.Any(m => Text(m) == "generateContent"))) continue;
              var id = Text(entry[prepared.Protocol == "gemini" ? "name" : "id"]);
              if (string.IsNullOrWhiteSpace(id) || !known.Add(id)) continue;
              if (known.Count > 20000) throw new InvalidOperationException("Список моделей API слишком большой.");
              var name = Text(entry[prepared.Protocol == "gemini" ? "displayName" : "display_name"]) ?? id;
              var selected = SameModel(id, prepared.Model, prepared.Protocol);
              catalog.Models.Add(new CliModel { Id = id, Name = name, IsDefault = selected });
              if (selected) catalog.DefaultModelId = id;
            }
            if (prepared.Protocol == "gemini") cursor = Text(root["nextPageToken"]);
            else if (prepared.Protocol == "anthropic" && Boolean(root["has_more"])) {
              cursor = Text(root["last_id"]);
              if (string.IsNullOrWhiteSpace(cursor)) throw new InvalidOperationException("API не вернул указатель следующей страницы моделей.");
            }
            else cursor = null;
            if (string.IsNullOrWhiteSpace(cursor)) break;
            if (!cursors.Add(cursor)) throw new InvalidOperationException("API повторил страницу списка моделей.");
          }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) {
          throw new TimeoutException("API не ответил за " + timeoutSeconds + " секунд.");
        }
      }
      catalog.Note = catalog.Models.Count == 0 ? "API не вернул доступных моделей для перевода." : "Моделей получено от API: " + catalog.Models.Count;
      return catalog;
    }

    private static PreparedConnection Prepare(ApiConnection connection)
    {
      if (connection == null) throw new ArgumentException("Выберите подключение API.");
      var proxy = PrepareProxy(connection);
      var protocol = (connection.Protocol ?? "").Trim().ToLowerInvariant();
      if (protocol != "chat-completions" && protocol != "responses" && protocol != "anthropic" && protocol != "gemini")
        throw new ArgumentException("Выберите поддерживаемый протокол API.");
      var endpointText = (connection.Endpoint ?? "").Trim();
      ValidateEndpointCredentials(endpointText);
      Uri endpoint;
      if (ContainsControl(endpointText) || !Uri.TryCreate(endpointText, UriKind.Absolute, out endpoint) ||
          (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps) ||
          string.IsNullOrWhiteSpace(endpoint.Host) || endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
        throw new ArgumentException("Укажите абсолютный HTTP(S) URL API без логина, пароля и фрагмента.");
      var prepared = new PreparedConnection {
        Protocol = protocol, Endpoint = endpoint, Model = (connection.Model ?? "").Trim(),
        Effort = (connection.ReasoningEffort ?? "").Trim().ToLowerInvariant(), ApiKey = connection.ApiKey ?? "",
        AuthHeader = (connection.AuthHeader ?? "").Trim(), AuthPrefix = connection.AuthPrefix ?? "",
        Headers = ReadObject(connection.AdditionalHeadersJson, "Дополнительные заголовки"),
        Parameters = ReadObject(connection.AdditionalParametersJson, "Дополнительные параметры"),
        MaxOutputTokens = connection.MaxOutputTokens, Temperature = connection.Temperature,
        TokenLimitParameter = (connection.TokenLimitParameter ?? "max_tokens").Trim(), Proxy = proxy
      };
      if (prepared.MaxOutputTokens < 0 || prepared.MaxOutputTokens > 2000000)
        throw new ArgumentException("Лимит выходных токенов должен быть от 0 до 2000000. Ноль — настройки сервиса.");
      var maximumTemperature = protocol == "anthropic" ? 1 : 2;
      if (prepared.Temperature.HasValue && (double.IsNaN(prepared.Temperature.Value) || double.IsInfinity(prepared.Temperature.Value) || prepared.Temperature < 0 || prepared.Temperature > maximumTemperature))
        throw new ArgumentException("Температура должна быть от 0 до " + maximumTemperature + ".");
      if (ContainsControl(prepared.Model) || ContainsControl(prepared.ApiKey) || ContainsControl(prepared.AuthPrefix))
        throw new ArgumentException("Модель и параметры авторизации не должны содержать управляющие символы.");
      if (protocol == "chat-completions" && prepared.TokenLimitParameter != "max_tokens" && prepared.TokenLimitParameter != "max_completion_tokens")
        throw new ArgumentException("Выберите max_tokens или max_completion_tokens для лимита Chat Completions.");
      if (prepared.AuthHeader.Length != 0 && (!HeaderName.IsMatch(prepared.AuthHeader) || IsTransportHeader(prepared.AuthHeader)))
        throw new ArgumentException("Имя заголовка авторизации API недопустимо.");
      if (prepared.Effort.Length != 0) {
        var allowed = protocol == "anthropic" ? new[] { "low", "medium", "high", "xhigh", "max" }
          : protocol == "gemini" ? new[] { "minimal", "low", "medium", "high" }
          : new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max" };
        if (!allowed.Contains(prepared.Effort)) throw new ArgumentException("Уровень reasoning не поддерживается выбранным протоколом. Оставьте поле пустым для настроек модели по умолчанию.");
      }
      var headers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      foreach (var property in prepared.Headers.Properties()) {
        if (!HeaderName.IsMatch(property.Name) || ManagedHeaders.Contains(property.Name) ||
            string.Equals(property.Name, prepared.AuthHeader, StringComparison.OrdinalIgnoreCase) || !headers.Add(property.Name) ||
            property.Value.Type != JTokenType.String || ContainsControl((string)property.Value))
          throw new ArgumentException("Дополнительные заголовки должны содержать уникальные допустимые имена и строковые значения. Авторизацию задайте в отдельных полях.");
      }
      ValidateAdditionalParameters(prepared.Parameters, true);
      prepared.BasePath = BasePath(endpoint, protocol);
      // Validate controlled merges before any network request, including model discovery.
      CreateBody(prepared, "validation");
      return prepared;
    }

    internal static void ValidateEndpointCredentials(string endpoint)
    {
      var text = (endpoint ?? "").Trim();
      var queryStart = text.IndexOf('?');
      var fragmentStart = text.IndexOf('#');
      // Drafts can omit their scheme or be malformed. Credential checks must
      // still run before such a draft reaches plaintext configuration storage.
      if (queryStart >= 0 && (fragmentStart < 0 || queryStart < fragmentStart)) {
        var queryEnd = fragmentStart < 0 ? text.Length : fragmentStart;
        foreach (var parameter in text.Substring(queryStart + 1, queryEnd - queryStart - 1).Split('&')) {
          var separator = parameter.IndexOf('=');
          var encodedName = separator < 0 ? parameter : parameter.Substring(0, separator);
          var name = Uri.UnescapeDataString(encodedName.Replace("+", " "));
          if (new[] { "key", "api_key", "api-key", "apikey", "access_token" }.Contains(name, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Ключ нельзя указывать в URL API. Используйте отдельное поле ключа API.");
        }
      }
      Uri uri;
      if (Uri.TryCreate(text, UriKind.Absolute, out uri) && uri.UserInfo.Length != 0)
        throw new ArgumentException("Авторизацию нельзя указывать в URL API. Используйте отдельное поле ключа API.");
      var scheme = Regex.Match(text, @"^[A-Za-z][A-Za-z0-9+.-]*://");
      var authorityStart = scheme.Success ? scheme.Length : text.StartsWith("//", StringComparison.Ordinal) ? 2 : 0;
      var authorityEnd = text.IndexOfAny(new[] { '/', '\\', '?', '#' }, authorityStart);
      if (authorityEnd < 0) authorityEnd = text.Length;
      if (text.Substring(authorityStart, authorityEnd - authorityStart).IndexOf('@') >= 0)
        throw new ArgumentException("Авторизацию нельзя указывать в URL API. Используйте отдельное поле ключа API.");
    }

    private static void ValidateOutputBudget(PreparedConnection connection)
    {
      if (connection.Protocol == "anthropic" && connection.MaxOutputTokens == 0)
        throw new ArgumentException("Anthropic требует явный лимит выходных токенов. Задайте его в дополнительных параметрах.");
    }

    private static void ValidateTimeout(int timeoutSeconds)
    {
      if (timeoutSeconds < 1 || timeoutSeconds > 3600) throw new ArgumentException("Тайм-аут API должен быть от 1 до 3600 секунд.");
    }

    private static void RequireCredentialsAvailable(ApiConnection connection)
    {
      if (!string.IsNullOrEmpty(connection?.CredentialError))
        throw new InvalidOperationException("Не удалось расшифровать сохранённые данные авторизации API. Введите их заново или явно очистите.");
    }

    private static bool ContainsControl(string value) => value != null && value.Any(c => char.IsControl(c));

    private static bool IsTransportHeader(string name) => new[] {
      "Host", "Content-Length", "Content-Type", "Transfer-Encoding", "Connection", "Cookie", "Set-Cookie", "Proxy-Authorization"
    }.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static JObject ReadObject(string json, string label)
    {
      if (string.IsNullOrWhiteSpace(json)) return new JObject();
      if (json.Length > MaximumConfigurationCharacters) throw new ArgumentException(label + ": JSON слишком большой.");
      try {
        using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None, MaxDepth = 32 }) {
          var token = JToken.ReadFrom(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
          if (!(token is JObject result) || reader.Read()) throw new JsonReaderException();
          return result;
        }
      }
      catch (JsonException) { throw new ArgumentException(label + ": требуется корректный JSON-объект без повторяющихся полей."); }
    }

    private static void ValidateAdditionalParameters(JToken token, bool topLevel)
    {
      if (token is JObject obj) foreach (var property in obj.Properties()) {
        if ((topLevel && ProtectedParameters.Contains(property.Name)) || ToolParameters.Contains(property.Name))
          throw new ArgumentException("Дополнительные параметры не могут заменять модель, запрос, лимит токенов, инструменты, streaming или состояние диалога.");
        ValidateAdditionalParameters(property.Value, false);
      }
      else if (token is JArray array) foreach (var item in array) ValidateAdditionalParameters(item, false);
      else if (token.Type == JTokenType.Float && (double.IsNaN((double)token) || double.IsInfinity((double)token)))
        throw new ArgumentException("Дополнительные параметры содержат недопустимое число.");
    }

    private static JObject CreateBody(PreparedConnection connection, string prompt)
    {
      JObject body;
      switch (connection.Protocol) {
        case "responses":
          body = new JObject { ["model"] = connection.Model, ["input"] = prompt, ["store"] = false, ["stream"] = false, ["tools"] = new JArray() };
          if (connection.MaxOutputTokens > 0) body["max_output_tokens"] = connection.MaxOutputTokens;
          if (connection.Effort.Length != 0) body["reasoning"] = new JObject { ["effort"] = connection.Effort };
          break;
        case "anthropic":
          body = new JObject { ["model"] = connection.Model, ["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = prompt }),
            ["max_tokens"] = connection.MaxOutputTokens, ["stream"] = false };
          if (connection.Effort.Length != 0) body["output_config"] = new JObject { ["effort"] = connection.Effort };
          break;
        case "gemini":
          var generation = new JObject();
          if (connection.MaxOutputTokens > 0) generation["maxOutputTokens"] = connection.MaxOutputTokens;
          if (connection.Temperature.HasValue) generation["temperature"] = connection.Temperature.Value;
          if (connection.Effort.Length != 0) generation["thinkingConfig"] = new JObject { ["thinkingLevel"] = connection.Effort.ToUpperInvariant() };
          body = new JObject { ["contents"] = new JArray(new JObject { ["role"] = "user", ["parts"] = new JArray(new JObject { ["text"] = prompt }) }), ["generationConfig"] = generation };
          break;
        default:
          body = new JObject { ["model"] = connection.Model, ["messages"] = new JArray(new JObject { ["role"] = "user", ["content"] = prompt }), ["stream"] = false };
          if (connection.MaxOutputTokens > 0) body[connection.TokenLimitParameter] = connection.MaxOutputTokens;
          if (connection.Effort.Length != 0) body["reasoning_effort"] = connection.Effort;
          break;
      }
      if (connection.Protocol != "gemini" && connection.Temperature.HasValue) body["temperature"] = connection.Temperature.Value;
      MergeAdditional(body, connection.Parameters);
      return body;
    }

    private static void MergeAdditional(JObject body, JObject additional)
    {
      foreach (var property in additional.Properties()) {
        var existing = body.Properties().FirstOrDefault(p => string.Equals(p.Name, property.Name, StringComparison.OrdinalIgnoreCase));
        if (existing == null) body[property.Name] = property.Value.DeepClone();
        else if (existing.Value is JObject nested && property.Value is JObject extra) MergeAdditional(nested, extra);
        else throw new ArgumentException("Дополнительный параметр дублирует значение, заданное в основных настройках API.");
      }
    }

    private static string BasePath(Uri endpoint, string protocol)
    {
      var path = endpoint.AbsolutePath.TrimEnd('/');
      if (path.Length == 0) return protocol == "gemini" ? "/v1beta" : "/v1";
      if (protocol == "gemini") {
        var operation = Regex.Match(path, @"/models/[^/]+:generateContent$", RegexOptions.IgnoreCase);
        if (operation.Success) return path.Substring(0, operation.Index);
      }
      var expected = protocol == "chat-completions" ? "/chat/completions" : protocol == "responses" ? "/responses" : protocol == "anthropic" ? "/messages" : null;
      if (expected != null && path.EndsWith(expected, StringComparison.OrdinalIgnoreCase)) return path.Substring(0, path.Length - expected.Length);
      if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase)) return path.Substring(0, path.Length - "/models".Length);
      if (new[] { "/chat/completions", "/responses", "/messages", ":generateContent", ":streamGenerateContent" }.Any(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
        throw new ArgumentException("URL операции не соответствует выбранному протоколу API.");
      return path;
    }

    private static Uri OperationUri(PreparedConnection connection)
    {
      var suffix = connection.Protocol == "chat-completions" ? "/chat/completions" : connection.Protocol == "responses" ? "/responses"
        : connection.Protocol == "anthropic" ? "/messages" : "/models/" + Uri.EscapeDataString(NormalizeGeminiModel(connection.Model)) + ":generateContent";
      return BuildUri(connection, suffix, null, null);
    }

    private static Uri BuildUri(PreparedConnection connection, string suffix, string queryName, string queryValue)
    {
      var builder = new UriBuilder(connection.Endpoint) { Path = connection.BasePath + suffix, Fragment = "" };
      if (queryName != null) {
        var pieces = builder.Query.TrimStart('?').Split('&').Where(p => p.Length != 0 && !string.Equals(Uri.UnescapeDataString(p.Split('=')[0]), queryName, StringComparison.OrdinalIgnoreCase)).ToList();
        pieces.Add(Uri.EscapeDataString(queryName) + "=" + Uri.EscapeDataString(queryValue));
        builder.Query = string.Join("&", pieces);
      }
      return builder.Uri;
    }

    private static string NormalizeGeminiModel(string model) => model != null && model.StartsWith("models/", StringComparison.Ordinal) ? model.Substring(7) : model;
    private static bool SameModel(string left, string right, string protocol) => string.Equals(protocol == "gemini" ? NormalizeGeminiModel(left) : left, protocol == "gemini" ? NormalizeGeminiModel(right) : right, StringComparison.Ordinal);

    private static async Task<JObject> SendAsync(PreparedConnection connection, HttpMethod method, Uri uri, JObject body, int timeoutSeconds, CancellationToken token)
    {
      using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
      using (var handler = CreateHttpHandler(connection.Proxy))
      using (var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan })
      using (var request = new HttpRequestMessage(method, uri)) {
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body != null) request.Content = new StringContent(body.ToString(Formatting.None), Utf8, "application/json");
        SetHeaders(request, connection);
        try {
          using (var response = await WithCancellationAsync(client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token), timeout.Token).ConfigureAwait(false)) {
            if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
              throw new InvalidOperationException("Прокси вернул HTTP 407. Проверьте параметры авторизации прокси.");
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("API вернул HTTP " + (int)response.StatusCode + ". Проверьте подключение, авторизацию и параметры запроса.");
            var text = await ReadResponseAsync(response.Content, timeout.Token).ConfigureAwait(false);
            JObject parsed;
            try { parsed = ReadResponseObject(text); }
            catch (JsonException) { throw new InvalidOperationException("API вернул некорректный JSON-ответ."); }
            if (parsed["error"] != null && parsed["error"].Type != JTokenType.Null) throw new InvalidOperationException("API сообщил об ошибке запроса.");
            return parsed;
          }
        }
        catch (Exception error) when (timeout.IsCancellationRequested && (error is OperationCanceledException || error is IOException || error is HttpRequestException || error is ObjectDisposedException)) {
          token.ThrowIfCancellationRequested();
          throw new TimeoutException("API не ответил за " + timeoutSeconds + " секунд.");
        }
        catch (HttpRequestException) { throw new InvalidOperationException("Не удалось подключиться к API. Проверьте адрес, сеть и сертификат сервера."); }
        catch (IOException) { throw new InvalidOperationException("Не удалось полностью прочитать ответ API."); }
        catch (DecoderFallbackException) { throw new InvalidOperationException("API вернул ответ в недопустимой кодировке UTF-8."); }
      }
    }

    private static void SetHeaders(HttpRequestMessage request, PreparedConnection connection)
    {
      foreach (var property in connection.Headers.Properties())
        if (!request.Headers.TryAddWithoutValidation(property.Name, (string)property.Value)) throw new ArgumentException("Не удалось добавить заголовок API.");
      if (connection.Protocol == "anthropic" && !request.Headers.Contains("anthropic-version")) request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
      if (connection.ApiKey.Length == 0) return;
      var header = connection.AuthHeader.Length != 0 ? connection.AuthHeader : connection.Protocol == "anthropic" ? "x-api-key" : connection.Protocol == "gemini" ? "x-goog-api-key" : "Authorization";
      var prefix = connection.AuthPrefix;
      if (connection.AuthHeader.Length == 0 && string.IsNullOrWhiteSpace(prefix) && (connection.Protocol == "chat-completions" || connection.Protocol == "responses")) prefix = "Bearer";
      var value = (string.IsNullOrWhiteSpace(prefix) ? "" : prefix.TrimEnd() + " ") + connection.ApiKey;
      if (!request.Headers.TryAddWithoutValidation(header, value)) throw new ArgumentException("Не удалось добавить заголовок авторизации API.");
    }

    private static async Task<string> ReadResponseAsync(HttpContent content, CancellationToken token)
    {
      if (content == null) throw new InvalidOperationException("API вернул пустой ответ.");
      if (content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidOperationException("Ответ API превышает допустимый размер.");
      using (var stream = await WithCancellationAsync(content.ReadAsStreamAsync(), token).ConfigureAwait(false))
      using (var data = new MemoryStream()) {
        var buffer = new byte[8192];
        while (true) {
          var count = await WithCancellationAsync(stream.ReadAsync(buffer, 0, buffer.Length, token), token).ConfigureAwait(false);
          if (count == 0) break;
          if (data.Length + count > MaximumResponseBytes) throw new InvalidOperationException("Ответ API превышает допустимый размер.");
          data.Write(buffer, 0, count);
        }
        return Utf8.GetString(data.GetBuffer(), 0, checked((int)data.Length)).TrimStart('\uFEFF');
      }
    }

    private static JObject ReadResponseObject(string text)
    {
      using (var reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, MaxDepth = 64 }) {
        var token = JToken.ReadFrom(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (!(token is JObject obj) || reader.Read()) throw new JsonReaderException();
        return obj;
      }
    }

    private static string ExtractTranslation(JObject root, string protocol)
    {
      if (protocol == "chat-completions") {
        var choice = (root["choices"] as JArray)?.OfType<JObject>().FirstOrDefault();
        var message = choice?["message"] as JObject;
        var finish = Text(choice?["finish_reason"]);
        if (finish == "length") throw OutputLimitError(root["usage"]);
        if (finish == "content_filter" || !string.IsNullOrEmpty(Text(message?["refusal"])))
          throw new InvalidOperationException("API отклонил перевод из-за ограничений модели.");
        if (HasItems(message?["tool_calls"]) || message?["function_call"] != null && message["function_call"].Type != JTokenType.Null || finish == "tool_calls" || finish == "function_call")
          throw new InvalidOperationException("API вернул вызов инструмента вместо перевода. Выберите текстовую модель или проверьте параметры API.");
        if (finish != "stop" || message == null || Text(message["role"]) != "assistant")
          throw new InvalidOperationException("API не подтвердил завершение перевода. Неполный ответ не показан.");
        if (message["content"]?.Type == JTokenType.String) return (string)message["content"];
        var parts = message["content"] as JArray;
        if (parts != null && parts.All(p => p is JObject && Text(p["type"]) == "text" && p["text"]?.Type == JTokenType.String)) return string.Concat(parts.Select(p => Text(p["text"])));
      }
      else if (protocol == "responses") {
        if (Text(Child(root["incomplete_details"], "reason")) == "max_output_tokens") throw OutputLimitError(root["usage"]);
        if (Text(root["status"]) != "completed" || root["incomplete_details"] != null && root["incomplete_details"].Type != JTokenType.Null)
          throw new InvalidOperationException("API Responses не завершил перевод. Проверьте лимит выходных токенов.");
        var output = root["output"] as JArray;
        if (output == null || output.Any(p => !(p is JObject)) || output.OfType<JObject>().Any(p => Text(p["type"]) != "message" && Text(p["type"]) != "reasoning"))
          throw new InvalidOperationException("API Responses вернул неожиданный тип ответа.");
        var messages = output.OfType<JObject>().Where(p => Text(p["type"]) == "message" && Text(p["role"]) == "assistant").ToList();
        var message = messages.LastOrDefault(p => Text(p["phase"]) == "final_answer") ?? messages.LastOrDefault();
        var content = message?["content"] as JArray;
        if (message != null && (message["status"] == null || Text(message["status"]) == "completed") && content != null && content.All(p => p is JObject && Text(p["type"]) == "output_text" && p["text"]?.Type == JTokenType.String))
          return string.Concat(content.Select(p => Text(p["text"])));
      }
      else if (protocol == "anthropic") {
        var stop = Text(root["stop_reason"]);
        if (stop == "max_tokens") throw OutputLimitError(root["usage"]);
        if (stop != "end_turn" && stop != "stop_sequence") throw new InvalidOperationException("Anthropic не завершил перевод. Проверьте параметры модели.");
        var content = root["content"] as JArray;
        if (content != null && content.All(p => p is JObject && new[] { "text", "thinking", "redacted_thinking" }.Contains(Text(p["type"])) && (Text(p["type"]) != "text" || p["text"]?.Type == JTokenType.String)))
          return string.Concat(content.Where(p => Text(p["type"]) == "text").Select(p => Text(p["text"])));
      }
      else {
        var block = Text(Child(root["promptFeedback"], "blockReason"));
        if (!string.IsNullOrEmpty(block) && block != "BLOCK_REASON_UNSPECIFIED") throw new InvalidOperationException("Gemini отклонил запрос перевода.");
        var candidate = (root["candidates"] as JArray)?.OfType<JObject>().FirstOrDefault();
        if (Text(candidate?["finishReason"]) == "MAX_TOKENS") throw OutputLimitError(root["usageMetadata"]);
        if (Text(candidate?["finishReason"]) != "STOP") throw new InvalidOperationException("Gemini не завершил перевод. Проверьте ограничения ответа.");
        var parts = Child(candidate?["content"], "parts") as JArray;
        if (parts != null && parts.All(p => p is JObject && p["text"]?.Type == JTokenType.String && p["functionCall"] == null && p["executableCode"] == null && p["codeExecutionResult"] == null))
          return string.Concat(parts.Where(p => !Boolean(p["thought"])).Select(p => Text(p["text"])));
      }
      throw new InvalidOperationException("API не вернул окончательный текст перевода.");
    }

    private static Exception OutputLimitError(JToken usage)
    {
      var output = UsageCount(Child(usage, "completion_tokens")) ?? UsageCount(Child(usage, "output_tokens")) ?? UsageCount(Child(usage, "candidatesTokenCount"));
      var reasoning = UsageCount(Child(Child(usage, "completion_tokens_details"), "reasoning_tokens"))
        ?? UsageCount(Child(Child(usage, "output_tokens_details"), "reasoning_tokens")) ?? UsageCount(Child(usage, "thoughtsTokenCount"));
      var detail = output.HasValue ? " Использовано выходных токенов: " + output.Value.ToString(CultureInfo.InvariantCulture) + "." : "";
      if (reasoning.HasValue) detail += " Токенов рассуждений: " + reasoning.Value.ToString(CultureInfo.InvariantCulture) + ".";
      return new InvalidOperationException("API исчерпал лимит ответа и не завершил перевод." + detail + " В дополнительных параметрах увеличьте лимит или выберите 0 (по умолчанию сервиса, кроме Anthropic). Для reasoning-моделей можно уменьшить effort. Неполный перевод не показан.");
    }

    private static long? UsageCount(JToken token) => token?.Type == JTokenType.Integer && long.TryParse(token.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0 ? (long?)value : null;

    private static JToken Child(JToken token, string name) => (token as JObject)?[name];
    private static bool HasItems(JToken token) => token is JArray array ? array.Count != 0 : token != null && token.Type != JTokenType.Null;
    private static string Text(JToken token) => token?.Type == JTokenType.String ? (string)token : null;
    private static bool Boolean(JToken token) => token?.Type == JTokenType.Boolean && (bool)token;

    private static async Task<T> WithCancellationAsync<T>(Task<T> task, CancellationToken token)
    {
      _ = task.ContinueWith(faulted => { var ignored = faulted.Exception; }, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
      var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
      using (token.Register(() => cancelled.TrySetResult(true))) {
        if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task) token.ThrowIfCancellationRequested();
        var result = await task.ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return result;
      }
    }
  }
}
