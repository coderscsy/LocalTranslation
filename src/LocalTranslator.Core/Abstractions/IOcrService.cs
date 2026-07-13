using LocalTranslator.Core.Models;

namespace LocalTranslator.Core.Abstractions;

public interface IOcrService
{
    Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken = default);
}

