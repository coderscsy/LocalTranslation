using System.ComponentModel;
using System.Runtime.CompilerServices;
using LocalTranslator.Core.Abstractions;
using LocalTranslator.Core.Exceptions;
using LocalTranslator.Core.Models;

namespace LocalTranslator.App;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly TimeSpan RealtimeDelay = TimeSpan.FromMilliseconds(650);

    private readonly ITranslationService _translationService;
    private readonly IOcrService _ocrService;
    private readonly IAppLogger _logger;
    private CancellationTokenSource? _realtimeCancellation;
    private LanguageItem _sourceLanguage;
    private LanguageItem _targetLanguage;
    private string _sourceText = string.Empty;
    private string _translatedText = string.Empty;
    private string _statusMessage = "离线 OCR 已就绪；请选择本地模型文件、本地服务或在线翻译服务。";
    private bool _isBusy;
    private bool _isRealtimeEnabled = true;

    public MainViewModel(
        ITranslationService translationService,
        IOcrService ocrService,
        IAppLogger logger)
    {
        _translationService = translationService;
        _ocrService = ocrService;
        _logger = logger;
        _sourceLanguage = SourceLanguages.First(item => item.Value == SupportedLanguage.AutoDetect);
        _targetLanguage = TargetLanguages.First(item => item.Value == SupportedLanguage.ChineseSimplified);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<LanguageItem> SourceLanguages { get; } = LanguageItem.All;

    public IReadOnlyList<LanguageItem> TargetLanguages { get; } = LanguageItem.Targets;

    public LanguageItem SourceLanguage
    {
        get => _sourceLanguage;
        set
        {
            if (SetField(ref _sourceLanguage, value))
            {
                ScheduleRealtimeTranslation();
            }
        }
    }

    public LanguageItem TargetLanguage
    {
        get => _targetLanguage;
        set
        {
            if (SetField(ref _targetLanguage, value))
            {
                ScheduleRealtimeTranslation();
            }
        }
    }

    public string SourceText
    {
        get => _sourceText;
        set
        {
            if (!SetField(ref _sourceText, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SourceCharacterCount));
            OnPropertyChanged(nameof(CanTranslate));

            if (string.IsNullOrWhiteSpace(value))
            {
                CancelRealtimeTranslation();
                TranslatedText = string.Empty;
                StatusMessage = "等待输入内容。";
                return;
            }

            ScheduleRealtimeTranslation();
        }
    }

    public int SourceCharacterCount => SourceText.Length;

    public string RealtimeModeText => IsRealtimeEnabled ? "实时翻译已开启" : "等待手动翻译";

    public string TranslatedText
    {
        get => _translatedText;
        private set => SetField(ref _translatedText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanTranslate));
            }
        }
    }

    public bool IsRealtimeEnabled
    {
        get => _isRealtimeEnabled;
        set
        {
            if (!SetField(ref _isRealtimeEnabled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(RealtimeModeText));

            if (value)
            {
                StatusMessage = "实时翻译已开启。";
                ScheduleRealtimeTranslation();
            }
            else
            {
                CancelRealtimeTranslation();
                StatusMessage = "实时翻译已关闭，可按 Ctrl + Enter 手动翻译。";
            }
        }
    }

    public bool CanTranslate => !IsBusy && !string.IsNullOrWhiteSpace(SourceText);

    public Task TranslateTextAsync()
    {
        CancelRealtimeTranslation();
        return TranslateCurrentTextAsync(CancellationToken.None);
    }

    public async Task TranslateScreenshotAsync(byte[] pngImage)
    {
        CancelRealtimeTranslation();
        await RunBusyAsync(async () =>
        {
            StatusMessage = "正在执行本地 OCR…";
            var ocrResult = await _ocrService.RecognizeAsync(
                new OcrRequest(pngImage, SourceLanguage.Value));

            SourceText = ocrResult.Text;
            CancelRealtimeTranslation();

            if (string.IsNullOrWhiteSpace(ocrResult.Text))
            {
                TranslatedText = string.Empty;
                StatusMessage = $"OCR 已完成，但未识别到文字（{ocrResult.Elapsed.TotalMilliseconds:F0} ms）。";
                return;
            }

            StatusMessage = $"OCR 已识别，正在本地翻译…（OCR {ocrResult.Elapsed.TotalMilliseconds:F0} ms）";
            var detectedSource = ResolveSourceLanguage(ocrResult.Text);
            var translationResult = await _translationService.TranslateAsync(
                new TranslationRequest(
                    ocrResult.Text,
                    detectedSource,
                    TargetLanguage.Value));

            TranslatedText = translationResult.Text;
            StatusMessage = $"截图翻译完成：OCR {ocrResult.Elapsed.TotalMilliseconds:F0} ms，翻译 {translationResult.Elapsed.TotalMilliseconds:F0} ms。";
        }, CancellationToken.None);
    }

    public void SwapLanguages()
    {
        CancelRealtimeTranslation();
        var oldSource = SourceLanguage.Value == SupportedLanguage.AutoDetect
            ? TextLanguageDetector.Detect(SourceText) ?? SupportedLanguage.English
            : SourceLanguage.Value;
        SourceLanguage = SourceLanguages.First(item => item.Value == TargetLanguage.Value);
        TargetLanguage = TargetLanguages.First(item => item.Value == oldSource);
        (SourceText, TranslatedText) = (TranslatedText, SourceText);
        SetStatus("已交换源语言和目标语言。", isError: false);
        ScheduleRealtimeTranslation();
    }

    public void ClearText()
    {
        CancelRealtimeTranslation();
        SourceText = string.Empty;
        TranslatedText = string.Empty;
        StatusMessage = "内容已清空。";
    }

    public void SetSourceText(string text)
    {
        SourceText = text;
        StatusMessage = string.IsNullOrWhiteSpace(text) ? "剪贴板中没有文字。" : "已从剪贴板粘贴。";
    }

    public void SetStatus(string message, bool isError)
    {
        StatusMessage = message;
        if (isError)
        {
            _logger.Info($"User-visible error: {message}");
        }
    }

    private async Task TranslateCurrentTextAsync(CancellationToken cancellationToken)
    {
        if (!CanTranslate)
        {
            return;
        }

        var sourceSnapshot = SourceText;
        await RunBusyAsync(async () =>
        {
            StatusMessage = "正在本地翻译…";
            var resolvedSource = ResolveSourceLanguage(sourceSnapshot);
            var result = await _translationService.TranslateAsync(new TranslationRequest(
                sourceSnapshot,
                resolvedSource,
                TargetLanguage.Value), cancellationToken);

            if (SourceText == sourceSnapshot)
            {
                TranslatedText = result.Text;
                StatusMessage = $"翻译完成，用时 {result.Elapsed.TotalMilliseconds:F0} ms。";
            }
        }, cancellationToken);
    }

    private SupportedLanguage ResolveSourceLanguage(string text)
    {
        var detected = TextLanguageDetector.Detect(text)
            ?? throw new OfflineEngineException("无法判断源语言，请输入包含中文、英文或日文的文本。");
        if (SourceLanguage.Value == SupportedLanguage.AutoDetect)
        {
            return detected;
        }

        if (detected != SourceLanguage.Value)
        {
            throw new OfflineEngineException(
                $"检测到输入为{detected.ToDisplayName()}，与源语言{SourceLanguage.DisplayName}不一致。请选择“自动检测”或切换源语言。");
        }
        return SourceLanguage.Value;
    }

    private void ScheduleRealtimeTranslation()
    {
        CancelRealtimeTranslation();
        if (!IsRealtimeEnabled || string.IsNullOrWhiteSpace(SourceText))
        {
            return;
        }

        _realtimeCancellation = new CancellationTokenSource();
        _ = TranslateAfterDelayAsync(_realtimeCancellation.Token);
    }

    private async Task TranslateAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(RealtimeDelay, cancellationToken);
            await TranslateCurrentTextAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A newer input superseded this request.
        }
    }

    private void CancelRealtimeTranslation()
    {
        _realtimeCancellation?.Cancel();
        _realtimeCancellation?.Dispose();
        _realtimeCancellation = null;
    }

    private async Task RunBusyAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        IsBusy = true;

        try
        {
            await action();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when realtime input changes before translation completes.
        }
        catch (OfflineEngineException exception)
        {
            _logger.Info(exception.Message);
            StatusMessage = exception.Message;
        }
        catch (Exception exception)
        {
            _logger.Error("Operation failed.", exception);
            StatusMessage = $"操作失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
