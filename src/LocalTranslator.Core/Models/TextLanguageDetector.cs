namespace LocalTranslator.Core.Models;

public static class TextLanguageDetector
{
    public static SupportedLanguage? Detect(string? text)
    {
        var profile = Analyze(text);

        if (profile.Kana > 0) return SupportedLanguage.Japanese;
        if (profile.Han > 0 && profile.Han >= profile.Latin) return SupportedLanguage.ChineseSimplified;
        if (profile.Latin > 0) return SupportedLanguage.English;
        return null;
    }

    public static SupportedLanguage? DetectForTranslation(string? text)
    {
        var profile = Analyze(text);
        if (profile.IsCjkLatinMixed) return SupportedLanguage.AutoDetect;
        return Detect(text);
    }

    public static bool RequiresTranslation(
        string? text,
        SupportedLanguage source,
        SupportedLanguage target)
    {
        if (source != target) return true;
        var profile = Analyze(text);
        return target switch
        {
            SupportedLanguage.ChineseSimplified => profile.Kana > 0 || profile.Latin >= 4,
            SupportedLanguage.Japanese => profile.Latin >= 4 || profile.Han > 0 && profile.Kana == 0,
            SupportedLanguage.English => profile.Han > 0 || profile.Kana > 0,
            _ => true
        };
    }

    public static LanguageScriptProfile Analyze(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;

        var kana = 0;
        var han = 0;
        var latin = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF)
                kana++;
            else if (value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF)
                han++;
            else if (value is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
                latin++;
        }

        return new LanguageScriptProfile(han, kana, latin);
    }
}

public readonly record struct LanguageScriptProfile(int Han, int Kana, int Latin)
{
    public bool IsCjkLatinMixed => Han + Kana >= 2 && Latin >= 4;
}
