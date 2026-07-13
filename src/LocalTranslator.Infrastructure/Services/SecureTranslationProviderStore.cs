using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalTranslator.Infrastructure.Services;

public sealed class SecureTranslationProviderStore
{
    private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes("LocalTranslator.ProviderSettings.v1");
    private readonly object _syncRoot = new();
    private readonly string _path;

    public SecureTranslationProviderStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalTranslator");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "translation-providers.dat");
    }

    public TranslationProviderSettings Load()
    {
        lock (_syncRoot)
        {
            if (!File.Exists(_path))
            {
                return TranslationProviderSettings.CreateDefault();
            }

            try
            {
                var encrypted = File.ReadAllBytes(_path);
                var json = ProtectedData.Unprotect(
                    encrypted,
                    OptionalEntropy,
                    DataProtectionScope.CurrentUser);
                return Normalize(JsonSerializer.Deserialize<TranslationProviderSettings>(json)
                                 ?? TranslationProviderSettings.CreateDefault());
            }
            catch
            {
                return TranslationProviderSettings.CreateDefault();
            }
        }
    }

    private static TranslationProviderSettings Normalize(TranslationProviderSettings settings)
    {
        if (settings.ActiveProviderId.Equals("mac", StringComparison.OrdinalIgnoreCase))
            settings.ActiveProviderId = "local-service";
        if (settings.OnlineProviders.All(item => item.Id != "local-service"))
            settings.OnlineProviders.Insert(0, new("local-service", "本地 / 局域网 OpenAI-compatible", "", "", ""));
        for (var index = 0; index < settings.OnlineProviders.Count; index++)
        {
            var provider = settings.OnlineProviders[index];
            var normalizedUrl = OpenAiCompatibleTranslationService.NormalizeBaseUrl(provider.BaseUrl);
            if (!normalizedUrl.Equals(provider.BaseUrl, StringComparison.Ordinal))
                settings.OnlineProviders[index] = provider with { BaseUrl = normalizedUrl };
        }
        return settings;
    }

    public void Save(TranslationProviderSettings settings)
    {
        lock (_syncRoot)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(settings);
            var encrypted = ProtectedData.Protect(
                json,
                OptionalEntropy,
                DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_path, encrypted);
        }
    }
}

public sealed class TranslationProviderSettings
{
    public string ActiveProviderId { get; set; } = "local";

    public List<OnlineProviderSettings> OnlineProviders { get; set; } = [];

    public static TranslationProviderSettings CreateDefault() => new()
    {
        ActiveProviderId = "local",
        OnlineProviders =
        [
            new("local-service", "本地 / 局域网 OpenAI-compatible", "", "", ""),
            new("openai", "OpenAI", "https://api.openai.com/v1", "", ""),
            new("deepseek", "DeepSeek", "https://api.deepseek.com", "deepseek-v4-flash", ""),
            new("gemini", "Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-3.5-flash", ""),
            new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "~openai/gpt-latest", ""),
            new("custom", "自定义 OpenAI-compatible", "", "", "")
        ]
    };
}

public sealed record OnlineProviderSettings(
    string Id,
    string DisplayName,
    string BaseUrl,
    string Model,
    string ApiKey);
