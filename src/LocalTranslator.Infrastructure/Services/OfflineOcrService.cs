using System.Diagnostics;
using LocalTranslator.Core.Abstractions;
using LocalTranslator.Core.Exceptions;
using LocalTranslator.Core.Models;
using LocalTranslator.Infrastructure.Configuration;
using Microsoft.ML.OnnxRuntime;
using RapidOcrNet;
using SkiaSharp;
using CoreOcrResult = LocalTranslator.Core.Models.OcrResult;

namespace LocalTranslator.Infrastructure.Services;

public sealed class OfflineOcrService : IOcrService, IDisposable
{
    private readonly AppOptions _options;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private RapidOcr? _engine;
    private bool _disposed;

    public OfflineOcrService(AppOptions options, IAppLogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<CoreOcrResult> RecognizeAsync(
        OcrRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.PngImage.Length == 0)
        {
            throw new ArgumentException("图片数据为空。", nameof(request));
        }

        await _inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => RecognizeCore(request.PngImage, request.Language, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    private CoreOcrResult RecognizeCore(
        byte[] pngImage,
        SupportedLanguage language,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureEngineInitialized();

        using var bitmap = SKBitmap.Decode(pngImage);
        if (bitmap is null)
        {
            throw new OfflineEngineException("无法解码截图，请确认图片为有效的 PNG、JPEG 或 BMP 数据。");
        }

        var stopwatch = Stopwatch.StartNew();
        var nativeResult = _engine!.Detect(
            bitmap,
            RapidOcrOptions.Default with
            {
                DoAngle = false,
                TextScore = 0.45f
            });

        var usedEnhancedFallback = false;
        if (string.IsNullOrWhiteSpace(nativeResult.StrRes))
        {
            using var enhancedBitmap = EnhanceForSmallText(bitmap);
            nativeResult = _engine.Detect(
                enhancedBitmap,
                RapidOcrOptions.Default with
                {
                    ImgResize = 1600,
                    MaxSideLen = 2400,
                    BoxScoreThresh = 0.3f,
                    BoxThresh = 0.2f,
                    TextScore = 0.3f,
                    UnClipRatio = 1.8f,
                    DoAngle = false
                });
            usedEnhancedFallback = true;
        }
        stopwatch.Stop();

        var text = nativeResult.StrRes.Trim();
        _logger.Info(
            $"Offline OCR completed. Language={language.ToCode()}, " +
            $"Size={bitmap.Width}x{bitmap.Height}, Lines={nativeResult.TextBlocks.Length}, " +
            $"EnhancedFallback={usedEnhancedFallback}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");

        return new CoreOcrResult(text, stopwatch.Elapsed);
    }

    private static SKBitmap EnhanceForSmallText(SKBitmap source)
    {
        const int maximumSide = 2400;
        var scale = Math.Min(2f, maximumSide / (float)Math.Max(source.Width, source.Height));
        scale = Math.Max(1f, scale);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var enhanced = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));

        using var canvas = new SKCanvas(enhanced);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            ColorFilter = SKColorFilter.CreateColorMatrix(
            [
                1.25f, 0, 0, 0, -24,
                0, 1.25f, 0, 0, -24,
                0, 0, 1.25f, 0, -24,
                0, 0, 0, 1, 0
            ])
        };
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(source, new SKRect(0, 0, width, height), paint);
        canvas.Flush();
        return enhanced;
    }

    private void EnsureEngineInitialized()
    {
        if (_engine is not null)
        {
            return;
        }

        var detectionModel = ResolveModelPath(_options.Ocr.DetectionModel, "OCR 检测模型");
        var classificationModel = ResolveModelPath(_options.Ocr.ClassificationModel, "OCR 方向分类模型");
        var recognitionModel = ResolveModelPath(_options.Ocr.RecognitionModel, "OCR 识别模型");
        var characterDictionary = ResolveModelPath(_options.Ocr.CharacterDictionary, "OCR 字符字典");

        var engine = new RapidOcr();
        try
        {
            using var sessionOptions = RapidOcr.GetDefaultSessionOptions();
            sessionOptions.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
            engine.InitModels(
                detectionModel,
                classificationModel,
                recognitionModel,
                characterDictionary,
                sessionOptions);
            _engine = engine;
            _logger.Info("PP-OCRv5 Mobile models loaded successfully for Chinese, English and Japanese.");
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    private string ResolveModelPath(string relativePath, string displayName)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new OfflineEngineException($"未配置{displayName}路径。");
        }

        var modelsRoot = Path.IsPathRooted(_options.ModelsRoot)
            ? _options.ModelsRoot
            : Path.Combine(AppContext.BaseDirectory, _options.ModelsRoot);
        var modelPath = Path.GetFullPath(Path.Combine(modelsRoot, relativePath));

        if (!File.Exists(modelPath))
        {
            throw new OfflineEngineException($"{displayName}尚未安装：{modelPath}");
        }

        return modelPath;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine?.Dispose();
        _inferenceLock.Dispose();
    }
}
