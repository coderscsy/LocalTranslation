namespace LocalTranslator.Core.Models;

public sealed record LanguageItem(SupportedLanguage Value, string Code, string DisplayName)
{
    public static IReadOnlyList<LanguageItem> All { get; } =
    [
        Create(SupportedLanguage.AutoDetect),
        Create(SupportedLanguage.ChineseSimplified),
        Create(SupportedLanguage.English),
        Create(SupportedLanguage.Japanese)
    ];

    public static IReadOnlyList<LanguageItem> Targets { get; } =
        All.Where(item => item.Value != SupportedLanguage.AutoDetect).ToArray();

    private static LanguageItem Create(SupportedLanguage language) =>
        new(language, language.ToCode(), language.ToDisplayName());
}
