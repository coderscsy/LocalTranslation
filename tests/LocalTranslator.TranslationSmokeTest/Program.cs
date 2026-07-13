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
                !requestBody.Contains("faithfully and naturally as one coherent passage", StringComparison.Ordinal) ||
                !requestBody.Contains("summarize, omit, invent content", StringComparison.Ordinal) ||
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

const string mixedChineseEnglish = "这个 API endpoint requires authentication 才能继续";
if (TextLanguageDetector.DetectForTranslation(mixedChineseEnglish) != SupportedLanguage.AutoDetect ||
    !TextLanguageDetector.RequiresTranslation(
        mixedChineseEnglish,
        SupportedLanguage.ChineseSimplified,
        SupportedLanguage.ChineseSimplified) ||
    TextLanguageDetector.RequiresTranslation(
        "这是纯中文视频字幕",
        SupportedLanguage.ChineseSimplified,
        SupportedLanguage.ChineseSimplified))
{
    throw new InvalidOperationException("Mixed Chinese-English translation routing smoke test failed.");
}

if (SpeechRecognitionRouter.Resolve(
        SupportedLanguage.AutoDetect,
        SpeechRecognitionEngine.MeetilyParakeet,
        senseVoiceEnabled: true,
        whisperEnabled: true) != SpeechRecognitionEngine.SenseVoiceSmall ||
    SpeechRecognitionRouter.Resolve(
        SupportedLanguage.English,
        SpeechRecognitionEngine.MeetilyParakeet,
        senseVoiceEnabled: true,
        whisperEnabled: true) != SpeechRecognitionEngine.MeetilyParakeet ||
    SpeechRecognitionRouter.Resolve(
        SupportedLanguage.ChineseSimplified,
        SpeechRecognitionEngine.MeetilyParakeet,
        senseVoiceEnabled: false,
        whisperEnabled: true) != SpeechRecognitionEngine.WhisperGgml)
{
    throw new InvalidOperationException("ASR language-aware routing smoke test failed.");
}

var mixedFragments = SemanticSubtitleBuffer.MergeFragments(
    ["这个 API endpoint", "requires authentication", "才能继续"],
    SupportedLanguage.AutoDetect);
if (mixedFragments != "这个 API endpoint requires authentication才能继续")
    throw new InvalidOperationException("Mixed-language subtitle spacing smoke test failed.");

var simplified = ChineseTextNormalizer.ToSimplified("輕易講所有繁體字幕");
Console.WriteLine($"SIMPLIFIED={simplified}");
if (simplified != "轻易讲所有繁体字幕")
{
    throw new InvalidOperationException("Traditional-to-simplified normalization smoke test failed.");
}

var normalizedNumbers = SubtitleTextFormatter.NormalizeNumbers(
    "三六零P 到四三零P，四K，刷新率一百四十四Hz，版本四点一，二零二六年");
if (normalizedNumbers != "360P 到430P，4K，刷新率144Hz，版本4.1，2026年")
    throw new InvalidOperationException($"Spoken-number normalization smoke test failed: {normalizedNumbers}");

var sentenceFormatted = SubtitleTextFormatter.FormatForDisplay(
    "First sentence. Second sentence? 版本4.1没有被拆开。下一句！");
if (sentenceFormatted != "First sentence.\nSecond sentence?\n版本4.1没有被拆开。\n下一句！")
    throw new InvalidOperationException($"Bilingual sentence formatting smoke test failed: {sentenceFormatted}");

if (!VideoSubtitleService.IsSubtitleArtifact("(字幕:J Chong)") ||
    !VideoSubtitleService.IsSubtitleArtifact("[Subtitles by Alex]") ||
    VideoSubtitleService.IsSubtitleArtifact("字幕在哪里设置？"))
{
    throw new InvalidOperationException("Subtitle hallucination filter smoke test failed.");
}

var clampedRecognitionRange = VideoSubtitleService.ClampRecognitionRange(
    TimeSpan.FromSeconds(2.8),
    TimeSpan.FromSeconds(2.8),
    TimeSpan.Zero,
    TimeSpan.FromSeconds(30));
if (clampedRecognitionRange.Start != TimeSpan.FromSeconds(2.8) ||
    clampedRecognitionRange.End != TimeSpan.FromSeconds(5.6))
{
    throw new InvalidOperationException("Whisper timestamp clamping smoke test failed.");
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
    SemanticSubtitleBuffer.ShouldFlush(
        "Following my completion of undergraduate studies, I am driven by a strong passion to pursue further academic accomplishments in the field of finance.",
        TimeSpan.FromSeconds(4.3)) ||
    !SemanticSubtitleBuffer.ShouldFlush(
        "Following my completion of undergraduate studies, I am driven by a strong passion to pursue further academic accomplishments in the field of finance.",
        TimeSpan.FromSeconds(12.1)))
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
    !SemanticSubtitleBuffer.ShouldFlushOnSpeechBoundary(expectedFinanceSentence, TimeSpan.FromSeconds(8.3)) ||
    mergedBrokenFinanceSentence.Contains("field. of", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Broken ASR subtitle fragment buffering smoke test failed.");
}

var rollingFinanceFragments = new[]
{
    ("Following my completion of undergraduate studies.", 2.0),
    ("Following my completion of undergraduate studies, I am driven by a strong passion.", 4.2),
    ("Following my completion of undergraduate studies, I am driven by a strong passion to pursue further academic accomplishments.", 6.6),
    (expectedFinanceSentence, 8.3)
};
for (var index = 0; index < rollingFinanceFragments.Length; index++)
{
    var (text, seconds) = rollingFinanceFragments[index];
    var shouldFlush = SemanticSubtitleBuffer.ShouldFlushOnSpeechBoundary(text, TimeSpan.FromSeconds(seconds));
    if (shouldFlush != (index == rollingFinanceFragments.Length - 1))
        throw new InvalidOperationException($"Rolling finance sentence was finalized at the wrong revision: {index}.");
}

var realSenseVoiceWindowFragments = new[]
{
    "following my completion of undergraduate studies i am driven by a strong passion",
    "a strong passion to pursue further academic accomplishments in the field of finance"
};
var mergedSenseVoiceWindows = SemanticSubtitleBuffer.MergeFragments(
    realSenseVoiceWindowFragments,
    SupportedLanguage.English);
const string expectedSenseVoiceWindows =
    "following my completion of undergraduate studies i am driven by a strong passion to pursue further academic accomplishments in the field of finance";
if (!mergedSenseVoiceWindows.Equals(expectedSenseVoiceWindows, StringComparison.Ordinal) ||
    !SemanticSubtitleBuffer.ShouldFlushOnSpeechBoundary(
        mergedSenseVoiceWindows,
        TimeSpan.FromSeconds(9.2)))
{
    throw new InvalidOperationException("Real SenseVoice rolling-window merge smoke test failed.");
}

const string stableParakeetPrefix = "Following my completion of undergraduate studies,";
var provisionalParakeetRevision = SemanticSubtitleBuffer.JoinFragments(
    stableParakeetPrefix,
    "I am driven by a strong passion to pursue further active.",
    SupportedLanguage.English);
var correctedParakeetRevision = SemanticSubtitleBuffer.JoinFragments(
    stableParakeetPrefix,
    "I am driven by a strong passion to pursue further academic accomplishments in the field of finance.",
    SupportedLanguage.English);
if (!provisionalParakeetRevision.Contains("further active", StringComparison.Ordinal) ||
    !correctedParakeetRevision.Equals(expectedFinanceSentence, StringComparison.Ordinal))
{
    throw new InvalidOperationException("Cumulative Parakeet revision replacement smoke test failed.");
}

var correlatedSource = new SubtitleSegment(TimeSpan.Zero, TimeSpan.FromSeconds(2),
    "Following my completion", string.Empty, 42);
var correlatedFinal = correlatedSource with
{
    End = TimeSpan.FromSeconds(8.3),
    SourceText = expectedFinanceSentence,
    TranslatedText = "完成本科学业后，我满怀热忱，立志在金融领域追求更高的学术成就。"
};
if (correlatedSource.Sequence != correlatedFinal.Sequence || correlatedFinal.Sequence != 42)
    throw new InvalidOperationException("Subtitle revision correlation smoke test failed.");

const string asrPrefix = "http://127.0.0.1:18992/";
using (var asrListener = new HttpListener())
{
    asrListener.Prefixes.Add(asrPrefix);
    asrListener.Start();
    var asrServerTask = Task.Run(async () =>
    {
        for (var requestIndex = 0; requestIndex < 2; requestIndex++)
        {
            var context = await asrListener.GetContextAsync();
            var requestPath = context.Request.Url?.AbsolutePath;
            byte[] bytes;
            if (requestPath == "/health" &&
                string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                bytes = Encoding.UTF8.GetBytes("{\"status\":\"ok\",\"models_loaded\":[\"sensevoice\"]}");
            }
            else if (requestPath == "/v1/audio/transcriptions" &&
                     string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                bytes = Encoding.UTF8.GetBytes("{\"text\":\"hello\"}");
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unexpected ASR request: {context.Request.HttpMethod} {requestPath}");
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    });

    var asrHealthy = await VideoSubtitleService.ProbeSenseVoiceEndpointAsync($"{asrPrefix}v1");
    if (!asrHealthy)
        throw new InvalidOperationException("SenseVoice health endpoint probe failed.");

    var asrResult = await VideoSubtitleService.TestSenseVoiceEndpointAsync(
        $"{asrPrefix}v1",
        "fun-asr-nano");
    await asrServerTask;
    if (asrResult != "hello")
        throw new InvalidOperationException("OpenAI-compatible ASR endpoint smoke test failed.");
}

var liveAsrUrl = Environment.GetEnvironmentVariable("LOCALTRANSLATOR_LIVE_ASR_URL");
if (!string.IsNullOrWhiteSpace(liveAsrUrl))
{
    if (!await VideoSubtitleService.ProbeSenseVoiceEndpointAsync(liveAsrUrl))
        throw new InvalidOperationException($"Live SenseVoice endpoint probe failed: {liveAsrUrl}");
    Console.WriteLine($"LIVE_ASR_READY={liveAsrUrl}");
}

var translationWindow = new TranslationWindowManager();
translationWindow.UpdateStream("Hello");
translationWindow.UpdateStream("Hello world");
if (translationWindow.ActiveStream != "Hello world")
    throw new InvalidOperationException("Accumulated ASR stream update failed.");
translationWindow.ReplaceStream("Hello corrected world");
if (translationWindow.ActiveStream != "Hello corrected world")
    throw new InvalidOperationException("Corrected ASR revision was appended instead of replacing the active stream.");
translationWindow.FinalizeSentence("Hello corrected world.");
translationWindow.FinalizeSentence("Second sentence.");
translationWindow.FinalizeSentence("Third sentence.");
translationWindow.FinalizeSentence("Fourth sentence.");
translationWindow.UpdateStream("Current fragment");
if (translationWindow.HistoricalContext.Contains("Hello corrected world.", StringComparison.Ordinal) ||
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
