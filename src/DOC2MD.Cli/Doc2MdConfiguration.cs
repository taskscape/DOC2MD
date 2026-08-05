using System.Text.Json;
using System.Text.Json.Serialization;

internal sealed class Doc2MdSettings
{
    public string? PdfProcessing { get; set; }

    public string? AzureDocumentIntelligenceEndpoint { get; set; }

    public string? AzureDocumentIntelligenceLocale { get; set; }

    public string? AzureDocumentIntelligenceTier { get; set; }

    public string? ProtectedAzureDocumentIntelligenceKey { get; set; }

    public string? AzureDocumentIntelligenceKey { get; set; }

    /// <summary>
    /// Resolves a supported setting by applying the process environment override before the persisted value.
    /// </summary>
    /// <param name="environmentVariableName">The supported DOC2MD environment-variable name.</param>
    /// <returns>The effective value, or <see langword="null"/> when the setting is unknown or unset.</returns>
    public string? Get(string environmentVariableName)
    {
        // Environment variables are intentionally authoritative so automation can override a user's saved defaults.
        var environmentValue = Environment.GetEnvironmentVariable(environmentVariableName);
        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return environmentValue;
        }

        return environmentVariableName switch
        {
            "DOC2MD_PDF_PROCESSING" => PdfProcessing,
            "DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_ENDPOINT" => AzureDocumentIntelligenceEndpoint,
            "DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_KEY" => AzureDocumentIntelligenceKey,
            "DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_LOCALE" => AzureDocumentIntelligenceLocale,
            "DOC2MD_AZURE_DOCUMENT_INTELLIGENCE_TIER" => AzureDocumentIntelligenceTier,
            _ => null
        };
    }
}

internal static class Doc2MdConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly ISecretStore SecretStore = SecretStoreFactory.Create();

    public static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DOC2MD",
            "settings.json");

    /// <summary>
    /// Loads the current user's persisted DOC2MD settings and decrypts the Azure key for in-process use.
    /// </summary>
    /// <returns>A settings instance; missing files are treated as an empty configuration.</returns>
    public static Doc2MdSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new Doc2MdSettings();
        }

        var settings = JsonSerializer.Deserialize<Doc2MdSettings>(File.ReadAllText(SettingsPath), JsonOptions)
            ?? new Doc2MdSettings();
        // Only the protected representation is persisted; plaintext exists solely on the returned in-memory object.
        settings.AzureDocumentIntelligenceKey = SecretStore.Load(settings.ProtectedAzureDocumentIntelligenceKey);
        return settings;
    }

    /// <summary>
    /// Persists Azure Document Intelligence settings with the API key protected by the current operating system.
    /// </summary>
    /// <param name="endpoint">The Azure Document Intelligence endpoint.</param>
    /// <param name="key">The API key to protect with the platform credential store.</param>
    /// <param name="locale">The optional document locale.</param>
    /// <param name="tier">The Azure service tier identifier.</param>
    /// <param name="useAzureByDefault">Whether future conversions should select Azure processing by default.</param>
    public static void SaveAzure(
        string endpoint,
        string key,
        string? locale,
        string tier,
        bool useAzureByDefault)
    {
        var settings = Load();
        settings.PdfProcessing = useAzureByDefault ? "azure" : settings.PdfProcessing;
        settings.AzureDocumentIntelligenceEndpoint = endpoint;
        settings.AzureDocumentIntelligenceLocale = string.IsNullOrWhiteSpace(locale) ? null : locale;
        settings.AzureDocumentIntelligenceTier = tier;
        // Clear the transient plaintext property before serialization to prevent accidental credential disclosure.
        settings.ProtectedAzureDocumentIntelligenceKey = SecretStore.Save(key);
        settings.AzureDocumentIntelligenceKey = null;

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions), System.Text.Encoding.UTF8);
    }

    public static string SecretStorageDescription => SecretStore.Description;
}
