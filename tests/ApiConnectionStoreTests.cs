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
    var scratch = Path.GetFullPath(@"D:\Temp\agent\markdown-ru");
    var directory = Path.Combine(scratch, "api-store-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try {
      Run(directory);
      Console.WriteLine("PASS API store: " + passed + " assertions");
      return 0;
    }
    catch (Exception error) { Console.Error.WriteLine(error.GetType().Name + ": " + error.Message); return 1; }
    finally {
      var resolved = Path.GetFullPath(directory);
      if (resolved.StartsWith(scratch + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
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

    File.WriteAllText(path, "{ broken-synthetic-json", Utf8);
    try { ApiConnectionStore.Load(path); throw new Exception("Malformed JSON was accepted."); }
    catch (InvalidDataException error) { Check(error.InnerException == null && !error.Message.Contains("broken-synthetic"), "malformed config reports safe generic error"); }
    ApiConnectionStore.Save(path, new[] { loaded });
    Check(File.ReadAllText(path + ".bak", Utf8) == "{ broken-synthetic-json", "replacement retains malformed original for recovery");

    var before = File.ReadAllText(path, Utf8);
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) {
      try { ApiConnectionStore.Save(path, new[] { loaded }); throw new Exception("Locked config was overwritten."); }
      catch (IOException error) { Check(error.InnerException == null, "failed atomic replacement reports safe error"); }
    }
    Check(File.ReadAllText(path, Utf8) == before, "failed replacement preserves existing config");
    Check(Directory.GetFiles(directory, "*.tmp-*").Length == 0, "temporary encrypted file is cleaned after failed replacement");
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
