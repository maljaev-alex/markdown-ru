using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AnotherMarkdown.Translation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class SocksApiTests
{
  private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
  private const string Source = "# SOCKS fixture\n\u041f\u0440\u0438\u0432\u0435\u0442 \ud83c\udf0d\n\"quoted\" C:\\folder\\file.md\nurl = \"http://127.0.0.1:1/never\"\nheader = \"X-Injected: no\"\n% ! ^ & | < > $ ` \n";
  private const string Answer = "# \u041f\u0435\u0440\u0435\u0432\u043e\u0434\n    code\n\"quoted\" C:\\folder\\file.md";
  private const string ApiKey = "SYNTHETIC-SOCKS-API-KEY-\"\\%!^&|<>$";
  private static int checks;

  private static int Main()
  {
    Console.OutputEncoding = Utf8;
    var scratch = Path.Combine(@"D:\Temp\agent\markdown-ru", "socks-api-tests-" + Guid.NewGuid().ToString("N"));
    var oldTemp = Environment.GetEnvironmentVariable("TEMP");
    var oldTmp = Environment.GetEnvironmentVariable("TMP");
    var oldNoProxy = Environment.GetEnvironmentVariable("NO_PROXY");
    var oldLowerNoProxy = Environment.GetEnvironmentVariable("no_proxy");
    try {
      Directory.CreateDirectory(scratch);
      Environment.SetEnvironmentVariable("TEMP", scratch);
      Environment.SetEnvironmentVariable("TMP", scratch);
      // Explicit per-connection SOCKS must override inherited bypass settings.
      Environment.SetEnvironmentVariable("NO_PROXY", "*");
      Environment.SetEnvironmentVariable("no_proxy", "*");
      Check(File.Exists(Path.Combine(Environment.SystemDirectory, "curl.exe")), "native Windows curl is available without installing a transport");
      Run().GetAwaiter().GetResult();
      Console.WriteLine("PASS SOCKS API: " + checks + " assertions");
      return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    finally {
      Environment.SetEnvironmentVariable("TEMP", oldTemp);
      Environment.SetEnvironmentVariable("TMP", oldTmp);
      Environment.SetEnvironmentVariable("NO_PROXY", oldNoProxy);
      Environment.SetEnvironmentVariable("no_proxy", oldLowerNoProxy);
      var resolved = Path.GetFullPath(scratch);
      if (Directory.Exists(resolved) && resolved.StartsWith(@"D:\Temp\agent\markdown-ru\", StringComparison.OrdinalIgnoreCase)) Directory.Delete(resolved, true);
    }
  }

  private static async Task Run()
  {
    await SuccessfulRequests(false);
    await SuccessfulRequests(true);
    await LocalDnsAndLoopback();
    await SafeHttpFailures();
    await FailedProxyNeverFallsBack();
    await CancelAndTimeout(false);
    await CancelAndTimeout(true);
    await InvalidSettings();
    await StrictResponseEncoding();
    await UntrustedTls();
    await ResponseLimit();
  }

  private static void Check(bool condition, string label)
  {
    if (!condition) throw new Exception("FAIL: " + label);
    checks++; Console.WriteLine("PASS " + label);
  }

  private static async Task<Exception> Failure(Func<Task> action)
  {
    try { await action(); }
    catch (Exception error) { return error; }
    throw new Exception("Expected the public API operation to fail.");
  }

  private static async Task<T> Within<T>(Task<T> task, int milliseconds = 8000)
  {
    if (await Task.WhenAny(task, Task.Delay(milliseconds)) != task) throw new TimeoutException("Local SOCKS fixture did not complete.");
    return await task;
  }

  private static ApiConnection Connection(Origin origin, SocksProxy proxy, bool local = false)
  {
    return new ApiConnection {
      Endpoint = "http://" + (local ? "127.0.0.1" : "api-socks-fixture.invalid") + ":" + origin.Port + "/gateway/v1",
      Model = "socks-fixture-model", ApiKey = ApiKey,
      ProxyMode = "custom", ProxyAddress = "socks5h://127.0.0.1:" + proxy.Port,
      AdditionalHeadersJson = "{\"X-Fixture\":\"value-\\\"-\\\\-%!^&|<>$\"}"
    };
  }

  private static Response Success() => new Response {
    Body = JObject.FromObject(new { choices = new[] { new { message = new { role = "assistant", content = Answer }, finish_reason = "stop" } } }).ToString(Formatting.None)
  };

  private static async Task SuccessfulRequests(bool authenticate)
  {
    var user = authenticate ? "fixture-user-\"\\%!^&|<>$-\u044e" : "";
    var password = authenticate ? "fixture-password-\"\\%!^&|<>$-\u043f\u0430\u0440\u043e\u043b\u044c" : "";
    using (var origin = new Origin(request => request.Method == "GET"
      ? new Response { Body = "{\"data\":[{\"id\":\"socks-fixture-model\",\"display_name\":\"SOCKS fixture\"}]}" } : Success()))
    using (var proxy = new SocksProxy(origin.Port, user, password)) {
      var connection = Connection(origin, proxy);
      connection.ProxyUsername = user; connection.ProxyPassword = password;
      var translated = await Within(new ApiTranslator().TranslateAsync(Source, connection, 5, CancellationToken.None));
      Check(translated == Answer, (authenticate ? "authenticated" : "anonymous") + " real SOCKS5 POST returns exact UTF-8 Markdown");
      var request = await Within(origin.FirstRequest);
      var handshake = await Within(proxy.FirstHandshake);
      Check(handshake.AddressType == 3 && handshake.Host == "api-socks-fixture.invalid" && handshake.Port == origin.Port,
        "socks5h transmits the synthetic domain and port to the proxy without local destination DNS");
      Check(handshake.Username == user && handshake.Password == password && handshake.Method == (authenticate ? 2 : 0),
        "SOCKS5 method negotiation and RFC1929 credentials preserve literal config and shell metacharacters");
      Check(request.Method == "POST" && request.Target == "/gateway/v1/chat/completions", "SOCKS preserves the API operation and endpoint base path");
      var body = JObject.Parse(request.Body);
      Check((string)body["model"] == connection.Model && body.Descendants().OfType<JValue>().Any(value => value.Type == JTokenType.String && ((string)value).Contains(Source))
        && !request.Body.StartsWith("\uFEFF", StringComparison.Ordinal), "SOCKS JSON preserves source quotes backslashes newlines Unicode and config-like text without a BOM");
      Check(request.Header("Authorization") == "Bearer " + ApiKey && request.Header("X-Fixture") == "value-\"-\\-%!^&|<>$",
        "API authentication and literal custom headers survive curl config stdin");
      Check(request.Header("Proxy-Authorization") == "" && !request.Headers.Values.Any(value => authenticate && (value.Contains(user) || value.Contains(password))),
        "SOCKS credentials are not forwarded as origin HTTP headers");
      var models = await Within(new ApiTranslator().LoadModelsAsync(connection, 5, CancellationToken.None));
      Check(models.Models.Count == 1 && models.Models[0].Id == connection.Model && origin.Requests.Last().Method == "GET"
        && origin.Requests.Last().Target == "/gateway/v1/models", "model discovery uses a real SOCKS5 GET and preserves exact model IDs");
      Check(proxy.Handshakes.Count == 2 && origin.Requests.Count == 2 && proxy.Failure == null && origin.Failure == null,
        "both API calls use only the selected SOCKS fixture despite inherited NO_PROXY=*");
    }
  }

  private static async Task LocalDnsAndLoopback()
  {
    using (var origin = new Origin(_ => Success()))
    using (var proxy = new SocksProxy(origin.Port)) {
      var connection = Connection(origin, proxy, true);
      connection.ProxyAddress = "socks5://127.0.0.1:" + proxy.Port;
      Check(await Within(new ApiTranslator().TranslateAsync(Source, connection, 5, CancellationToken.None)) == Answer,
        "SOCKS supports an actual loopback API destination without direct bypass");
      var handshake = await Within(proxy.FirstHandshake);
      Check(handshake.AddressType == 1 && handshake.Host == "127.0.0.1" && handshake.Port == origin.Port,
        "socks5 local-DNS mode transmits the resolved IPv4 destination");
      Check(proxy.Handshakes.Count == 1 && origin.Requests.Count == 1, "loopback API traffic actually traverses SOCKS");
    }
  }

  private static async Task SafeHttpFailures()
  {
    using (var origin = new Origin(_ => new Response { Status = 401, Body = "{\"error\":\"" + "SYNTHETIC_PRIVATE_RESPONSE" + "\"}" }))
    using (var proxy = new SocksProxy(origin.Port)) {
      var error = await Failure(() => new ApiTranslator().TranslateAsync(Source, Connection(origin, proxy), 5, CancellationToken.None));
      Check(error.Message.Contains("401") && !error.ToString().Contains("SYNTHETIC_PRIVATE_RESPONSE") && !error.ToString().Contains(ApiKey),
        "SOCKS preserves HTTP 401 while keeping response bodies and API keys out of errors");
    }
    using (var trap = new Origin(_ => Success()))
    using (var origin = new Origin(_ => new Response { Status = 302, Body = "{}", Headers = "Location: http://127.0.0.1:" + trap.Port + "/redirect-target\r\n" }))
    using (var proxy = new SocksProxy(origin.Port)) {
      var error = await Failure(() => new ApiTranslator().TranslateAsync(Source, Connection(origin, proxy), 5, CancellationToken.None));
      Check(error.Message.Contains("302") && trap.Requests.Count == 0 && origin.Requests.Count == 1 && proxy.Handshakes.Count == 1,
        "SOCKS redirects are not followed and credentials never reach the redirect destination");
    }
  }

  private static async Task FailedProxyNeverFallsBack()
  {
    using (var origin = new Origin(_ => Success()))
    using (var proxy = new SocksProxy(origin.Port, connectReply: 5)) {
      var error = await Failure(() => new ApiTranslator().TranslateAsync(Source, Connection(origin, proxy, true), 5, CancellationToken.None));
      Check(error != null && origin.Requests.Count == 0 && proxy.Handshakes.Count == 1, "SOCKS CONNECT refusal cannot fall back to a directly reachable origin");
      var reserved = new TcpListener(IPAddress.Loopback, 0); reserved.Start();
      var unusedPort = ((IPEndPoint)reserved.LocalEndpoint).Port; reserved.Stop();
      var connection = Connection(origin, proxy, true); connection.ProxyAddress = "socks5h://127.0.0.1:" + unusedPort;
      await Failure(() => new ApiTranslator().TranslateAsync(Source, connection, 2, CancellationToken.None));
      Check(origin.Requests.Count == 0, "an unavailable SOCKS listener cannot fall back to the origin");
    }
    using (var origin = new Origin(_ => Success()))
    using (var proxy = new SocksProxy(origin.Port, "expected-user", "expected-password")) {
      var connection = Connection(origin, proxy, true); connection.ProxyUsername = "wrong-user"; connection.ProxyPassword = "wrong-password";
      var error = await Failure(() => new ApiTranslator().TranslateAsync(Source, connection, 5, CancellationToken.None));
      Check(origin.Requests.Count == 0 && !error.ToString().Contains(connection.ProxyPassword), "SOCKS authentication rejection neither bypasses the proxy nor exposes the password");
    }
  }

  private static async Task CancelAndTimeout(bool timeout)
  {
    using (var origin = new Origin(_ => new Response { Stall = true }))
    using (var proxy = new SocksProxy(origin.Port))
    using (var cancellation = new CancellationTokenSource()) {
      var pending = new ApiTranslator().TranslateAsync(Source, Connection(origin, proxy), timeout ? 2 : 15, cancellation.Token);
      var ready = await Task.WhenAny(origin.FirstRequest, pending, Task.Delay(8000));
      if (ready != origin.FirstRequest) { await pending; throw new Exception("Stalled SOCKS request never reached the fixture."); }
      var handshake = await Within(proxy.FirstHandshake);
      var pid = FindClientProcess(handshake.ClientPort, proxy.Port);
      using (var process = Process.GetProcessById(pid)) {
        var ownedHandle = process.Handle;
        Check(process.ProcessName.Equals("curl", StringComparison.OrdinalIgnoreCase), "the stalled SOCKS connection belongs to the owned native curl process");
        var timer = Stopwatch.StartNew();
        if (!timeout) cancellation.Cancel();
        var error = await Within(Failure(async () => { await pending; }), 8000);
        Check(timeout ? error is TimeoutException : error is OperationCanceledException, timeout ? "SOCKS API timeout retains the public timeout contract" : "SOCKS API cancellation retains the public cancellation contract");
        Check(process.WaitForExit(4000) && timer.Elapsed < TimeSpan.FromSeconds(7), timeout ? "SOCKS timeout kills its owned curl promptly" : "SOCKS cancellation kills its owned curl promptly");
      }
    }
  }

  private static async Task InvalidSettings()
  {
    using (var origin = new Origin(_ => Success()))
    using (var proxy = new SocksProxy(origin.Port)) {
      var changes = new Action<ApiConnection>[] {
        value => value.ProxyAddress = "socks4://127.0.0.1:" + proxy.Port,
        value => value.ProxyAddress += "/unexpected-path",
        value => value.ProxyAddress = "socks5h://user:SYNTHETIC_URL_SECRET@127.0.0.1:" + proxy.Port,
        value => value.ProxyAddress += "\nurl = \"http://127.0.0.1:1/never\"",
        value => value.ProxyUseDefaultCredentials = true,
        value => value.ProxyUsername = "ambiguous:username",
        value => value.ProxyUsername = "invalid\0username",
        value => value.ProxyPassword = "invalid\0password",
        value => value.Endpoint += "\nurl = \"http://127.0.0.1:1/never\"",
        value => value.AdditionalHeadersJson = "{\"X-Fixture\":\"safe\\r\\nX-Injected: bad\"}"
      };
      foreach (var change in changes) {
        var connection = Connection(origin, proxy); change(connection);
        var error = await Failure(() => new ApiTranslator().TranslateAsync(Source, connection, 5, CancellationToken.None));
        Check(error is ArgumentException && !error.ToString().Contains("SYNTHETIC_URL_SECRET"), "malformed SOCKS Windows-auth or config-injection settings fail safely before transport");
      }
      Check(proxy.Handshakes.Count == 0 && origin.Requests.Count == 0, "invalid SOCKS settings perform no origin or proxy request");
    }
  }

  private static async Task StrictResponseEncoding()
  {
    var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Success().Body)).ToArray();
    foreach (var bytes in new[] { utf16, new byte[] { 0xff, 0x7b, 0x7d } }) {
      using (var origin = new Origin(_ => new Response { RawBody = bytes }))
      using (var proxy = new SocksProxy(origin.Port)) {
        var error = await Failure(() => new ApiTranslator().TranslateAsync(Source, Connection(origin, proxy), 5, CancellationToken.None));
        Check(error is InvalidOperationException && origin.Requests.Count == 1, "SOCKS rejects UTF-16 BOM and invalid UTF-8 rather than silently changing the API response encoding");
      }
    }
  }

  private static async Task ResponseLimit()
  {
    using (var origin = new Origin(_ => new Response { Body = new JObject {
      ["choices"] = new JArray(new JObject { ["message"] = new JObject { ["role"] = "assistant", ["content"] = new string('x', 32000001) }, ["finish_reason"] = "stop" })
    }.ToString(Formatting.None) }))
    using (var proxy = new SocksProxy(origin.Port)) {
      var error = await Failure(() => new ApiTranslator().TranslateAsync(Source, Connection(origin, proxy), 10, CancellationToken.None));
      Check(error is InvalidOperationException && origin.Requests.Count == 1, "SOCKS rejects a response exceeding the API size bound");
    }
  }

  private static async Task UntrustedTls()
  {
    var keyLog = Path.Combine(Path.GetTempPath(), "fixture-tls-keys-" + Guid.NewGuid().ToString("N") + ".log");
    var oldKeyLog = Environment.GetEnvironmentVariable("SSLKEYLOGFILE");
    try {
      Environment.SetEnvironmentVariable("SSLKEYLOGFILE", keyLog);
      using (var rsa = new RSACng(2048)) {
        Check(rsa.Key.IsEphemeral, "TLS fixture RSA private key is ephemeral without a persisted container");
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback); names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using (var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)))
        using (var origin = new TlsOrigin(certificate, rsa))
        using (var proxy = new SocksProxy(origin.Port)) {
          using (var privateKey = certificate.GetRSAPrivateKey())
            Check(privateKey is RSACng cng && cng.Key.IsEphemeral, "TLS certificate keeps its private key in memory without writing a PFX or trusting a certificate");
          var connection = new ApiConnection {
            Endpoint = "https://127.0.0.1:" + origin.Port + "/gateway/v1", Model = "socks-fixture-model", ApiKey = ApiKey,
            ProxyMode = "custom", ProxyAddress = "socks5h://127.0.0.1:" + proxy.Port
          };
          var pending = new ApiTranslator().TranslateAsync(Source, connection, 5, CancellationToken.None);
          await Within(origin.ClientHelloReceived);
          var handshake = await Within(proxy.FirstHandshake);
          using (var process = Process.GetProcessById(FindClientProcess(handshake.ClientPort, proxy.Port))) {
            var ownedHandle = process.Handle;
            origin.Continue();
            var error = await Within(Failure(async () => { await pending; }));
            await Within(origin.Completed);
            Check(error is InvalidOperationException && error.Message.Contains("TLS") && !error.ToString().Contains(ApiKey)
              && !error.Message.Contains("UTF-8"), "an untrusted certificate fails with a safe TLS diagnostic rather than a localized stderr decoding error");
            Check(process.WaitForExit(4000) && process.ExitCode == 60, "native SOCKS curl reports certificate verification failure 60 rather than a malformed TLS handshake");
          }
          Check(origin.CertificateSent && origin.ApplicationBytes == 0 && proxy.Handshakes.Count == 1 && origin.Connections == 1 && origin.Failure == null,
            "SOCKS HTTPS rejects the presented self-signed certificate before sending API authorization or document bytes without direct fallback");
          Check(!File.Exists(keyLog), "untrusted SOCKS TLS handshake writes no SSLKEYLOGFILE artifact");
        }
      }
    }
    finally { Environment.SetEnvironmentVariable("SSLKEYLOGFILE", oldKeyLog); }
  }

  private static int FindClientProcess(int localPort, int remotePort)
  {
    var size = 0;
    GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 5, 0);
    size += 65536;
    var memory = Marshal.AllocHGlobal(size);
    try {
      if (GetExtendedTcpTable(memory, ref size, false, 2, 5, 0) != 0) throw new Exception("Could not identify the fixture-owned curl TCP connection.");
      var rows = Marshal.ReadInt32(memory);
      for (var index = 0; index < rows; index++) {
        var row = IntPtr.Add(memory, 4 + index * 24);
        var local = Marshal.ReadInt32(row, 8); var remote = Marshal.ReadInt32(row, 16);
        if ((((local & 255) << 8) | ((local >> 8) & 255)) == localPort && (((remote & 255) << 8) | ((remote >> 8) & 255)) == remotePort)
          return Marshal.ReadInt32(row, 20);
      }
      throw new Exception("Owned curl connection was not present in the TCP owner table.");
    }
    finally { Marshal.FreeHGlobal(memory); }
  }

  [DllImport("iphlpapi.dll", SetLastError = true)]
  private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool ordered, int addressFamily, int tableClass, uint reserved);

  private sealed class Request
  {
    public string Method, Target, Body;
    public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : "";
  }

  private sealed class Response
  {
    public int Status = 200;
    public string Body = "{}", Headers = "";
    public byte[] RawBody;
    public bool Stall;
  }

  private sealed class Handshake
  {
    public int Method, AddressType, Port, ClientPort;
    public string Host, Username = "", Password = "";
  }

  private abstract class Fixture : IDisposable
  {
    protected readonly TcpListener Listener = new TcpListener(IPAddress.Loopback, 0);
    protected readonly CancellationTokenSource Stop = new CancellationTokenSource();
    private readonly List<TcpClient> clients = new List<TcpClient>();
    private int disposed;
    public int Port { get; private set; }
    public Exception Failure { get; private set; }
    protected Fixture() { Listener.Start(); Port = ((IPEndPoint)Listener.LocalEndpoint).Port; }
    protected void Start() { Observe(Accept()); }
    protected void Own(TcpClient client) { lock (clients) clients.Add(client); }
    private async Task Accept()
    {
      while (!Stop.IsCancellationRequested) {
        var client = await Listener.AcceptTcpClientAsync(); Own(client); Observe(Handle(client));
      }
    }
    private async void Observe(Task task)
    {
      try { await task; }
      catch (Exception error) { if (!Stop.IsCancellationRequested && !(error is IOException) && !(error is SocketException)) Failure = error; }
    }
    protected abstract Task Handle(TcpClient client);
    public void Dispose()
    {
      if (Interlocked.Exchange(ref disposed, 1) != 0) return;
      Stop.Cancel(); Listener.Stop(); lock (clients) foreach (var client in clients) client.Close();
    }
  }

  private sealed class Origin : Fixture
  {
    private readonly Func<Request, Response> respond;
    private readonly List<Request> requests = new List<Request>();
    private readonly TaskCompletionSource<Request> first = new TaskCompletionSource<Request>(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<Request> FirstRequest => first.Task;
    public List<Request> Requests { get { lock (requests) return requests.ToList(); } }
    public Origin(Func<Request, Response> respond) { this.respond = respond; Start(); }
    protected override async Task Handle(TcpClient client)
    {
      using (client) {
        var stream = client.GetStream(); var header = new MemoryStream();
        while (true) {
          var next = await ReadExactly(stream, 1, Stop.Token); header.WriteByte(next[0]);
          var buffer = header.GetBuffer(); var length = (int)header.Length;
          if (length >= 4 && buffer[length - 4] == 13 && buffer[length - 3] == 10 && buffer[length - 2] == 13 && buffer[length - 1] == 10) break;
          if (length > 65536) throw new Exception("Fixture HTTP headers exceeded their bound.");
        }
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
        var start = lines[0].Split(' '); var request = new Request { Method = start[0], Target = start[1] };
        foreach (var line in lines.Skip(1)) { var colon = line.IndexOf(':'); if (colon > 0) request.Headers[line.Substring(0, colon)] = line.Substring(colon + 1).Trim(); }
        var contentLength = request.Headers.ContainsKey("Content-Length") ? int.Parse(request.Header("Content-Length")) : 0;
        if (contentLength < 0 || contentLength > 2000000) throw new Exception("Fixture request body exceeded its bound.");
        request.Body = Utf8.GetString(await ReadExactly(stream, contentLength, Stop.Token));
        lock (requests) requests.Add(request); first.TrySetResult(request);
        var response = respond(request);
        if (response.Stall) { await Task.Delay(Timeout.Infinite, Stop.Token); return; }
        var body = response.RawBody ?? Utf8.GetBytes(response.Body);
        var headers = Encoding.ASCII.GetBytes("HTTP/1.1 " + response.Status + " Fixture\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n" + response.Headers + "\r\n");
        await stream.WriteAsync(headers, 0, headers.Length, Stop.Token); await stream.WriteAsync(body, 0, body.Length, Stop.Token);
      }
    }
  }

  private sealed class TlsOrigin : Fixture
  {
    private readonly X509Certificate2 certificate;
    private readonly RSA rsa;
    private readonly TaskCompletionSource<bool> completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> helloReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> proceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private int connections, applicationBytes, certificateSent;
    public Task<bool> Completed => completed.Task;
    public Task<bool> ClientHelloReceived => helloReceived.Task;
    public int Connections => Volatile.Read(ref connections);
    public int ApplicationBytes => Volatile.Read(ref applicationBytes);
    public bool CertificateSent => Volatile.Read(ref certificateSent) != 0;
    public void Continue() { proceed.TrySetResult(true); }
    public TlsOrigin(X509Certificate2 certificate, RSA rsa) { this.certificate = certificate; this.rsa = rsa; Start(); }
    protected override async Task Handle(TcpClient client)
    {
      Interlocked.Increment(ref connections);
      using (client) {
        try {
          var stream = client.GetStream();
          var record = await ReadExactly(stream, 5, Stop.Token);
          if (record[0] != 22) throw new Exception("TLS fixture expected ClientHello.");
          var hello = await ReadExactly(stream, (record[3] << 8) | record[4], Stop.Token);
          if (hello.Length < 42 || hello[0] != 1) throw new Exception("Invalid TLS fixture ClientHello.");
          var offset = 39 + hello[38];
          if (offset + 2 > hello.Length) throw new Exception("Invalid TLS fixture session ID.");
          var cipherBytes = (hello[offset] << 8) | hello[offset + 1]; offset += 2;
          if (cipherBytes % 2 != 0 || offset + cipherBytes > hello.Length) throw new Exception("Invalid TLS fixture ciphers.");
          var offered = new List<int>();
          for (var index = 0; index < cipherBytes; index += 2) offered.Add((hello[offset + index] << 8) | hello[offset + index + 1]);
          var cipher = new[] { 0xc02f, 0xc030, 0x009c, 0x009d, 0x003c, 0x003d, 0x002f, 0x0035 }.FirstOrDefault(offered.Contains);
          if (cipher == 0) throw new Exception("Native curl did not offer a supported TLS 1.2 RSA certificate cipher.");
          helloReceived.TrySetResult(true);
          await Task.WhenAny(proceed.Task, Task.Delay(Timeout.Infinite, Stop.Token)); Stop.Token.ThrowIfCancellationRequested();
          var random = new byte[32]; using (var generator = RandomNumberGenerator.Create()) generator.GetBytes(random);
          var serverHello = new byte[] { 3, 3 }.Concat(random).Concat(new byte[] { 0, (byte)(cipher >> 8), (byte)cipher, 0, 0, 5, 255, 1, 0, 1, 0 }).ToArray();
          var der = certificate.RawData;
          var certificateBody = Length24(der.Length + 3).Concat(Length24(der.Length)).Concat(der).ToArray();
          var flight = Handshake(2, serverHello).Concat(Handshake(11, certificateBody));
          if (cipher == 0xc02f || cipher == 0xc030) {
            using (var ecdh = new ECDiffieHellmanCng(256)) {
              var publicKey = ecdh.Key.Export(CngKeyBlobFormat.EccPublicBlob).Skip(8).ToArray();
              var parameters = new byte[] { 3, 0, 23, 65, 4 }.Concat(publicKey).ToArray();
              var signed = hello.Skip(6).Take(32).Concat(random).Concat(parameters).ToArray();
              var signature = rsa.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
              var exchange = parameters.Concat(new byte[] { 4, 1, (byte)(signature.Length >> 8), (byte)signature.Length }).Concat(signature).ToArray();
              flight = flight.Concat(Handshake(12, exchange));
            }
          }
          var payload = flight.Concat(Handshake(14, new byte[0])).ToArray();
          var response = new byte[] { 22, 3, 3, (byte)(payload.Length >> 8), (byte)payload.Length }.Concat(payload).ToArray();
          // Framework Schannel cannot use an ephemeral server credential. This
          // TLS 1.2 server flight is complete through ServerHelloDone, including
          // a signed ECDHE exchange when offered. An untrusted client must stop
          // before HTTP or key exchange proceeds; Schannel may close without
          // emitting an alert, so the test also verifies curl's native exit 60.
          await stream.WriteAsync(response, 0, response.Length, Stop.Token);
          Interlocked.Exchange(ref certificateSent, 1);
          record = await ReadExactly(stream, 5, Stop.Token);
          var answer = await ReadExactly(stream, (record[3] << 8) | record[4], Stop.Token);
          if (!(record[0] == 21 && answer.Length == 2 && answer[0] == 2 && new[] { 42, 46, 48 }.Contains((int)answer[1])))
            Interlocked.Add(ref applicationBytes, answer.Length);
        }
        catch (IOException) { }
        finally { completed.TrySetResult(true); }
      }
    }
    private static byte[] Length24(int length) => new byte[] { (byte)(length >> 16), (byte)(length >> 8), (byte)length };
    private static byte[] Handshake(byte type, byte[] body) => new[] { type }.Concat(Length24(body.Length)).Concat(body).ToArray();
  }

  private sealed class SocksProxy : Fixture
  {
    private readonly int originPort, connectReply;
    private readonly string username, password;
    private readonly List<Handshake> handshakes = new List<Handshake>();
    private readonly TaskCompletionSource<Handshake> first = new TaskCompletionSource<Handshake>(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<Handshake> FirstHandshake => first.Task;
    public List<Handshake> Handshakes { get { lock (handshakes) return handshakes.ToList(); } }
    public SocksProxy(int originPort, string username = "", string password = "", int connectReply = 0)
    { this.originPort = originPort; this.username = username; this.password = password; this.connectReply = connectReply; Start(); }
    protected override async Task Handle(TcpClient client)
    {
      using (client) {
        var stream = client.GetStream(); var hello = await ReadExactly(stream, 2, Stop.Token);
        if (hello[0] != 5 || hello[1] == 0) throw new Exception("Invalid fixture SOCKS greeting.");
        var methods = await ReadExactly(stream, hello[1], Stop.Token); var method = username.Length == 0 ? 0 : 2;
        if (!methods.Contains((byte)method)) throw new Exception("curl did not offer the required SOCKS authentication method.");
        await stream.WriteAsync(new byte[] { 5, (byte)method }, 0, 2, Stop.Token);
        var handshake = new Handshake { Method = method, ClientPort = ((IPEndPoint)client.Client.RemoteEndPoint).Port };
        if (method == 2) {
          var auth = await ReadExactly(stream, 2, Stop.Token);
          if (auth[0] != 1) throw new Exception("Invalid RFC1929 authentication version.");
          handshake.Username = Utf8.GetString(await ReadExactly(stream, auth[1], Stop.Token));
          var size = (await ReadExactly(stream, 1, Stop.Token))[0];
          handshake.Password = Utf8.GetString(await ReadExactly(stream, size, Stop.Token));
          var accepted = handshake.Username == username && handshake.Password == password;
          await stream.WriteAsync(new byte[] { 1, (byte)(accepted ? 0 : 1) }, 0, 2, Stop.Token);
          if (!accepted) return;
        }
        var command = await ReadExactly(stream, 4, Stop.Token);
        if (command[0] != 5 || command[1] != 1 || command[2] != 0) throw new Exception("Invalid fixture SOCKS CONNECT command.");
        handshake.AddressType = command[3];
        if (command[3] == 3) handshake.Host = Encoding.ASCII.GetString(await ReadExactly(stream, (await ReadExactly(stream, 1, Stop.Token))[0], Stop.Token));
        else if (command[3] == 1 || command[3] == 4) handshake.Host = new IPAddress(await ReadExactly(stream, command[3] == 1 ? 4 : 16, Stop.Token)).ToString();
        else throw new Exception("Invalid fixture SOCKS address type.");
        var port = await ReadExactly(stream, 2, Stop.Token); handshake.Port = (port[0] << 8) | port[1];
        lock (handshakes) handshakes.Add(handshake); first.TrySetResult(handshake);
        var reply = new byte[] { 5, (byte)connectReply, 0, 1, 127, 0, 0, 1, 0, 0 };
        if (connectReply != 0) { await stream.WriteAsync(reply, 0, reply.Length, Stop.Token); return; }
        // Only our local origin is reachable; synthetic remote domains never resolve.
        using (var origin = new TcpClient()) {
          Own(origin); await origin.ConnectAsync(IPAddress.Loopback, originPort);
          await stream.WriteAsync(reply, 0, reply.Length, Stop.Token);
          var upstream = origin.GetStream();
          var upload = stream.CopyToAsync(upstream, 8192, Stop.Token); var download = upstream.CopyToAsync(stream, 8192, Stop.Token);
          await Task.WhenAny(upload, download);
          ObserveRelay(upload); ObserveRelay(download);
        }
      }
    }
    private static async void ObserveRelay(Task relay) { try { await relay; } catch (IOException) { } catch (ObjectDisposedException) { } catch (OperationCanceledException) { } }
  }

  private static async Task<byte[]> ReadExactly(Stream stream, int count, CancellationToken cancellation)
  {
    var bytes = new byte[count]; var offset = 0;
    while (offset < count) {
      var read = await stream.ReadAsync(bytes, offset, count - offset, cancellation);
      if (read == 0) throw new IOException("Fixture connection closed.");
      offset += read;
    }
    return bytes;
  }
}
