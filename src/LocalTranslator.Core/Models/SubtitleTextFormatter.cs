using System.Text;
using System.Text.RegularExpressions;

namespace LocalTranslator.Core.Models;

/// <summary>
/// Applies the presentation rules shared by live ASR text and translated subtitles.
/// The rules are deliberately conservative so ordinary Chinese expressions are not
/// rewritten as numbers by accident.
/// </summary>
public static partial class SubtitleTextFormatter
{
    private static readonly IReadOnlyDictionary<char, int> Digits = new Dictionary<char, int>
    {
        ['零'] = 0, ['〇'] = 0, ['一'] = 1, ['二'] = 2, ['两'] = 2,
        ['三'] = 3, ['四'] = 4, ['五'] = 5, ['六'] = 6, ['七'] = 7,
        ['八'] = 8, ['九'] = 9
    };

    private static readonly HashSet<string> EnglishAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "vs", "etc", "e.g", "i.e"
    };

    public static string NormalizeNumbers(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var result = ChineseDecimalPattern().Replace(text, match =>
        {
            var integer = ParseChineseNumber(match.Groups[1].Value);
            var fraction = ToDigitSequence(match.Groups[2].Value);
            return integer is null || fraction is null ? match.Value : $"{integer}.{fraction}";
        });

        result = TechnicalNumberPattern().Replace(result, match =>
        {
            var value = ParseChineseNumber(match.Groups[1].Value);
            return value is null ? match.Value : $"{value}{match.Groups[2].Value}";
        });

        // Spoken years and resolutions are commonly emitted as individual digits,
        // for example "二零二六" and "三六零P".
        result = DigitSequencePattern().Replace(result, match =>
            ToDigitSequence(match.Value) ?? match.Value);
        return result;
    }

    public static string FormatForDisplay(string text)
    {
        var normalized = NormalizeNumbers(text).Trim();
        if (normalized.Length == 0) return string.Empty;

        var builder = new StringBuilder(normalized.Length + 8);
        for (var index = 0; index < normalized.Length; index++)
        {
            var current = normalized[index];
            if (current is '\r' or '\n')
            {
                if (builder.Length > 0 && builder[^1] != '\n') builder.Append('\n');
                continue;
            }

            builder.Append(current);
            if (!IsSentenceBoundary(normalized, index)) continue;

            while (index + 1 < normalized.Length && char.IsWhiteSpace(normalized[index + 1])) index++;
            if (index + 1 < normalized.Length && builder[^1] != '\n') builder.Append('\n');
        }

        return builder.ToString().Trim();
    }

    private static bool IsSentenceBoundary(string text, int index)
    {
        var value = text[index];
        if (value is '。' or '！' or '？') return true;
        if (value is not ('.' or '!' or '?')) return false;

        if (value == '.' && index > 0 && index + 1 < text.Length &&
            char.IsDigit(text[index - 1]) && char.IsDigit(text[index + 1]))
            return false;

        if (value == '.' && IsEnglishAbbreviation(text, index)) return false;
        var next = index + 1;
        while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
        return next >= text.Length || char.IsUpper(text[next]) || IsCjk(text[next]);
    }

    private static bool IsEnglishAbbreviation(string text, int periodIndex)
    {
        var start = periodIndex - 1;
        while (start >= 0 && (char.IsLetter(text[start]) || text[start] == '.')) start--;
        var token = text[(start + 1)..periodIndex];
        return token.Length == 1 || EnglishAbbreviations.Contains(token);
    }

    private static bool IsCjk(char value) => value is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff';

    private static string? ToDigitSequence(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (!Digits.TryGetValue(character, out var digit) || character == '两') return null;
            builder.Append(digit);
        }
        return builder.ToString();
    }

    private static long? ParseChineseNumber(string value)
    {
        if (value.All(character => Digits.ContainsKey(character) && character != '两'))
        {
            var digits = ToDigitSequence(value);
            return digits is not null && long.TryParse(digits, out var sequence) ? sequence : null;
        }

        long total = 0;
        long section = 0;
        var number = 0;
        foreach (var character in value)
        {
            if (Digits.TryGetValue(character, out var digit))
            {
                number = digit;
                continue;
            }

            var unit = character switch { '十' => 10, '百' => 100, '千' => 1000, '万' => 10000, _ => 0 };
            if (unit == 0) return null;
            if (unit == 10000)
            {
                section = (section + number) * unit;
                total += section;
                section = 0;
            }
            else
            {
                section += (number == 0 ? 1 : number) * unit;
            }
            number = 0;
        }
        return total + section + number;
    }

    [GeneratedRegex("([零〇一二两三四五六七八九十百千万]+)点([零〇一二三四五六七八九]+)", RegexOptions.Compiled)]
    private static partial Regex ChineseDecimalPattern();

    [GeneratedRegex("([零〇一二两三四五六七八九十百千万]+)\\s*(P|K|FPS|Hz|kHz|MHz|GHz|KB|MB|GB|TB|%|％|帧|倍|核|位|年|个月|分钟|秒)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalNumberPattern();

    [GeneratedRegex("[零〇一二三四五六七八九]{2,}", RegexOptions.Compiled)]
    private static partial Regex DigitSequencePattern();
}
