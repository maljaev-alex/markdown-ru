using System;
using System.IO;
using System.Text;
using AnotherMarkdown.Translation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class ApiConnectionStoreTests
{
  private static int passed;
  private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

  private static int Main()
  {
    var scratch = Path.GetFullPath(Directory.Exists(@"D:\Temp") ? @"D:\Temp\agent\markdown-ru" : Path.GetTempPath());
    var directory = Path.Combine(scratch, "api-store-tests-" + Guid.NewGuid().ToString("N"));
    try {
      Directory.CreateDirectory(directory);
      Run(directory);
      ProxySettings(directory);
      Console.WriteLine("PASS API store: " + passed + " assertions");
      return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); return 1; }
    finally {
      var resolved = Path.GetFullPath(directory);
      if (Directory.Exists(resolved) && resolved.StartsWith(scratch.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        Directory.Delete(resolved, true);
    }
  }

  private static void Run(string directory)
  {
    var path = Path.Combine(directory, "connections.json");
    var key = "SYNTHETIC_API_KEY_ONLY_FOR_STORE_TEST";
    var headerSecret = "SYNTHETIC_HEADER_ONLY_FOR_STORE_TEST";
    var headers = "{\"X-Test-Secret\":\"" + headerSecret + "\"}";
    var connection = new ApiConnection { Name = "Test connection", ApiKey = key, AdditionalHeadersJson = headers };
    Check(connection.Id.Length == 32 && connection.Id != new ApiConnection().Id, "new connections have independent IDs");
    Check(connection.Protocol == "chat-completions" && connection.TokenLimitParameter == "max_tokens"
      && connection.MaxOutputTokens == 0 && connection.Temperature == null, "protocol defaults");
    Check(connection.ToString() == connection.Name, "display uses connection name");
    var missing = Path.Combine(directory, "missing", "connections.json");
    Check(ApiConnectionStore.Load(missing).Count == 0 && !Directory.Exists(Path.GetDirectoryName(missing)), "missing file does not create storage");
    var directJson = JsonConvert.SerializeObject(connection);
    Check(!directJson.Contains(key) && !directJson.Contains(headerSecret), "direct JSON serialization excludes plaintext credentials");

    ApiConnectionStore.Save(path, new[] { connection });
    var bytes = File.ReadAllBytes(path);
    Check(bytes.Length < 3 || bytes[0] != 239 || bytes[1] != 187 || bytes[2] != 191, "UTF8 config has no BOM");
    var json = File.ReadAllText(path, Utf8);
    var stored = (JObject)JArray.Parse(json)[0];
    Check(!json.Contains(key) && !json.Contains(headerSecret) && stored.Property("ApiKey") == null
      && stored.Property("AdditionalHeadersJson") == null, "store contains encrypted credentials only");
    Check(!string.IsNullOrEmpty((string)stored["EncryptedApiKey"]) && !string.IsNullOrEmpty((string)stored["EncryptedHeaders"]), "both secret fields have ciphertext");
    var loaded = ApiConnectionStore.Load(path)[0];
    Check(loaded.ApiKey == key && loaded.AdditionalHeadersJson == headers && loaded.CredentialError == null, "CurrentUser DPAPI roundtrip");
    Check(loaded.Id == connection.Id && loaded.TokenLimitParameter == "max_tokens", "nonsecret settings roundtrip");

    var previousDefaults = JsonConvert.DefaultSettings;
    try {
      JsonConvert.DefaultSettings = () => new JsonSerializerSettings { Converters = { new RejectConnectionConverter() } };
      ApiConnectionStore.Save(path, new[] { loaded });
      Check(ApiConnectionStore.Load(path)[0].ApiKey == key, "host JSON converters cannot expose or override credential serialization");
    }
    finally { JsonConvert.DefaultSettings = previousDefaults; }
    json = File.ReadAllText(path, Utf8);

    loaded.Name = "Second version";
    ApiConnectionStore.Save(path, new[] { loaded });
    Check(File.ReadAllText(path + ".bak", Utf8) == json, "replacement retains exact prior bytes");
    var second = File.ReadAllText(path, Utf8);
    loaded.Name = "Third version";
    ApiConnectionStore.Save(path, new[] { loaded });
    Check(File.ReadAllText(path + ".bak", Utf8) == second, "existing stable backup is replaced without predeletion");

    var damaged = JArray.Parse(File.ReadAllText(path, Utf8));
    damaged[0]["EncryptedApiKey"] = "not-base64";
    File.WriteAllText(path, damaged.ToString(), Utf8);
    loaded = ApiConnectionStore.Load(path)[0];
    Check(loaded.ApiKey == "" && loaded.AdditionalHeadersJson == headers && loaded.CredentialError != null, "bad key ciphertext does not lose valid headers");
    Check(!loaded.CredentialError.Contains("not-base64"), "credential error contains no input");
    ApiConnectionStore.Save(path, new[] { loaded.Copy() });
    Check((string)JArray.Parse(File.ReadAllText(path, Utf8))[0]["EncryptedApiKey"] == "not-base64", "ordinary copy/save preserves unreadable key ciphertext");
    loaded.ApiKey = "SYNTHETIC_REPLACEMENT_KEY";
    Check(loaded.CredentialError == null, "new key clears repaired credential error");
    ApiConnectionStore.Save(path, new[] { loaded });
    Check(ApiConnectionStore.Load(path)[0].ApiKey == loaded.ApiKey, "replacement key is encrypted and recoverable");

    damaged = JArray.Parse(File.ReadAllText(path, Utf8));
    damaged[0]["EncryptedHeaders"] = "AQIDBA==";
    File.WriteAllText(path, damaged.ToString(), Utf8);
    loaded = ApiConnectionStore.Load(path)[0];
    Check(loaded.ApiKey.Length > 0 && loaded.AdditionalHeadersJson == "{}" && loaded.CredentialError != null, "bad headers ciphertext does not lose valid key");
    loaded.AdditionalHeadersJson = "{}";
    ApiConnectionStore.Save(path, new[] { loaded.Copy() });
    Check((string)JArray.Parse(File.ReadAllText(path, Utf8))[0]["EncryptedHeaders"] == "AQIDBA==", "unchanged empty UI field preserves unreadable headers");
    loaded.ApiKey = "SYNTHETIC_ANOTHER_KEY";
    Check(loaded.CredentialError != null, "replacing key does not hide unresolved headers error");
    loaded.ClearCredentials();
    Check(loaded.ApiKey == "" && loaded.AdditionalHeadersJson == "{}" && loaded.EncryptedApiKey == ""
      && loaded.EncryptedHeaders == "" && loaded.CredentialError == null, "explicit clear removes both credentials and errors");
    ApiConnectionStore.Save(path, new[] { loaded });
    stored = (JObject)JArray.Parse(File.ReadAllText(path, Utf8))[0];
    Check((string)stored["EncryptedApiKey"] == "" && (string)stored["EncryptedHeaders"] == "", "cleared credentials remain empty on save");
    Check(!File.Exists(path + ".bak"), "explicit credential clearing removes the rolling credential backup");
    ApiConnectionStore.Save(path, new[] { connection });
    ApiConnectionStore.Save(path, new ApiConnection[0]);
    Check(ApiConnectionStore.Load(path).Count == 0 && !File.Exists(path + ".bak"), "deleting a credential-bearing profile also removes its rolling backup");
    var backupFixture = connection.Copy();
    ApiConnectionStore.Save(path, new[] { backupFixture });
    backupFixture.Name = "Backup fixture";
    ApiConnectionStore.Save(path, new[] { backupFixture });
    File.SetAttributes(path + ".bak", FileAttributes.ReadOnly);
    try {
      backupFixture.ClearCredentials();
      string warning;
      ApiConnectionStore.Save(path, new[] { backupFixture }, false, out warning);
      Check(warning != null && ApiConnectionStore.Load(path)[0].ApiKey == "" && File.Exists(path + ".bak"), "backup cleanup failure is a separate warning after a successful settings commit");
    }
    finally { if (File.Exists(path + ".bak")) { File.SetAttributes(path + ".bak", FileAttributes.Normal); File.Delete(path + ".bak"); } }

    File.WriteAllText(path, "{ broken-synthetic-json", Utf8);
    try { ApiConnectionStore.Load(path); throw new Exception("Malformed JSON was accepted."); }
    catch (InvalidDataException error) { Check(error.InnerException == null && !error.Message.Contains("broken-synthetic"), "malformed config reports safe generic error"); }
    var options = new TranslationOptions();
    options.LoadApiConnections(path);
    Check(options.ApiConnections.Count == 0 && options.ApiConfigurationError != null && File.ReadAllText(path, Utf8) == "{ broken-synthetic-json", "startup tolerates malformed API settings without modifying them");
    var recovery = ApiConnectionStore.Save(path, new[] { loaded });
    Check(File.ReadAllText(path + ".bak", Utf8) == "{ broken-synthetic-json", "replacement retains malformed original for recovery");
    ApiConnectionStore.Save(path, new[] { loaded });
    Check(recovery != null && File.ReadAllText(recovery, Utf8) == "{ broken-synthetic-json", "unreadable original survives subsequent saves in a separate recovery file");

    var before = File.ReadAllText(path, Utf8);
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) {
      options.LoadApiConnections(path);
      Check(options.ApiConnections.Count == 0 && options.ApiConfigurationError != null, "startup tolerates a locked API settings file");
      try { ApiConnectionStore.Save(path, new[] { loaded }); throw new Exception("Locked config was overwritten."); }
      catch (IOException error) { Check(error.InnerException == null, "failed atomic replacement reports safe error"); }
    }
    Check(File.ReadAllText(path, Utf8) == before, "failed replacement preserves existing config");
    Check(Directory.GetFiles(directory, "*.tmp-*").Length == 0, "temporary encrypted file is cleaned after failed replacement");
    loaded.Endpoint = "https://example.test/v1?key=" + key;
    try { ApiConnectionStore.Save(path, new[] { loaded }); throw new Exception("Plaintext endpoint key was saved."); }
    catch (IOException error) { Check(!error.ToString().Contains(key) && File.ReadAllText(path, Utf8) == before, "endpoint credentials cannot bypass encrypted storage"); }
  }

  private static void ProxySettings(string directory)
  {
    var path = Path.Combine(directory, "proxy-connections.json");
    var username = "SYNTHETIC_PROXY_USER_ONLY_FOR_STORE_TEST";
    var password = "SYNTHETIC_PROXY_PASSWORD_ONLY_FOR_STORE_TEST";
    var connection = new ApiConnection {
      ProxyMode = "custom", ProxyAddress = "http://127.0.0.1:8080", ProxyUsername = username, ProxyPassword = password
    };
    var defaults = new ApiConnection();
    Check(defaults.ProxyMode == "system" && defaults.ProxyAddress == "" && !defaults.ProxyUseDefaultCredentials
      && defaults.ProxyUsername == "" && defaults.ProxyPassword == "", "new API profiles use system proxy without explicit proxy credentials");
    var json = JsonConvert.SerializeObject(connection);
    Check(!json.Contains(username) && !json.Contains(password) && JObject.Parse(json).Property("ProxyUsername") == null
      && JObject.Parse(json).Property("ProxyPassword") == null, "direct profile JSON excludes proxy username and password");
    ApiConnectionStore.Save(path, new[] { connection });
    json = File.ReadAllText(path, Utf8);
    var stored = (JObject)JArray.Parse(json)[0];
    Check(!json.Contains(username) && !json.Contains(password) && !string.IsNullOrEmpty((string)stored["EncryptedProxyUsername"])
      && !string.IsNullOrEmpty((string)stored["EncryptedProxyPassword"]), "both proxy secrets are persisted only as DPAPI ciphertext");
    var loaded = ApiConnectionStore.Load(path)[0];
    Check(loaded.ProxyUsername == username && loaded.ProxyPassword == password && loaded.CredentialError == null
      && loaded.ProxyMode == "custom" && loaded.ProxyAddress == connection.ProxyAddress, "proxy route and CurrentUser DPAPI credentials roundtrip");
    loaded.ProxyUseDefaultCredentials = true;
    ApiConnectionStore.Save(path, new[] { loaded });
    loaded = ApiConnectionStore.Load(path)[0];
    Check(loaded.ProxyUseDefaultCredentials && loaded.ProxyUsername == username && loaded.ProxyPassword == password, "Windows proxy authentication preference persists without discarding explicit credentials");
    loaded.ProxyUseDefaultCredentials = false;

    var legacy = Path.Combine(directory, "legacy-proxy-defaults.json");
    File.WriteAllText(legacy, "[{\"Name\":\"Legacy\"}]", Utf8);
    var old = ApiConnectionStore.Load(legacy)[0];
    Check(old.ProxyMode == "system" && old.ProxyAddress == "" && old.ProxyUsername == "" && old.ProxyPassword == ""
      && !old.ProxyUseDefaultCredentials && old.CredentialError == null, "profiles predating proxy support retain compatible system-proxy defaults");

    var damaged = JArray.Parse(File.ReadAllText(path, Utf8));
    damaged[0]["EncryptedProxyUsername"] = "unavailable-proxy-user";
    File.WriteAllText(path, damaged.ToString(), Utf8);
    loaded = ApiConnectionStore.Load(path)[0];
    Check(loaded.ProxyUsername == "" && loaded.ProxyPassword == password && loaded.CredentialError != null
      && !loaded.CredentialError.Contains("unavailable-proxy-user"), "unreadable proxy username preserves the valid password and reports a generic error");
    loaded.ProxyUsername = "";
    var copy = loaded.Copy();
    copy.Name = "Copied proxy draft";
    copy.ClearCredentials(false);
    Check(copy.ProxyPassword == password && copy.CredentialError != null && copy.EncryptedProxyUsername == "unavailable-proxy-user",
      "service preset credential clearing preserves unavailable proxy ciphertext and its error");
    ApiConnectionStore.Save(path, new[] { copy });
    stored = (JObject)JArray.Parse(File.ReadAllText(path, Utf8))[0];
    Check((string)stored["EncryptedProxyUsername"] == "unavailable-proxy-user" && ApiConnectionStore.Load(path)[0].ProxyPassword == password,
      "ordinary proxy copy save preserves unavailable username ciphertext despite an untouched empty UI field");
    copy.ProxyUsername = "SYNTHETIC_REPAIRED_PROXY_USER";
    Check(copy.CredentialError == null && loaded.CredentialError != null && loaded.ProxyUsername == "", "proxy draft replacement flags remain independent of the original copy");
    ApiConnectionStore.Save(path, new[] { copy });
    loaded = ApiConnectionStore.Load(path)[0];
    Check(loaded.ProxyUsername == copy.ProxyUsername && loaded.ProxyPassword == password && loaded.CredentialError == null,
      "repaired proxy username is encrypted and recoverable without altering its password");

    damaged = JArray.Parse(File.ReadAllText(path, Utf8));
    damaged[0]["EncryptedProxyPassword"] = "AQIDBA==";
    File.WriteAllText(path, damaged.ToString(), Utf8);
    loaded = ApiConnectionStore.Load(path)[0];
    Check(loaded.ProxyUsername == copy.ProxyUsername && loaded.ProxyPassword == "" && loaded.CredentialError != null,
      "unreadable proxy password preserves the valid username");
    loaded.ProxyPassword = "";
    loaded.ProxyUsername = "SYNTHETIC_ANOTHER_PROXY_USER";
    Check(loaded.CredentialError != null, "replacing proxy username does not hide an unresolved password error");
    ApiConnectionStore.Save(path, new[] { loaded.Copy() });
    Check((string)JArray.Parse(File.ReadAllText(path, Utf8))[0]["EncryptedProxyPassword"] == "AQIDBA==",
      "independently edited proxy username preserves unavailable untouched password ciphertext");
    loaded.ProxyPassword = "SYNTHETIC_REPAIRED_PROXY_PASSWORD";
    Check(loaded.CredentialError == null, "replacing the proxy password clears its repaired credential error");
    ApiConnectionStore.Save(path, new[] { loaded });
    loaded = ApiConnectionStore.Load(path)[0];
    Check(loaded.ProxyPassword == "SYNTHETIC_REPAIRED_PROXY_PASSWORD", "replacement proxy password roundtrips through DPAPI");
    loaded.ClearCredentials(false);
    Check(loaded.ProxyUsername.Length > 0 && loaded.ProxyPassword.Length > 0 && loaded.CredentialError == null,
      "service preset credential clearing retains valid proxy authentication");
    ApiConnectionStore.Save(path, new[] { loaded });
    Check(File.Exists(path + ".bak"), "normal proxy saves retain the rolling backup");
    loaded.ClearCredentials();
    Check(loaded.ProxyUsername == "" && loaded.ProxyPassword == "" && loaded.EncryptedProxyUsername == ""
      && loaded.EncryptedProxyPassword == "" && loaded.CredentialError == null, "explicit credential clearing removes both proxy secrets and errors");
    ApiConnectionStore.Save(path, new[] { loaded });
    stored = (JObject)JArray.Parse(File.ReadAllText(path, Utf8))[0];
    Check((string)stored["EncryptedProxyUsername"] == "" && (string)stored["EncryptedProxyPassword"] == "" && !File.Exists(path + ".bak"),
      "proxy-only credential clearing deletes both ciphertext fields and the rolling credential backup");
    ApiConnectionStore.Save(path, new[] { connection });
    ApiConnectionStore.Save(path, new ApiConnection[0]);
    Check(ApiConnectionStore.Load(path).Count == 0 && !File.Exists(path + ".bak"), "removing a proxy-credential profile removes its rolling backup");
    ApiConnectionStore.Save(path, new[] { connection });
    var before = File.ReadAllText(path, Utf8);
    var invalid = connection.Copy();
    invalid.ProxyAddress = "http://" + username + ":" + password + "@127.0.0.1:8080";
    try { ApiConnectionStore.Save(path, new[] { invalid }); throw new Exception("Proxy URL credentials were saved."); }
    catch (IOException error) { Check(error.InnerException == null && !error.ToString().Contains(username) && !error.ToString().Contains(password)
      && File.ReadAllText(path, Utf8) == before, "proxy URL credentials are rejected before serialization without leaking or replacing settings"); }
  }

  private static void Check(bool condition, string label)
  {
    if (!condition) throw new Exception("FAIL " + label);
    passed++;
  }

  private sealed class RejectConnectionConverter : JsonConverter
  {
    public override bool CanConvert(Type type) => type == typeof(ApiConnection);
    public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
    { throw new Exception("Host converter must not control protected API settings."); }
    public override object ReadJson(JsonReader reader, Type type, object existingValue, JsonSerializer serializer)
    { throw new Exception("Host converter must not control protected API settings."); }
  }
}
