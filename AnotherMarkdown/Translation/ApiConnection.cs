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
    private string proxyUsername = "", proxyPassword = "";
    private bool apiKeyChanged, headersChanged, apiKeyUnavailable, headersUnavailable;
    private bool proxyUsernameChanged, proxyPasswordChanged, proxyUsernameUnavailable, proxyPasswordUnavailable;

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
    // A level may be sent only after this model advertised it in the API catalog.
    // Older saved profiles lack this marker and keep their effort inactive.
    [JsonProperty]
    public string ReasoningEffortModel { get; set; } = "";
    [JsonProperty]
    public string ReasoningEffortCatalogKey { get; set; } = "";
    // Missing values in older profiles inherit the previously shared translation settings.
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? TimeoutSeconds { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? ParallelRequests { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? MinimumChunkCharacters { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? ShowButtons { get; set; }
    [JsonProperty]
    public string ProxyMode { get; set; } = "system";
    [JsonProperty]
    public string ProxyAddress { get; set; } = "";
    [JsonProperty]
    public bool ProxyUseDefaultCredentials { get; set; }

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

    [JsonIgnore]
    public string ProxyUsername {
      get { return proxyUsername; }
      set {
        value = value ?? "";
        if (proxyUsername == value) return;
        proxyUsername = value; proxyUsernameChanged = true; proxyUsernameUnavailable = false;
        UpdateCredentialError();
      }
    }

    [JsonIgnore]
    public string ProxyPassword {
      get { return proxyPassword; }
      set {
        value = value ?? "";
        if (proxyPassword == value) return;
        proxyPassword = value; proxyPasswordChanged = true; proxyPasswordUnavailable = false;
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
    [JsonProperty]
    public string EncryptedProxyUsername { get; set; } = "";
    [JsonProperty]
    public string EncryptedProxyPassword { get; set; } = "";
    [JsonIgnore]
    public string CredentialError { get; internal set; }

    internal string RequiredCredentialError {
      get {
        if (apiKeyUnavailable || headersUnavailable) return UnavailableCredentials;
        if (!string.Equals((ProxyMode ?? "system").Trim(), "custom", StringComparison.OrdinalIgnoreCase) || ProxyUseDefaultCredentials) return null;
        return proxyUsernameUnavailable || proxyPasswordUnavailable && proxyUsername.Length != 0 ? UnavailableCredentials : null;
      }
    }

    public ApiConnection Copy() => (ApiConnection)MemberwiseClone();
    public override string ToString() => Name;

    public void ClearCredentials(bool includeProxy = true)
    {
      apiKey = ""; additionalHeadersJson = "{}";
      EncryptedApiKey = EncryptedHeaders = "";
      apiKeyChanged = headersChanged = true;
      apiKeyUnavailable = headersUnavailable = false;
      if (includeProxy) {
        proxyUsername = proxyPassword = "";
        EncryptedProxyUsername = EncryptedProxyPassword = "";
        proxyUsernameChanged = proxyPasswordChanged = true;
        proxyUsernameUnavailable = proxyPasswordUnavailable = false;
      }
      UpdateCredentialError();
    }

    // Untouched unavailable ciphertext must survive an ordinary UI copy/save.
    internal bool PreserveEncryptedApiKey => !apiKeyChanged && !string.IsNullOrEmpty(EncryptedApiKey)
      && (apiKeyUnavailable || apiKey.Length == 0);
    internal bool PreserveEncryptedHeaders => !headersChanged && !string.IsNullOrEmpty(EncryptedHeaders)
      && (headersUnavailable || string.IsNullOrWhiteSpace(additionalHeadersJson) || additionalHeadersJson.Trim() == "{}");
    internal bool PreserveEncryptedProxyUsername => !proxyUsernameChanged && !string.IsNullOrEmpty(EncryptedProxyUsername)
      && (proxyUsernameUnavailable || proxyUsername.Length == 0);
    internal bool PreserveEncryptedProxyPassword => !proxyPasswordChanged && !string.IsNullOrEmpty(EncryptedProxyPassword)
      && (proxyPasswordUnavailable || proxyPassword.Length == 0);

    internal void RestoreCredentials(string key, bool keyUnavailable, string headers, bool headerUnavailable,
      string username = "", bool usernameUnavailable = false, string password = "", bool passwordUnavailable = false)
    {
      apiKey = key ?? ""; additionalHeadersJson = headers ?? "{}";
      apiKeyChanged = headersChanged = false;
      apiKeyUnavailable = keyUnavailable; headersUnavailable = headerUnavailable;
      proxyUsername = username ?? ""; proxyPassword = password ?? "";
      proxyUsernameChanged = proxyPasswordChanged = false;
      proxyUsernameUnavailable = usernameUnavailable; proxyPasswordUnavailable = passwordUnavailable;
      UpdateCredentialError();
    }

    private void UpdateCredentialError()
    {
      CredentialError = apiKeyUnavailable || headersUnavailable || proxyUsernameUnavailable || proxyPasswordUnavailable ? UnavailableCredentials : null;
    }
  }
}
