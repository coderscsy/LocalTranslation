using LocalTranslator.Core.Models;

namespace LocalTranslator.Infrastructure.Services;

public static class SemanticSubtitleBuffer
{
    private static readonly string[] ContinuationStarters =
    [
        "to ", "in ", "of ", "for ", "with ", "from ", "by ", "on ", "at ", "as ",
        "and ", "or ", "but ", "because ", "which ", "that ", "who ", "where ",
        "when ", "while ", "into ", "about ", "a ", "an ", "the "
    ];

    public static string MergeFragments(IEnumerable<string> fragments, SupportedLanguage language)
    {
        var merged = string.Empty;
        foreach (var fragment in fragments)
            merged = JoinFragments(merged, fragment, language);
        return merged;
    }

    public static bool ShouldFlush(string text, TimeSpan duration)
    {
        var normalized = Normalize(text);
        if (string.IsNullOrWhiteSpace(normalized)) return false;

        // A hard limit is only a memory/latency guard for unusually long speech.
        // Ordinary clauses must wait for an actual pause instead of being translated
        // merely because an ASR rolling window reached its maximum duration.
        return duration >= TimeSpan.FromSeconds(24) ||
               normalized.Length >= 600;
    }

    public static bool ShouldFlushOnSpeechBoundary(string text, TimeSpan duration)
    {
        var normalized = Normalize(text);
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        if (ShouldFlush(normalized, duration)) return true;
        if (duration < TimeSpan.FromSeconds(0.75)) return false;

        // This method is called only for a real trailing-silence boundary. Once the
        // speaker has paused, commit even a short reply so the next utterance starts
        // on a new subtitle row instead of being glued to the previous sentence.
        var wordCount = CountWords(normalized);
        return wordCount >= 2 || ContainsCjk(normalized) ||
               duration >= TimeSpan.FromSeconds(1.2);
    }

    public static string JoinFragments(string existing, string next, SupportedLanguage language)
    {
        var left = Normalize(existing);
        var right = Normalize(next);
        if (string.IsNullOrWhiteSpace(left)) return right;
        if (string.IsNullOrWhiteSpace(right)) return left;
        if (left.Contains(right, StringComparison.OrdinalIgnoreCase)) return left;
        if (right.Contains(left, StringComparison.OrdinalIgnoreCase)) return right;

        var overlap = FindSuffixPrefixOverlap(left, right);
        if (overlap > 0) return $"{left}{right[overlap..]}";

        if (EndsSentence(left) && LooksLikeContinuationFragment(right))
            left = TrimSoftSentenceEnding(left);

        if (language is SupportedLanguage.ChineseSimplified or SupportedLanguage.Japanese)
            return $"{left}{right}";

        if (language == SupportedLanguage.AutoDetect &&
            (ContainsCjk(left) || ContainsCjk(right)))
        {
            var separator = NeedsAsciiWordSeparator(left[^1], right[0]) ? " " : string.Empty;
            return $"{left}{separator}{right}";
        }

        return NeedsNoSpaceBefore(right) || NeedsNoSpaceAfter(left)
            ? $"{left}{right}"
            : $"{left} {right}";
    }

    public static string Normalize(string text) =>
        string.Join(' ', text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static bool LooksLikeContinuationFragment(string text)
    {
        var value = text.Trim().TrimStart('"', '\'', '“', '‘', '(');
        var lower = value.ToLowerInvariant();
        var wordCount = CountWords(lower);
        return wordCount <= 2 ||
               wordCount <= 9 && lower.StartsWith("following ", StringComparison.Ordinal) ||
               wordCount <= 7 && ContinuationStarters.Any(lower.StartsWith);
    }

    private static int CountWords(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static bool EndsSentence(string text)
    {
        var value = text.TrimEnd();
        return value.EndsWith('.') ||
               value.EndsWith('!') ||
               value.EndsWith('?') ||
               value.EndsWith('。') ||
               value.EndsWith('！') ||
               value.EndsWith('？');
    }

    private static bool NeedsNoSpaceBefore(string text) =>
        text.Length > 0 && ",.;:!?%)]}，。；：！？）】」』".Contains(text[0]);

    private static bool NeedsNoSpaceAfter(string text) =>
        text.Length > 0 && "([{（【「『".Contains(text[^1]);

    private static bool NeedsAsciiWordSeparator(char left, char right) =>
        left <= 0x7F && right <= 0x7F && char.IsLetterOrDigit(left) && char.IsLetterOrDigit(right);

    private static bool ContainsCjk(string text) =>
        text.EnumerateRunes().Any(rune => rune.Value is
            >= 0x3040 and <= 0x30FF or
            >= 0x31F0 and <= 0x31FF or
            >= 0x3400 and <= 0x4DBF or
            >= 0x4E00 and <= 0x9FFF);

    private static string TrimSoftSentenceEnding(string text) =>
        text.TrimEnd().TrimEnd('.', '。');

    private static int FindSuffixPrefixOverlap(string left, string right)
    {
        var maximum = Math.Min(left.Length, right.Length);
        for (var length = maximum; length >= 4; length--)
        {
            if (left.AsSpan(left.Length - length, length)
                .Equals(right.AsSpan(0, length), StringComparison.OrdinalIgnoreCase))
                return length;
        }

        return 0;
    }
}
