namespace LocalTranslator.Core.Models;

public sealed record ScreenshotTranslationResult(
    string RecognizedText,
    string TranslatedText,
    TimeSpan OcrElapsed,
    TimeSpan TranslationElapsed);

