using LocalTranslator.Core.Abstractions;
using LocalTranslator.Core.Models;

namespace LocalTranslator.Core.Workflows;

public sealed class ScreenshotTranslationWorkflow(
    IOcrService ocrService,
    ITranslationService translationService)
{
    public async Task<ScreenshotTranslationResult> ExecuteAsync(
        byte[] pngImage,
        SupportedLanguage sourceLanguage,
        SupportedLanguage targetLanguage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pngImage);

        if (pngImage.Length == 0)
        {
            throw new ArgumentException("截图数据为空。", nameof(pngImage));
        }

        var ocrResult = await ocrService.RecognizeAsync(
            new OcrRequest(pngImage, sourceLanguage),
            cancellationToken);

        if (string.IsNullOrWhiteSpace(ocrResult.Text))
        {
            return new ScreenshotTranslationResult(string.Empty, string.Empty, ocrResult.Elapsed, TimeSpan.Zero);
        }

        var translationResult = await translationService.TranslateAsync(
            new TranslationRequest(ocrResult.Text, sourceLanguage, targetLanguage),
            cancellationToken);

        return new ScreenshotTranslationResult(
            ocrResult.Text,
            translationResult.Text,
            ocrResult.Elapsed,
            translationResult.Elapsed);
    }
}

