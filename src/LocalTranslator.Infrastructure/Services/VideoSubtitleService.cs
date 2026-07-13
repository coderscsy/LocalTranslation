using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;
using LocalTranslator.Core.Abstractions;
using LocalTranslator.Core.Exceptions;
using LocalTranslator.Core.Models;
using LocalTranslator.Core.Services;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.Utils;
using System.Text.RegularExpressions;
using Whisper.net;

namespace LocalTranslator.Infrastructure.Services;

public sealed class VideoSubtitleService(ITranslationService translationService, IAppLogger logger) : IAsyncDisposable
{
    private const double MinimumChunkSeconds = 0.72;
    private const double MaximumChunkSeconds = 1.6;
    private const double TailSilenceSeconds = 0.28;
    private const double OverlapSeconds = 0.16;
    private static readonly WaveFormat WhisperWaveFormat = new(16000, 16, 1);
    private static readonly Regex SubtitleArtifactPattern = new(
        @"^\s*[\(\[（【<].{0,100}(?:字幕|subtitles?|captions?|translated\s+by|translation\s+by|synced\s+by|www\.).{0,100}[\)\]）】>]\s*[。.!！]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private Channel<AudioChunk> _chunks = CreateAudioChannel();
    private Channel<RecognizedSegment> _translations = CreateTranslationChannel(6);
    private readonly List<byte> _pcmBuffer = [];
    private readonly object _bufferLock = new();
    private WasapiLoopbackCapture? _capture;
    private BufferedWaveProvider? _captureBuffer;
    private IWaveProvider? _whisperWaveProvider;
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private Task[] _translationWorkers = [];
    private WhisperFactory? _factory;
    private readonly HttpClient _asrHttpClient = new() { Timeout = TimeSpan.FromSeconds(90) };
    private double _resampleByteRemainder;
    private TimeSpan _nextChunkStart;
    private SupportedLanguage _source;
    private SupportedLanguage _target;
    private SpeechRecognitionEngine _recognitionEngine = SpeechRecognitionEngine.WhisperGgml;
    private Uri? _senseVoiceEndpoint;
    private long _recognizedVersion;
    private PendingUtterance? _pendingUtterance;
    private readonly TranslationWindowManager _translationWindow = new();

    public event EventHandler<SubtitleSegment>? SegmentReady;
    public event EventHandler<SubtitleSegment>? SourceSegmentReady;
    public event EventHandler<string>? StatusChanged;

    public bool IsRunning => _capture is not null;

    public void SetTargetLanguage(SupportedLanguage target)
    {
        _target = target;
        _translationWindow.Reset();
        StatusChanged?.Invoke(this, $"后续字幕目标语言已切换为：{target.ToDisplayName()}。");
    }

    public Task StartAsync(
        SpeechRecognitionEngine recognitionEngine,
        string whisperModelPath,
        string senseVoiceBaseUrl,
        SupportedLanguage source,
        SupportedLanguage target,
        int translationConcurrency = 3,
        CancellationToken cancellationToken = default)
    {
        if (IsRunning) throw new InvalidOperationException("视频字幕已经在运行。");
        _recognitionEngine = recognitionEngine;
        if (_recognitionEngine == SpeechRecognitionEngine.WhisperGgml && !File.Exists(whisperModelPath))
            throw new OfflineEngineException("请选择存在的 Whisper GGML 模型文件。");
        if (_recognitionEngine == SpeechRecognitionEngine.SenseVoiceSmall)
            _senseVoiceEndpoint = BuildTranscriptionEndpoint(senseVoiceBaseUrl);
        else
            _senseVoiceEndpoint = null;
        _source = source;
        _target = target;
        _chunks = CreateAudioChannel();
        translationConcurrency = Math.Clamp(translationConcurrency, 1, 4);
        _translations = CreateTranslationChannel(translationConcurrency * 2);
        _nextChunkStart = TimeSpan.Zero;
        _recognizedVersion = 0;
        _pendingUtterance = null;
        _translationWindow.Reset();
        _factory = _recognitionEngine == SpeechRecognitionEngine.WhisperGgml
            ? WhisperFactory.FromPath(whisperModelPath)
            : null;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Loopback audio must be captured in the Windows device mix format (usually
        // 48 kHz IEEE-float stereo). Treating those bytes as 16 kHz PCM16 corrupts
        // speech and is especially destructive for Mandarin recognition.
        _capture = new WasapiLoopbackCapture();
        ConfigureResampler(_capture.WaveFormat);
        _capture.DataAvailable += CaptureOnDataAvailable;
        _capture.RecordingStopped += CaptureOnRecordingStopped;
        _worker = ProcessChunksAsync(_cancellation.Token);
        _translationWorkers = Enumerable.Range(1, translationConcurrency)
            .Select(workerId => ProcessTranslationsAsync(workerId, _cancellation.Token))
            .ToArray();
        _capture.StartRecording();
        logger.Info($"Loopback capture started. DeviceFormat={_capture.WaveFormat}; WhisperFormat={WhisperWaveFormat}.");
        StatusChanged?.Invoke(this, _recognitionEngine == SpeechRecognitionEngine.SenseVoiceSmall
            ? $"SenseVoice Small 识别中：本地 ASR 服务负责转写，最长 {MaximumChunkSeconds:F1} 秒切片。"
            : $"Whisper 识别中：静音触发切片，最长 {MaximumChunkSeconds:F1} 秒送识别，译文晚到也会回填到字幕。");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var capture = _capture;
        if (capture is null && _cancellation is null) return;
        StatusChanged?.Invoke(this, "正在停止翻译…");
        _capture = null;
        _cancellation?.Cancel();
        try
        {
            capture?.StopRecording();
        }
        catch (Exception exception)
        {
            logger.Error("Stopping loopback capture failed.", exception);
        }
        _chunks.Writer.TryComplete();
        _translations.Writer.TryComplete();
        await AwaitStopAsync(_worker, "speech worker").ConfigureAwait(false);
        if (_translationWorkers.Length > 0)
            await AwaitStopAsync(Task.WhenAll(_translationWorkers), "translation workers").ConfigureAwait(false);
        _translationWorkers = [];
        capture?.Dispose();
        _captureBuffer = null;
        _whisperWaveProvider = null;
        _resampleByteRemainder = 0;
        _factory?.Dispose();
        _factory = null;
        _cancellation?.Dispose();
        _cancellation = null;
        StatusChanged?.Invoke(this, "视频字幕已停止。");
    }

    private async Task AwaitStopAsync(Task? task, string name)
    {
        if (task is null) return;
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during a user initiated stop.
        }
        catch (TimeoutException)
        {
            logger.Info($"Timed out while stopping {name}; background cancellation was already requested.");
        }
        catch (Exception exception)
        {
            logger.Error($"Stopping {name} failed.", exception);
        }
    }

    private void CaptureOnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var input = _captureBuffer;
        var output = _whisperWaveProvider;
        var capture = _capture;
        if (input is null || output is null || capture is null || e.BytesRecorded <= 0) return;

        input.AddSamples(e.Buffer, 0, e.BytesRecorded);
        var exactOutputBytes = e.BytesRecorded *
            (double)WhisperWaveFormat.AverageBytesPerSecond /
            capture.WaveFormat.AverageBytesPerSecond + _resampleByteRemainder;
        var requestedBytes = Math.Max(2, (int)exactOutputBytes & ~1);
        _resampleByteRemainder = exactOutputBytes - requestedBytes;
        var converted = new byte[requestedBytes];
        var bytesRead = output.Read(converted, 0, converted.Length);
        if (bytesRead <= 0) return;

        lock (_bufferLock)
        {
            _pcmBuffer.AddRange(converted.AsSpan(0, bytesRead).ToArray());
            var bufferedBytes = _pcmBuffer.Count;
            var maxBytes = WhisperWaveFormat.AverageBytesPerSecond * MaximumChunkSeconds;
            var minBytes = WhisperWaveFormat.AverageBytesPerSecond * MinimumChunkSeconds;
            var hasTailSilence = bufferedBytes >= minBytes && IsTailSilent(_pcmBuffer, TailSilenceSeconds);
            if (bufferedBytes >= maxBytes || hasTailSilence)
            {
                FlushChunkLocked(hasTailSilence);
            }
        }
    }

    private void ConfigureResampler(WaveFormat deviceFormat)
    {
        if (deviceFormat.Channels is < 1 or > 2)
            throw new OfflineEngineException($"当前输出设备为 {deviceFormat.Channels} 声道，暂不支持。请在 Windows 中切换为单声道或立体声输出。");

        _captureBuffer = new BufferedWaveProvider(deviceFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(1),
            DiscardOnBufferOverflow = true,
            ReadFully = false
        };
        ISampleProvider samples = _captureBuffer.ToSampleProvider();
        if (deviceFormat.Channels == 2)
        {
            samples = new StereoToMonoSampleProvider(samples)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f
            };
        }

        samples = new WdlResamplingSampleProvider(samples, WhisperWaveFormat.SampleRate);
        _whisperWaveProvider = new SampleToWaveProvider16(samples);
    }

    private void FlushChunk()
    {
        lock (_bufferLock) FlushChunkLocked();
    }

    private void FlushChunkLocked(bool isSpeechBoundary = false)
    {
        if (_pcmBuffer.Count < WhisperWaveFormat.AverageBytesPerSecond) return;
        var pcm = _pcmBuffer.ToArray();
        _pcmBuffer.Clear();
        var duration = TimeSpan.FromSeconds((double)pcm.Length / WhisperWaveFormat.AverageBytesPerSecond);
        var chunk = new AudioChunk(pcm, _nextChunkStart, duration, isSpeechBoundary);
        var overlapBytes = Math.Min(
            (int)(WhisperWaveFormat.AverageBytesPerSecond * OverlapSeconds) & ~1,
            Math.Max(0, pcm.Length - WhisperWaveFormat.AverageBytesPerSecond));
        if (overlapBytes > 0)
            _pcmBuffer.AddRange(pcm.AsSpan(pcm.Length - overlapBytes, overlapBytes).ToArray());
        _nextChunkStart += TimeSpan.FromSeconds(
            (double)(pcm.Length - overlapBytes) / WhisperWaveFormat.AverageBytesPerSecond);
        if (IsSilent(pcm)) return;
        _chunks.Writer.TryWrite(chunk);
    }

    private async Task ProcessChunksAsync(CancellationToken cancellationToken)
    {
        if (_recognitionEngine == SpeechRecognitionEngine.SenseVoiceSmall)
        {
            await ProcessSenseVoiceChunksAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await ProcessWhisperChunksAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessWhisperChunksAsync(CancellationToken cancellationToken)
    {
        var builder = _factory!.CreateBuilder()
            .WithLanguage(_source switch
            {
                SupportedLanguage.AutoDetect => "auto",
                SupportedLanguage.ChineseSimplified => "zh",
                _ => _source.ToCode()
            })
            .WithThreads(Math.Clamp(Environment.ProcessorCount - 1, 2, 8))
            .WithTemperature(0)
            .WithSingleSegment()
            .WithMaxTokensPerSegment(64)
            .WithNoSpeechThreshold(0.72f)
            .WithGreedySamplingStrategy(options => options.WithBestOf(1))
            .WithProbabilities();
        using var processor = builder.Build();

        await foreach (var chunk in _chunks.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                StatusChanged?.Invoke(this, "正在识别人物对话…");
                var stopwatch = Stopwatch.StartNew();
                using var wav = CreateWaveStream(chunk.Pcm);
                await foreach (var result in processor.ProcessAsync(wav, cancellationToken).ConfigureAwait(false))
                {
                    var sourceText = result.Text.Trim();
                    if (string.IsNullOrWhiteSpace(sourceText)) continue;
                    if (IsSubtitleArtifact(sourceText))
                    {
                        logger.Info("Subtitle credit/caption hallucination ignored before translation.");
                        continue;
                    }
                    if (result.NoSpeechProbability > 0.82f || result.Probability < 0.08f)
                    {
                        logger.Info($"Low confidence subtitle ignored. Probability={result.Probability:F2}, NoSpeech={result.NoSpeechProbability:F2}.");
                        continue;
                    }
                    var resolvedSource = _source == SupportedLanguage.AutoDetect
                        ? TextLanguageDetector.Detect(sourceText) ?? SupportedLanguage.English
                        : _source;
                    var displayedSource = _source == SupportedLanguage.ChineseSimplified
                        ? ChineseTextNormalizer.ToSimplified(sourceText)
                        : sourceText;
                    var start = chunk.Start + result.Start;
                    var end = result.End > result.Start ? chunk.Start + result.End : chunk.Start + chunk.Duration;
                    AppendRecognizedText(sourceText, displayedSource, resolvedSource, start, end);
                    logger.Info($"Video speech recognized. Start={start}, SourceChars={sourceText.Length}.");
                }
                if (chunk.IsSpeechBoundary) FlushPendingUtterance();
                stopwatch.Stop();
                StatusChanged?.Invoke(this, $"监听中 · 本段处理 {stopwatch.Elapsed.TotalSeconds:F1} 秒 · 等待下一段对话…");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.Error("Video subtitle chunk failed.", exception);
                StatusChanged?.Invoke(this, $"本段识别失败：{exception.Message}");
            }
        }
    }

    private async Task ProcessSenseVoiceChunksAsync(CancellationToken cancellationToken)
    {
        await foreach (var chunk in _chunks.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                StatusChanged?.Invoke(this, "正在调用 SenseVoice Small 识别人物对话…");
                var stopwatch = Stopwatch.StartNew();
                var sourceText = await TranscribeWithSenseVoiceAsync(chunk.Pcm, cancellationToken)
                    .ConfigureAwait(false);
                sourceText = sourceText.Trim();
                if (!string.IsNullOrWhiteSpace(sourceText) &&
                    !IsSubtitleArtifact(sourceText))
                {
                    var resolvedSource = _source == SupportedLanguage.AutoDetect
                        ? TextLanguageDetector.Detect(sourceText) ?? SupportedLanguage.English
                        : _source;
                    var displayedSource = _source == SupportedLanguage.ChineseSimplified
                        ? ChineseTextNormalizer.ToSimplified(sourceText)
                        : sourceText;
                    AppendRecognizedText(sourceText, displayedSource, resolvedSource,
                        chunk.Start, chunk.Start + chunk.Duration);
                    logger.Info($"SenseVoice speech recognized. Start={chunk.Start}, SourceChars={sourceText.Length}.");
                }

                if (chunk.IsSpeechBoundary) FlushPendingUtterance();
                stopwatch.Stop();
                StatusChanged?.Invoke(this,
                    $"监听中 · SenseVoice 本段处理 {stopwatch.Elapsed.TotalSeconds:F1} 秒 · 等待下一段对话…");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.Error("SenseVoice subtitle chunk failed.", exception);
                StatusChanged?.Invoke(this, $"SenseVoice 识别失败：{exception.Message}");
            }
        }
    }

    private async Task<string> TranscribeWithSenseVoiceAsync(byte[] pcm, CancellationToken cancellationToken)
    {
        if (_senseVoiceEndpoint is null)
            throw new OfflineEngineException("SenseVoice 服务地址未配置。");

        await using var wav = CreateWaveStream(pcm);
        using var content = new MultipartFormDataContent();
        using var file = new StreamContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(file, "file", "subtitle.wav");
        content.Add(new StringContent("SenseVoiceSmall"), "model");
        if (_source != SupportedLanguage.AutoDetect)
            content.Add(new StringContent(_source.ToCode()), "language");

        using var response = await _asrHttpClient.PostAsync(
            _senseVoiceEndpoint, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new OfflineEngineException($"SenseVoice 服务返回 HTTP {(int)response.StatusCode}：{body}");
        return ExtractTranscriptionText(body);
    }

    private static Uri BuildTranscriptionEndpoint(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new OfflineEngineException(
                "SenseVoice Small 需要填写本地 FunASR/OpenAI-compatible ASR 地址，例如 http://127.0.0.1:10095/v1。");
        var value = baseUrl.Trim().TrimEnd('/');
        if (value.EndsWith("/audio/transcriptions", StringComparison.OrdinalIgnoreCase))
            return new Uri(value);
        if (value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return new Uri($"{value}/audio/transcriptions");
        return new Uri($"{value}/v1/audio/transcriptions");
    }

    private static string ExtractTranscriptionText(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                return text.GetString() ?? string.Empty;
            if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String)
                return result.GetString() ?? string.Empty;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                return string.Join(' ', data.EnumerateArray()
                    .Select(item =>
                        item.TryGetProperty("text", out var itemText) && itemText.ValueKind == JsonValueKind.String
                            ? itemText.GetString()
                            : null)
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            }
        }

        throw new OfflineEngineException("SenseVoice 服务返回格式无法识别：缺少 text/result/data[].text。");
    }

    private void AppendRecognizedText(
        string sourceText,
        string displayedSource,
        SupportedLanguage sourceLanguage,
        TimeSpan start,
        TimeSpan end)
    {
        if (_pendingUtterance is not null)
        {
            var gap = start - _pendingUtterance.End;
            if (_pendingUtterance.SourceLanguage != sourceLanguage || gap > TimeSpan.FromSeconds(0.9))
                FlushPendingUtterance();
        }

        if (_pendingUtterance is null)
        {
            _pendingUtterance = new PendingUtterance
            {
                Start = start,
                End = end,
                SourceText = SemanticSubtitleBuffer.Normalize(sourceText),
                DisplayedSource = SemanticSubtitleBuffer.Normalize(displayedSource),
                SourceLanguage = sourceLanguage
            };
        }
        else
        {
            _pendingUtterance.End = end > _pendingUtterance.End ? end : _pendingUtterance.End;
            _pendingUtterance.SourceText = SemanticSubtitleBuffer.JoinFragments(
                _pendingUtterance.SourceText, sourceText, sourceLanguage);
            _pendingUtterance.DisplayedSource = SemanticSubtitleBuffer.JoinFragments(
                _pendingUtterance.DisplayedSource, displayedSource, sourceLanguage);
        }

        _translationWindow.UpdateStream(_pendingUtterance.SourceText);
        SourceSegmentReady?.Invoke(this, new SubtitleSegment(
            _pendingUtterance.Start,
            _pendingUtterance.End,
            _pendingUtterance.DisplayedSource,
            string.Empty));

        if (ShouldFlushPending(_pendingUtterance))
            FlushPendingUtterance();
    }

    private void FlushPendingUtterance()
    {
        var pending = _pendingUtterance;
        if (pending is null) return;
        _pendingUtterance = null;

        var sourceText = pending.SourceText.Trim();
        var displayedSource = pending.DisplayedSource.Trim();
        if (string.IsNullOrWhiteSpace(sourceText)) return;

        var version = Interlocked.Increment(ref _recognizedVersion);
        _translationWindow.UpdateStream(sourceText);
        SourceSegmentReady?.Invoke(this, new SubtitleSegment(
            pending.Start, pending.End, displayedSource, string.Empty));

        if (pending.SourceLanguage == _target)
        {
            SegmentReady?.Invoke(this, new SubtitleSegment(
                pending.Start,
                pending.End,
                displayedSource,
                NormalizeForTarget(sourceText, _target)));
        }
        else
        {
            var context = _translationWindow.HistoricalContext;
            _translations.Writer.TryWrite(new RecognizedSegment(
                version,
                pending.Start,
                pending.End,
                sourceText,
                displayedSource,
                pending.SourceLanguage,
                context));
        }

        _translationWindow.FinalizeSentence(sourceText);
        logger.Info($"Video utterance committed. Start={pending.Start}, SourceChars={sourceText.Length}.");
    }

    private static bool ShouldFlushPending(PendingUtterance pending) =>
        SemanticSubtitleBuffer.ShouldFlush(pending.SourceText, pending.End - pending.Start);

    private async Task ProcessTranslationsAsync(int workerId, CancellationToken cancellationToken)
    {
        await foreach (var item in _translations.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                var request = new TranslationRequest(
                    item.SourceText,
                    item.SourceLanguage,
                    _target,
                    item.Context,
                    MaxOutputTokens: EstimateSubtitleTokenLimit(item.SourceText));
                var translated = (await translationService.TranslateAsync(
                    request, cancellationToken).ConfigureAwait(false)).Text;
                if (!TranslationOutputValidator.IsValid(item.SourceText, translated, _target))
                {
                    logger.Info(
                        $"Translation output rejected and retried. Worker={workerId}, " +
                        $"Direction={item.SourceLanguage.ToCode()}->{_target.ToCode()}.");
                    translated = (await translationService.TranslateAsync(
                        request with { RequireTargetLanguage = true }, cancellationToken)
                        .ConfigureAwait(false)).Text;
                }

                translated = NormalizeForTarget(translated, _target);
                if (!TranslationOutputValidator.IsValid(item.SourceText, translated, _target))
                    throw new OfflineEngineException("翻译模型连续返回了原文或错误语言，本句已阻止上屏。");
                SegmentReady?.Invoke(this, new SubtitleSegment(
                    item.Start, item.End, item.DisplayedSource, translated));
                logger.Info(
                    $"Video translation ready. Worker={workerId}, Start={item.Start}, SourceChars={item.SourceText.Length}.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.Error("Video subtitle translation failed.", exception);
                StatusChanged?.Invoke(this, $"语音已识别，但本句翻译失败：{exception.Message}");
            }
        }
    }

    private static Channel<AudioChunk> CreateAudioChannel() => Channel.CreateBounded<AudioChunk>(
        new BoundedChannelOptions(2)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private static Channel<RecognizedSegment> CreateTranslationChannel(int capacity) =>
        Channel.CreateBounded<RecognizedSegment>(new BoundedChannelOptions(Math.Max(2, capacity))
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private static string NormalizeForTarget(string text, SupportedLanguage target) =>
        target == SupportedLanguage.ChineseSimplified ? ChineseTextNormalizer.ToSimplified(text) : text;

    public static bool IsSubtitleArtifact(string text) =>
        !string.IsNullOrWhiteSpace(text) && SubtitleArtifactPattern.IsMatch(text.Trim());

    private static bool IsSilent(byte[] pcm)
    {
        if (pcm.Length < 2) return true;
        double sum = 0;
        var samples = pcm.Length / 2;
        for (var index = 0; index + 1 < pcm.Length; index += 2)
        {
            var sample = (short)(pcm[index] | pcm[index + 1] << 8);
            var normalized = sample / 32768d;
            sum += normalized * normalized;
        }
        return Math.Sqrt(sum / samples) < 0.006;
    }

    private static bool IsTailSilent(List<byte> pcm, double seconds)
    {
        var bytesToInspect = Math.Min(
            pcm.Count,
            (int)(WhisperWaveFormat.AverageBytesPerSecond * seconds) & ~1);
        if (bytesToInspect < 2) return false;
        double sum = 0;
        var samples = bytesToInspect / 2;
        var start = pcm.Count - bytesToInspect;
        for (var index = start; index + 1 < pcm.Count; index += 2)
        {
            var sample = (short)(pcm[index] | pcm[index + 1] << 8);
            var normalized = sample / 32768d;
            sum += normalized * normalized;
        }

        return Math.Sqrt(sum / samples) < 0.0048;
    }

    private MemoryStream CreateWaveStream(byte[] pcm)
    {
        var stream = new MemoryStream();
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(stream), WhisperWaveFormat)) writer.Write(pcm, 0, pcm.Length);
        stream.Position = 0;
        return stream;
    }

    private void CaptureOnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) StatusChanged?.Invoke(this, $"音频采集停止：{e.Exception.Message}");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _asrHttpClient.Dispose();
    }

    private sealed record AudioChunk(byte[] Pcm, TimeSpan Start, TimeSpan Duration, bool IsSpeechBoundary);
    private sealed class PendingUtterance
    {
        public TimeSpan Start { get; init; }
        public TimeSpan End { get; set; }
        public required string SourceText { get; set; }
        public required string DisplayedSource { get; set; }
        public required SupportedLanguage SourceLanguage { get; init; }
    }

    private sealed record RecognizedSegment(
        long Version,
        TimeSpan Start,
        TimeSpan End,
        string SourceText,
        string DisplayedSource,
        SupportedLanguage SourceLanguage,
        string Context);

    private static int EstimateSubtitleTokenLimit(string sourceText) =>
        Math.Clamp(sourceText.Length * 2 + 24, 32, 160);
}
