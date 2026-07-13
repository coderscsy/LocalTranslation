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

    private static readonly string[] WeakEnglishEndings =
    [
        "completion", "studies", "passion", "field", "pursue further",
        "driven by", "strong", "academic", "undergraduate", "following my"
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

        return duration >= TimeSpan.FromSeconds(4.2) ||
               normalized.Length >= 180;
    }

    public static bool ShouldFlushOnSpeechBoundary(string text, TimeSpan duration)
    {
        var normalized = Normalize(text);
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        if (ShouldFlush(normalized, duration)) return true;
        if (LooksLikeContinuationFragment(normalized)) return false;
        if (HasWeakEnding(normalized)) return false;

        var wordCount = CountWords(normalized);
        if (wordCount <= 2) return false;

        return duration >= TimeSpan.FromSeconds(1.2) && wordCount >= 3;
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

    private static bool HasWeakEnding(string text)
    {
        var lower = text.Trim().TrimEnd('.', '!', '?', '。', '！', '？').ToLowerInvariant();
        return WeakEnglishEndings.Any(ending => lower.EndsWith(ending, StringComparison.Ordinal));
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
