namespace LocalTranslator.Core.Models;

public enum VideoSubtitleMode { Live, Movie }

public sealed record SubtitleSegment(
    TimeSpan Start,
    TimeSpan End,
    string SourceText,
    string TranslatedText);
