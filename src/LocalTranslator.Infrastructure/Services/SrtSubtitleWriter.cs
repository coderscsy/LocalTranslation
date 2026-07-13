using System.Text;
using LocalTranslator.Core.Models;

namespace LocalTranslator.Infrastructure.Services;

public static class SrtSubtitleWriter
{
    public static async Task WriteAsync(string path, IEnumerable<SubtitleSegment> segments, bool bilingual,
        CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder();
        var index = 1;
        foreach (var segment in segments)
        {
            builder.AppendLine((index++).ToString());
            builder.Append(Format(segment.Start)).Append(" --> ").AppendLine(Format(segment.End));
            if (bilingual) builder.AppendLine(segment.SourceText);
            builder.AppendLine(segment.TranslatedText).AppendLine();
        }
        await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(true), cancellationToken);
    }

    private static string Format(TimeSpan value) => $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}";
}
