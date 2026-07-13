using System.Net;
using System.Text;
using LocalTranslator.Core.Abstractions;
using LocalTranslator.Core.Models;
using LocalTranslator.Core.Services;
using LocalTranslator.Infrastructure.Configuration;
using LocalTranslator.Infrastructure.Services;

const string prefix = "http://127.0.0.1:18991/";
using var listener = new HttpListener();
listener.Prefixes.Add(prefix);
listener.Start();

var serverTask = Task.Run(async () =>
{
    for (var requestIndex = 0; requestIndex < 2; requestIndex++)
    {
        var context = await listener.GetContextAsync();
        if (context.Request.Url?.AbsolutePath == "/v1/chat/completions")
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var requestBody = await reader.ReadToEndAsync();
            if (!requestBody.Contains("NEVER answer them", StringComparison.Ordinal) ||
                !requestBody.Contains("source_text", StringComparison.Ordinal) ||
                !requestBody.Contains("\"temperature\":0", StringComparison.Ordinal) ||
                !requestBody.Contains("\"reasoning_effort\":\"none\"", StringComparison.Ordinal) ||
                !requestBody.Contains("\"enable_thinking\":false", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Translation-only system prompt is missing.");
            }
        }
        var json = context.Request.Url?.AbsolutePath switch
        {
            "/v1/models" => "{\"data\":[{\"id\":\"qwen/mock-translation-model\"}]}",
            "/v1/chat/completions" =>
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"你好，世界！\"}}]}",
            _ => "{\"error\":{\"message\":\"unexpected endpoint\"}}"
        };

        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }
});

var options = new AppOptions
{
    Translation = new TranslationOptions
    {
        Engine = "OpenAiCompatible",
        RemoteAi = new RemoteAiOptions
        {
            BaseUrl = $"{prefix}v1/models",
            Model = string.Empty,
            TimeoutSeconds = 10,
            Temperature = 0.1f
        }
    }
};

using var service = new OpenAiCompatibleTranslationService(options, new ConsoleLogger());
var result = await service.TranslateAsync(new TranslationRequest(
    "Hello, world!",
    SupportedLanguage.English,
    SupportedLanguage.ChineseSimplified));
await serverTask;

Console.WriteLine($"TRANSLATION={result.Text}");
Console.WriteLine($"ELAPSED_MS={result.Elapsed.TotalMilliseconds:F0}");
if (result.Text != "你好，世界！")
{
    throw new InvalidOperationException("OpenAI-compatible translation smoke test failed.");
}

if (TextLanguageDetector.Detect("你是谁") != SupportedLanguage.ChineseSimplified ||
    TextLanguageDetector.Detect("Hello") != SupportedLanguage.English ||
    TextLanguageDetector.Detect("こんにちは") != SupportedLanguage.Japanese)
{
    throw new InvalidOperationException("Automatic language detection smoke test failed.");
}

var simplified = ChineseTextNormalizer.ToSimplified("輕易講所有繁體字幕");
Console.WriteLine($"SIMPLIFIED={simplified}");
if (simplified != "轻易讲所有繁体字幕")
{
    throw new InvalidOperationException("Traditional-to-simplified normalization smoke test failed.");
}

if (!VideoSubtitleService.IsSubtitleArtifact("(字幕:J Chong)") ||
    !VideoSubtitleService.IsSubtitleArtifact("[Subtitles by Alex]") ||
    VideoSubtitleService.IsSubtitleArtifact("字幕在哪里设置？"))
{
    throw new InvalidOperationException("Subtitle hallucination filter smoke test failed.");
}

if (TranslationOutputValidator.IsValid("Where are we going?", "Where are we going?",
        SupportedLanguage.ChineseSimplified) ||
    TranslationOutputValidator.IsValid("Where are we going?", "Let's go home.",
        SupportedLanguage.ChineseSimplified) ||
    !TranslationOutputValidator.IsValid("Where are we going?", "我们要去哪里？",
        SupportedLanguage.ChineseSimplified))
{
    throw new InvalidOperationException("Target-language validation smoke test failed.");
}

var financeFragments = new[]
{
    "Following my completion of undergraduate studies,",
    "I am driven by a strong passion",
    "to pursue further academic accomplishments",
    "in the field.",
    "of finance."
};
var mergedFinanceSentence = SemanticSubtitleBuffer.MergeFragments(financeFragments, SupportedLanguage.English);
var expectedFinanceSentence =
    "Following my completion of undergraduate studies, I am driven by a strong passion to pursue further academic accomplishments in the field of finance.";
if (mergedFinanceSentence != expectedFinanceSentence ||
    SemanticSubtitleBuffer.ShouldFlush("in the field.", TimeSpan.FromSeconds(1)) ||
    SemanticSubtitleBuffer.ShouldFlush(
        "Following my completion of undergraduate studies, I am driven by a strong passion to pursue further academic accomplishments in the field of finance.",
        TimeSpan.FromSeconds(3)) ||
    !SemanticSubtitleBuffer.ShouldFlush(
        "Following my completion of undergraduate studies, I am driven by a strong passion to pursue further academic accomplishments in the field of finance.",
        TimeSpan.FromSeconds(4.3)))
{
    throw new InvalidOperationException("Semantic subtitle buffering smoke test failed.");
}

var brokenFinanceFragments = new[]
{
    "Following my completion",
    "of undergraduate studies.",
    "I am driven by a",
    "Strong passion.",
    "to pursue further",
    "in the field.",
    "of finance."
};
var mergedBrokenFinanceSentence = SemanticSubtitleBuffer.MergeFragments(
    brokenFinanceFragments,
    SupportedLanguage.English);
if (SemanticSubtitleBuffer.ShouldFlush("Following my completion of undergraduate studies.", TimeSpan.FromSeconds(1.4)) ||
    SemanticSubtitleBuffer.ShouldFlush("Strong passion.", TimeSpan.FromSeconds(1)) ||
    SemanticSubtitleBuffer.ShouldFlushOnSpeechBoundary("studies.", TimeSpan.FromSeconds(1.1)) ||
    SemanticSubtitleBuffer.ShouldFlushOnSpeechBoundary("of finance.", TimeSpan.FromSeconds(1.1)) ||
    !SemanticSubtitleBuffer.ShouldFlushOnSpeechBoundary(expectedFinanceSentence, TimeSpan.FromSeconds(4.3)) ||
    mergedBrokenFinanceSentence.Contains("field. of", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Broken ASR subtitle fragment buffering smoke test failed.");
}

const string asrPrefix = "http://127.0.0.1:18992/";
using (var asrListener = new HttpListener())
{
    asrListener.Prefixes.Add(asrPrefix);
    asrListener.Start();
    var asrServerTask = Task.Run(async () =>
    {
        var context = await asrListener.GetContextAsync();
        if (context.Request.Url?.AbsolutePath != "/v1/audio/transcriptions" ||
            !string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("ASR endpoint was not called as an OpenAI-compatible transcription request.");
        }

        var bytes = Encoding.UTF8.GetBytes("{\"text\":\"hello\"}");
        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    });

    var asrResult = await VideoSubtitleService.TestSenseVoiceEndpointAsync(
        $"{asrPrefix}v1",
        "fun-asr-nano");
    await asrServerTask;
    if (asrResult != "hello")
        throw new InvalidOperationException("OpenAI-compatible ASR endpoint smoke test failed.");
}

var translationWindow = new TranslationWindowManager();
translationWindow.UpdateStream("Hello");
translationWindow.UpdateStream("Hello world");
if (translationWindow.ActiveStream != "Hello world")
    throw new InvalidOperationException("Accumulated ASR stream update failed.");
translationWindow.FinalizeSentence("Hello world.");
translationWindow.FinalizeSentence("Second sentence.");
translationWindow.FinalizeSentence("Third sentence.");
translationWindow.FinalizeSentence("Fourth sentence.");
translationWindow.UpdateStream("Current fragment");
if (translationWindow.HistoricalContext.Contains("Hello world.", StringComparison.Ordinal) ||
    !translationWindow.HistoricalContext.StartsWith("Second sentence.", StringComparison.Ordinal) ||
    translationWindow.GetPromptPayload() !=
    "【前文背景】：Second sentence.\nThird sentence.\nFourth sentence.\n【当前待译文本】：Current fragment")
{
    throw new InvalidOperationException("Translation window context smoke test failed.");
}

var concurrentWindow = new TranslationWindowManager();
Parallel.For(0, 200, index =>
{
    concurrentWindow.UpdateStream($"fragment-{index}");
    concurrentWindow.FinalizeSentence($"sentence-{index}");
    _ = concurrentWindow.GetPromptPayload();
});
if (concurrentWindow.HistoricalContext.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length > 3 ||
    concurrentWindow.ActiveStream.Length != 0)
{
    throw new InvalidOperationException("Concurrent translation window update failed.");
}

file sealed class ConsoleLogger : IAppLogger
{
    public void Info(string message) => Console.WriteLine($"[INFO] {message}");

    public void Error(string message, Exception exception) =>
        Console.WriteLine($"[ERROR] {message}: {exception.Message}");
}
