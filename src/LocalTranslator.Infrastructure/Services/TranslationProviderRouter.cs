using LocalTranslator.Core.Abstractions;
using LocalTranslator.Core.Exceptions;
using LocalTranslator.Core.Models;
using LocalTranslator.Infrastructure.Configuration;

namespace LocalTranslator.Infrastructure.Services;

public sealed class TranslationProviderRouter(
    LocalLlmTranslationService localService,
    SecureTranslationProviderStore providerStore,
    IAppLogger logger) : ITranslationService
{
    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        TranslationResult result;
        var settings = providerStore.Load();
        if (settings.ActiveProviderId.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            result = await localService.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
            return NormalizeTarget(result, request.TargetLanguage);
        }

        var provider = settings.OnlineProviders.FirstOrDefault(item =>
            item.Id.Equals(settings.ActiveProviderId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            throw new OfflineEngineException($"未知的翻译 Provider：{settings.ActiveProviderId}");
        }

        if (string.IsNullOrWhiteSpace(provider.BaseUrl))
        {
            throw new OfflineEngineException($"请先配置 {provider.DisplayName} 的 API 地址。");
        }

        logger.Info($"Using translation provider: {provider.DisplayName}.");
        using var service = CreateOnlineService(provider);
        result = await service.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
        return NormalizeTarget(result, request.TargetLanguage);
    }

    private static TranslationResult NormalizeTarget(TranslationResult result, SupportedLanguage target) =>
        target == SupportedLanguage.ChineseSimplified
            ? result with { Text = ChineseTextNormalizer.ToSimplified(result.Text) }
            : result;

    public OpenAiCompatibleTranslationService CreateOnlineService(
        OnlineProviderSettings provider,
        string? systemPrompt = null) =>
        new(
            new RemoteAiOptions
            {
                DisplayName = provider.DisplayName,
                BaseUrl = provider.BaseUrl,
                Model = provider.Model,
                ApiKey = provider.ApiKey,
                TimeoutSeconds = 120,
                Temperature = 0.1f,
                SystemPrompt = systemPrompt ?? string.Empty
            },
            logger);
}
