using LocalTranslator.Core.Models;

namespace LocalTranslator.Core.Abstractions;

public interface ITranslationService
{
    Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default);
}

