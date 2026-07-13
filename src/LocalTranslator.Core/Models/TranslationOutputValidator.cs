namespace LocalTranslator.Core.Models;

public static class TranslationOutputValidator
{
    public static bool IsValid(string source, string translation, SupportedLanguage target)
    {
        if (string.IsNullOrWhiteSpace(translation)) return false;
        if (Normalize(source).Equals(Normalize(translation), StringComparison.OrdinalIgnoreCase)) return false;

        var (han, kana, latin) = CountScripts(translation);
        return target switch
        {
            SupportedLanguage.ChineseSimplified => han > 0,
            SupportedLanguage.English => latin > 0 && kana == 0,
            SupportedLanguage.Japanese => kana > 0 || han > 0 && latin == 0,
            _ => true
        };
    }

    public static bool AreEquivalent(string left, string right) =>
        Normalize(left).Equals(Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static (int Han, int Kana, int Latin) CountScripts(string text)
    {
        var han = 0;
        var kana = 0;
        var latin = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF) kana++;
            else if (value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF) han++;
            else if (value is >= 'A' and <= 'Z' or >= 'a' and <= 'z') latin++;
        }

        return (han, kana, latin);
    }

    private static string Normalize(string? value) => string.Concat(
        (value ?? string.Empty).Where(character => !char.IsWhiteSpace(character) && !char.IsPunctuation(character)));
}
