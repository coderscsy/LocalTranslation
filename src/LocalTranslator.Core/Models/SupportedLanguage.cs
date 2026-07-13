namespace LocalTranslator.Core.Models;

public enum SupportedLanguage
{
    AutoDetect,
    ChineseSimplified,
    English,
    Japanese
}

public static class SupportedLanguageExtensions
{
    public static string ToCode(this SupportedLanguage language) => language switch
    {
        SupportedLanguage.AutoDetect => "auto",
        SupportedLanguage.ChineseSimplified => "zh-CN",
        SupportedLanguage.English => "en",
        SupportedLanguage.Japanese => "ja",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null)
    };

    public static string ToDisplayName(this SupportedLanguage language) => language switch
    {
        SupportedLanguage.AutoDetect => "自动检测",
        SupportedLanguage.ChineseSimplified => "简体中文",
        SupportedLanguage.English => "English",
        SupportedLanguage.Japanese => "日本語",
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null)
    };
}
