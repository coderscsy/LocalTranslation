namespace LocalTranslator.Core.Models;

public sealed record TranslationRequest(
    string Text,
    SupportedLanguage SourceLanguage,
    SupportedLanguage TargetLanguage,
    string? Context = null,
    bool RequireTargetLanguage = false,
    int? MaxOutputTokens = null);
