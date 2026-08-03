using System.Security.Cryptography;
using System.Text;
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

    public string? Get(string environmentVariableName)
    {
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
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DOC2MD.AzureDocumentIntelligenceKey.v1");

    public static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DOC2MD",
            "settings.json");

    public static Doc2MdSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new Doc2MdSettings();
        }

        var settings = JsonSerializer.Deserialize<Doc2MdSettings>(File.ReadAllText(SettingsPath), JsonOptions)
            ?? new Doc2MdSettings();
        settings.AzureDocumentIntelligenceKey = Unprotect(settings.ProtectedAzureDocumentIntelligenceKey);
        return settings;
    }

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
        settings.ProtectedAzureDocumentIntelligenceKey = Protect(key);
        settings.AzureDocumentIntelligenceKey = null;

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions), Encoding.UTF8);
    }

    private static string Protect(string value)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DOC2MD secure local key storage uses Windows DPAPI.");
        }

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value),
            Entropy,
            DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue) || !OperatingSystem.IsWindows())
        {
            return null;
        }

        var protectedBytes = Convert.FromBase64String(protectedValue);
        var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}
