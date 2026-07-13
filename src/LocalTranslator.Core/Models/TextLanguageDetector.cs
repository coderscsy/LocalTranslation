namespace LocalTranslator.Core.Models;

public static class TextLanguageDetector
{
    public static SupportedLanguage? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

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

        if (kana > 0) return SupportedLanguage.Japanese;
        if (han > 0 && han >= latin) return SupportedLanguage.ChineseSimplified;
        if (latin > 0) return SupportedLanguage.English;
        return null;
    }
}
