using System;
using Newtonsoft.Json;

namespace AnotherMarkdown.Translation
{
  [JsonObject(MemberSerialization.OptIn)]
  public sealed class ApiConnection
  {
    private const string UnavailableCredentials = "Some saved API credentials could not be decrypted for this Windows user. Re-enter or explicitly clear them.";
    private string apiKey = "";
    private string additionalHeadersJson = "{}";
    private bool apiKeyChanged, headersChanged, apiKeyUnavailable, headersUnavailable;

    [JsonProperty]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonProperty]
    public string Name { get; set; } = "API";
    [JsonProperty]
    public string Protocol { get; set; } = "chat-completions";
    [JsonProperty]
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    [JsonProperty]
    public string Model { get; set; } = "";
    [JsonProperty]
    public string ReasoningEffort { get; set; } = "";

    [JsonIgnore]
    public string ApiKey {
      get { return apiKey; }
      set {
        value = value ?? "";
        if (apiKey == value) return;
        apiKey = value; apiKeyChanged = true; apiKeyUnavailable = false;
        UpdateCredentialError();
      }
    }

    [JsonIgnore]
    public string AdditionalHeadersJson {
      get { return additionalHeadersJson; }
      set {
        value = value ?? "{}";
        if (additionalHeadersJson == value) return;
        additionalHeadersJson = value; headersChanged = true; headersUnavailable = false;
        UpdateCredentialError();
      }
    }

    [JsonProperty]
    public string AdditionalParametersJson { get; set; } = "{}";
    [JsonProperty]
    public string AuthHeader { get; set; } = "";
    [JsonProperty]
    public string AuthPrefix { get; set; } = "";
    [JsonProperty]
    // Zero leaves the output budget to the service. Reasoning may consume this budget too.
    public int MaxOutputTokens { get; set; }
    [JsonProperty]
    public string TokenLimitParameter { get; set; } = "max_tokens";
    [JsonProperty]
    public double? Temperature { get; set; }
    [JsonProperty]
    public string EncryptedApiKey { get; set; } = "";
    [JsonProperty]
    public string EncryptedHeaders { get; set; } = "";
    [JsonIgnore]
    public string CredentialError { get; internal set; }

    public ApiConnection Copy() => (ApiConnection)MemberwiseClone();
    public override string ToString() => Name;

    public void ClearCredentials()
    {
      apiKey = ""; additionalHeadersJson = "{}";
      EncryptedApiKey = EncryptedHeaders = "";
      apiKeyChanged = headersChanged = true;
      apiKeyUnavailable = headersUnavailable = false;
      CredentialError = null;
    }

    // Untouched unavailable ciphertext must survive an ordinary UI copy/save.
    internal bool PreserveEncryptedApiKey => !apiKeyChanged && !string.IsNullOrEmpty(EncryptedApiKey)
      && (apiKeyUnavailable || apiKey.Length == 0);
    internal bool PreserveEncryptedHeaders => !headersChanged && !string.IsNullOrEmpty(EncryptedHeaders)
      && (headersUnavailable || string.IsNullOrWhiteSpace(additionalHeadersJson) || additionalHeadersJson.Trim() == "{}");

    internal void RestoreCredentials(string key, bool keyUnavailable, string headers, bool headerUnavailable)
    {
      apiKey = key ?? ""; additionalHeadersJson = headers ?? "{}";
      apiKeyChanged = headersChanged = false;
      apiKeyUnavailable = keyUnavailable; headersUnavailable = headerUnavailable;
      UpdateCredentialError();
    }

    private void UpdateCredentialError()
    {
      CredentialError = apiKeyUnavailable || headersUnavailable ? UnavailableCredentials : null;
    }
  }
}
