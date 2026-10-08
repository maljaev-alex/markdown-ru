using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace AnotherMarkdown.Translation
{
  public static class ApiConnectionStore
  {
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("AnotherMarkdown.ApiConnection.v1");
    private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings {
      TypeNameHandling = TypeNameHandling.None, MaxDepth = 64, DateParseHandling = DateParseHandling.None,
      CheckAdditionalContent = true
    };

    public static List<ApiConnection> Load(string path)
    {
      if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An API settings path is required.", nameof(path));
      try {
        string json;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        using (var reader = new StreamReader(stream, Utf8, true)) json = reader.ReadToEnd();
        List<ApiConnection> connections;
        // Ignore host-wide JsonConvert.DefaultSettings and its custom converters.
        using (var input = new StringReader(json))
        using (var reader = new JsonTextReader(input))
          connections = JsonSerializer.Create(JsonSettings).Deserialize<List<ApiConnection>>(reader);
        if (connections == null) throw new JsonSerializationException();
        foreach (var connection in connections) {
          if (connection == null) throw new JsonSerializationException();
          bool keyUnavailable, headersUnavailable, usernameUnavailable, passwordUnavailable;
          var key = Unprotect(connection.EncryptedApiKey, "", out keyUnavailable);
          var headers = Unprotect(connection.EncryptedHeaders, "{}", out headersUnavailable);
          var username = Unprotect(connection.EncryptedProxyUsername, "", out usernameUnavailable);
          var password = Unprotect(connection.EncryptedProxyPassword, "", out passwordUnavailable);
          connection.RestoreCredentials(key, keyUnavailable, headers, headersUnavailable,
            username, usernameUnavailable, password, passwordUnavailable);
        }
        return connections;
      }
      catch (FileNotFoundException) { return new List<ApiConnection>(); }
      catch (DirectoryNotFoundException) { return new List<ApiConnection>(); }
      catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException
        || error is DecoderFallbackException || error is ArgumentException || error is NotSupportedException) {
        // JSON and cryptography exceptions can quote input; never attach them to user-visible errors.
        throw new InvalidDataException("The saved API connection settings could not be read.");
      }
    }

    public static string Save(string path, IEnumerable<ApiConnection> connections)
    {
      string ignored;
      return Save(path, connections, false, out ignored);
    }

    public static string Save(string path, IEnumerable<ApiConnection> connections, bool preserveOriginal, out string backupWarning)
    {
      backupWarning = null;
      if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An API settings path is required.", nameof(path));
      if (connections == null) throw new ArgumentNullException(nameof(connections));
      string temporary = null;
      try {
        var snapshot = new List<ApiConnection>();
        foreach (var connection in connections) {
          if (connection == null) throw new ArgumentException("An API connection cannot be null.");
          ApiTranslator.ValidateEndpointCredentials(connection.Endpoint);
          ApiTranslator.ValidateProxyDraft(connection);
          var saved = connection.Copy();
          if (!saved.PreserveEncryptedApiKey) saved.EncryptedApiKey = Protect(saved.ApiKey);
          if (!saved.PreserveEncryptedHeaders) {
            var headers = saved.AdditionalHeadersJson;
            saved.EncryptedHeaders = Protect(string.IsNullOrWhiteSpace(headers) || headers.Trim() == "{}" ? "" : headers);
          }
          if (!saved.PreserveEncryptedProxyUsername) saved.EncryptedProxyUsername = Protect(saved.ProxyUsername);
          if (!saved.PreserveEncryptedProxyPassword) saved.EncryptedProxyPassword = Protect(saved.ProxyPassword);
          snapshot.Add(saved);
        }
        string json;
        using (var output = new StringWriter())
        using (var writer = new JsonTextWriter(output) { Formatting = Formatting.Indented }) {
          JsonSerializer.Create(JsonSettings).Serialize(writer, snapshot);
          writer.Flush(); json = output.ToString();
        }
        var destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, Utf8, 4096, true)) {
          writer.Write(json); writer.Flush(); stream.Flush(true);
        }
        string recovery = null;
        if (File.Exists(destination)) {
          List<ApiConnection> previous = null;
          var preserve = preserveOriginal;
          if (!preserve) {
            try { previous = Load(destination); }
            catch (InvalidDataException) { preserve = true; }
          }
          if (preserve) {
            // A rolling .bak is overwritten on the next save; preserve the unreadable
            // original separately before replacing it for the first time.
            recovery = destination + ".unreadable-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            File.Copy(destination, recovery, false);
          }
          var removedCredentials = !preserve && previous != null && previous.Exists(old => {
            var current = snapshot.Find(value => value.Id == old.Id);
            return (!string.IsNullOrEmpty(old.EncryptedApiKey) && string.IsNullOrEmpty(current?.EncryptedApiKey))
              || (!string.IsNullOrEmpty(old.EncryptedHeaders) && string.IsNullOrEmpty(current?.EncryptedHeaders))
              || (!string.IsNullOrEmpty(old.EncryptedProxyUsername) && string.IsNullOrEmpty(current?.EncryptedProxyUsername))
              || (!string.IsNullOrEmpty(old.EncryptedProxyPassword) && string.IsNullOrEmpty(current?.EncryptedProxyPassword));
          });
          File.Replace(temporary, destination, removedCredentials ? null : destination + ".bak");
          if (removedCredentials) {
            try { File.Delete(destination + ".bak"); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) {
              backupWarning = "Настройки сохранены, но не удалось удалить резервную копию со старыми данными: " + destination + ".bak. Проверьте права и атрибут «Только чтение» этого файла.";
            }
          }
        }
        else File.Move(temporary, destination);
        temporary = null;
        return recovery;
      }
      catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is JsonException
        || error is CryptographicException || error is ArgumentException || error is NotSupportedException) {
        throw new IOException("The API connection settings could not be saved.");
      }
      finally {
        if (temporary != null) {
          try { File.Delete(temporary); }
          catch (IOException) { }
          catch (UnauthorizedAccessException) { }
        }
      }
    }

    private static string Protect(string value)
    {
      if (string.IsNullOrEmpty(value)) return "";
      var bytes = Utf8.GetBytes(value);
      try { return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)); }
      finally { Array.Clear(bytes, 0, bytes.Length); }
    }

    private static string Unprotect(string ciphertext, string emptyValue, out bool unavailable)
    {
      unavailable = false;
      if (string.IsNullOrEmpty(ciphertext)) return emptyValue;
      byte[] bytes = null;
      try {
        bytes = ProtectedData.Unprotect(Convert.FromBase64String(ciphertext), Entropy, DataProtectionScope.CurrentUser);
        return Utf8.GetString(bytes);
      }
      catch (Exception error) when (error is CryptographicException || error is FormatException
        || error is DecoderFallbackException || error is ArgumentException) {
        unavailable = true;
        return emptyValue;
      }
      finally { if (bytes != null) Array.Clear(bytes, 0, bytes.Length); }
    }
  }
}
