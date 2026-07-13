using LocalTranslator.Core.Abstractions;
using LocalTranslator.Core.Models;
using LocalTranslator.Infrastructure.Configuration;
using LocalTranslator.Infrastructure.Services;

if (args.Length < 2)
    throw new ArgumentException("Usage: <base-url> <model>");

using var service = new OpenAiCompatibleTranslationService(
    new RemoteAiOptions
    {
        DisplayName = "Live smoke test",
        BaseUrl = args[0],
        Model = args[1],
        ApiKey = string.Empty,
        TimeoutSeconds = 240,
        Temperature = 0,
        SystemPrompt = args.Length > 2 && args[2].Equals("video", StringComparison.OrdinalIgnoreCase)
            ? VideoSubtitleTranslationPrompt.System
            : string.Empty
    },
    new ConsoleLogger());

var videoMode = args.Length > 2 && args[2].Equals("video", StringComparison.OrdinalIgnoreCase);
var result = await service.TranslateAsync(videoMode
    ? new TranslationRequest(
        "That was really close. Let's try again next round.",
        SupportedLanguage.English,
        SupportedLanguage.ChineseSimplified,
        "They're watching a live game together.")
    : new TranslationRequest(
        "你是谁",
        SupportedLanguage.ChineseSimplified,
        SupportedLanguage.English));

Console.WriteLine($"TRANSLATION={result.Text}");
Console.WriteLine($"ELAPSED_MS={result.Elapsed.TotalMilliseconds:F0}");
if (!videoMode && !result.Text.Contains("Who are you", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Provider returned an answer or unexpected text instead of a translation.");
if (videoMode && (string.IsNullOrWhiteSpace(result.Text) ||
                  result.Text.Contains("live game", StringComparison.OrdinalIgnoreCase) ||
                  !TranslationOutputValidator.IsValid(
                      "That was really close. Let's try again next round.",
                      result.Text,
                      SupportedLanguage.ChineseSimplified)))
    throw new InvalidOperationException("Video provider returned empty text or repeated previous context.");

file sealed class ConsoleLogger : IAppLogger
{
    public void Info(string message) => Console.WriteLine($"[INFO] {message}");
    public void Error(string message, Exception exception) =>
        Console.WriteLine($"[ERROR] {message}: {exception.Message}");
}
