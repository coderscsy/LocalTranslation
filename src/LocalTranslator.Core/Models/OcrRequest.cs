namespace LocalTranslator.Core.Models;

public sealed record OcrRequest(byte[] PngImage, SupportedLanguage Language);

