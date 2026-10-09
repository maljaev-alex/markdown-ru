using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AnotherMarkdown.Translation
{
  public static class CliConnectionStore
  {
    public const int MaximumConnections = 256;
    public const int MaximumFileBytes = 32000000;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings {
      TypeNameHandling = TypeNameHandling.None, MissingMemberHandling = MissingMemberHandling.Error,
      MaxDepth = 32, DateParseHandling = DateParseHandling.None
    };

    public static List<CliConnectionSettings> Load(string path)
    {
      if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A CLI settings path is required.", nameof(path));
      try {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) {
          if (stream.Length > MaximumFileBytes) throw new InvalidDataException();
          using (var input = new StreamReader(stream, Utf8, true))
          using (var reader = new JsonTextReader(input) { MaxDepth = 32, DateParseHandling = DateParseHandling.None }) {
            var json = JToken.ReadFrom(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            if (!(json is JArray array) || array.Count > MaximumConnections || reader.Read()) throw new JsonSerializationException();
            var serializer = JsonSerializer.Create(JsonSettings); var result = new List<CliConnectionSettings>();
            foreach (var token in array) {
              if (!(token is JObject)) throw new JsonSerializationException();
              var connection = token.ToObject<CliConnectionSettings>(serializer); Validate(connection); CliConnectionSettings.Upsert(result, connection);
            }
            return result;
          }
        }
      }
      catch (FileNotFoundException) { return new List<CliConnectionSettings>(); }
      catch (DirectoryNotFoundException) { return new List<CliConnectionSettings>(); }
      catch (Exception error) when (error is InvalidDataException || error is IOException || error is UnauthorizedAccessException || error is JsonException
        || error is DecoderFallbackException || error is ArgumentException || error is NotSupportedException) {
        throw new InvalidDataException("The saved CLI connection settings could not be read.");
      }
    }

    public static void Save(string path, IEnumerable<CliConnectionSettings> connections)
    {
      if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A CLI settings path is required.", nameof(path));
      if (connections == null) throw new ArgumentNullException(nameof(connections));
      string temporary = null;
      try {
        var snapshot = new List<CliConnectionSettings>(); var supplied = 0;
        foreach (var connection in connections) {
          if (++supplied > MaximumConnections) throw new ArgumentException("Too many CLI connections.");
          Validate(connection); CliConnectionSettings.Upsert(snapshot, connection);
        }
        string json;
        using (var output = new StringWriter())
        using (var writer = new JsonTextWriter(output) { Formatting = Formatting.Indented }) {
          JsonSerializer.Create(JsonSettings).Serialize(writer, snapshot); writer.Flush(); json = output.ToString();
        }
        if (Utf8.GetByteCount(json) > MaximumFileBytes) throw new ArgumentException("CLI settings are too large.");
        var destination = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(destination));
        // Refuse to replace an unreadable/corrupt original. A user may repair or
        // remove it and reload; a normal Save is never an implicit recovery action.
        if (File.Exists(destination)) Load(destination);
        temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, Utf8, 4096, true)) {
          writer.Write(json); writer.Flush(); stream.Flush(true);
        }
        if (File.Exists(destination)) File.Replace(temporary, destination, destination + ".bak");
        else File.Move(temporary, destination);
        temporary = null;
      }
      catch (Exception error) when (error is InvalidDataException || error is IOException || error is UnauthorizedAccessException || error is JsonException
        || error is EncoderFallbackException || error is ArgumentException || error is NotSupportedException) {
        throw new IOException("The CLI connection settings could not be saved.");
      }
      finally {
        if (temporary != null) {
          try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
      }
    }
    private static void Validate(CliConnectionSettings value)
    {
      if (value == null) throw new ArgumentException("A CLI connection cannot be null.");
      if ((value.ProviderId ?? "").Length > 128 || (value.Executable ?? "").Length > 32767 || (value.Model ?? "").Length > 4096
        || (value.ReasoningEffort ?? "").Length > 128 || (value.OutputFormat ?? "").Length > 128
        || (value.Arguments ?? "").Length > TranslationOptions.MaximumStoredArgumentCharacters
        || value.TimeoutSeconds < 10 || value.TimeoutSeconds > 3600 || value.ParallelRequests < 1 || value.ParallelRequests > 8
        || value.MinimumChunkCharacters < 0 || value.MinimumChunkCharacters > 1000000) throw new ArgumentException("Invalid CLI connection settings.");
      foreach (var field in new[] { value.ProviderId, value.Executable, value.Model, value.ReasoningEffort, value.OutputFormat })
        if ((field ?? "").IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0) throw new ArgumentException("Invalid CLI connection field.");
      if ((value.Arguments ?? "").IndexOf('\0') >= 0) throw new ArgumentException("Invalid CLI arguments.");
    }
  }
}
