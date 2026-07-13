using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using LocalTranslator.Core.Abstractions;
using LocalTranslator.Core.Models;
using LocalTranslator.Infrastructure.Services;
using Microsoft.Win32;

namespace LocalTranslator.App;

public partial class VideoSubtitleWindow : Window
{
    private const string PreferredVideoModel = "gemma-4-26b-a4b-it-mlx";
    private const int CurrentOverlayLayoutVersion = 4;
    private readonly VideoSubtitleService _service;
    private readonly VideoTranslationSessionService _translationSession = new();
    private readonly TranslationProviderRouter _providerRouter;
    private readonly TranslationProviderSettings _providerSettings;
    private readonly SpeechModelManager _speechModelManager = new();
    private readonly string _settingsPath;
    private SubtitleOverlayWindow? _overlay;
    private bool _running;
    private bool _allowClose;
    private bool _settingsLoaded;
    private bool _applyingOverlayPlacement;
    private bool _syncingTargetLanguage;
    private string? _selectedVideoProviderId;
    private double? _overlayLeft;
    private double? _overlayTop;
    private double _overlayHeight = 150;
    private double _overlayOpacity = 0.66;
    private TimeSpan _latestOverlayStart = TimeSpan.MinValue;
    private CancellationTokenSource? _modelDownloadCancellation;

    public VideoSubtitleWindow(
        SecureTranslationProviderStore providerStore,
        TranslationProviderRouter providerRouter,
        IAppLogger logger)
    {
        InitializeComponent();
        _providerRouter = providerRouter;
        _providerSettings = providerStore.Load();
        VideoProviders = new ObservableCollection<VideoProviderChoice>(
            _providerSettings.OnlineProviders.Select(provider =>
                new VideoProviderChoice(provider.Id, provider.DisplayName)));
        _service = new VideoSubtitleService(_translationSession, logger);
        _service.SourceSegmentReady += ServiceOnSourceSegmentReady;
        _service.SegmentReady += ServiceOnSegmentReady;
        _service.StatusChanged += (_, status) => Dispatcher.Invoke(() => StatusText.Text = status);
        SourceLanguages = LanguageItem.All;
        TargetLanguages = LanguageItem.Targets;
        DataContext = this;
        SourceCombo.SelectedIndex = 0;
        TargetCombo.SelectedItem = TargetLanguages.First(item => item.Value == SupportedLanguage.ChineseSimplified);
        AsrEngineCombo.SelectedItem = AsrEngines[0];
        _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalTranslator", "video-subtitle-settings.json");
        LoadSettings();
        _settingsLoaded = true;
        SaveSettings();
        RefreshDefaultModelStatus();
        RefreshAsrEngineUi();
        Closing += VideoSubtitleWindow_Closing;
        Closed += (_, _) =>
        {
            _speechModelManager.Dispose();
            _translationSession.Dispose();
        };
    }

    public IReadOnlyList<LanguageItem> SourceLanguages { get; }
    public IReadOnlyList<LanguageItem> TargetLanguages { get; }
    public IReadOnlyList<AsrEngineChoice> AsrEngines { get; } =
    [
        new(SpeechRecognitionEngine.SenseVoiceSmall, "SenseVoice Small（推荐，需本地 FunASR 服务）"),
        new(SpeechRecognitionEngine.WhisperGgml, "Whisper GGML（内置 fallback）")
    ];
    public ObservableCollection<SubtitleRow> Segments { get; } = [];
    public ObservableCollection<VideoProviderChoice> VideoProviders { get; }
    public ObservableCollection<string> VideoModels { get; } = [];
    public IReadOnlyList<int> VideoConcurrencyOptions { get; } = [1, 2, 3, 4];
    public IReadOnlyList<VideoSceneChoice> VideoSceneOptions { get; } =
    [
        new("general", "通用视频/直播"),
        new("game", "游戏/电竞")
    ];

    public bool IsRunning => _running || _service.IsRunning;

    public async Task ShutdownAsync()
    {
        _allowClose = true;
        await StopAsync();
        Close();
    }

    private void BrowseModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Whisper GGML 模型 (*.bin)|*.bin|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true) ModelPathBox.Text = dialog.FileName;
    }

    private void AsrEngine_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        RefreshAsrEngineUi();
        if (_settingsLoaded) SaveSettings();
    }

    private void AsrEngineAvailability_Changed(object sender, RoutedEventArgs e)
    {
        RefreshAsrEngineUi();
        if (_settingsLoaded) SaveSettings();
    }

    private void RefreshAsrEngineUi()
    {
        var engine = (AsrEngineCombo.SelectedItem as AsrEngineChoice)?.Engine
                     ?? SpeechRecognitionEngine.SenseVoiceSmall;
        if (!IsAsrEngineEnabled(engine))
        {
            var fallback = EnableSenseVoiceCheck.IsChecked == true
                ? SpeechRecognitionEngine.SenseVoiceSmall
                : EnableWhisperCheck.IsChecked == true
                    ? SpeechRecognitionEngine.WhisperGgml
                    : engine;
            if (fallback != engine)
            {
                AsrEngineCombo.SelectedItem = AsrEngines.First(item => item.Engine == fallback);
                engine = fallback;
            }
        }

        var anyAsrEnabled = EnableSenseVoiceCheck.IsChecked == true || EnableWhisperCheck.IsChecked == true;
        var useWhisper = engine == SpeechRecognitionEngine.WhisperGgml;
        WhisperModelTitle.Opacity = useWhisper ? 1 : 0.45;
        ModelPathBox.IsEnabled = useWhisper && !_running;
        DefaultModelPathText.Opacity = useWhisper ? 1 : 0.45;
        InstallDefaultModelButton.IsEnabled = useWhisper && EnableWhisperCheck.IsChecked == true;
        UninstallDefaultModelButton.IsEnabled = useWhisper && EnableWhisperCheck.IsChecked == true;
        SenseVoiceUrlBox.IsEnabled = engine == SpeechRecognitionEngine.SenseVoiceSmall && !_running;
        SenseVoiceModelBox.IsEnabled = engine == SpeechRecognitionEngine.SenseVoiceSmall && !_running;
        TestAsrButton.IsEnabled = engine == SpeechRecognitionEngine.SenseVoiceSmall && EnableSenseVoiceCheck.IsChecked == true && !_running;
        if (!_running) StartButton.IsEnabled = anyAsrEnabled;
        DefaultModelStatusText.Text = engine == SpeechRecognitionEngine.SenseVoiceSmall
            ? "推荐默认"
            : _speechModelManager.IsDefaultModelInstalled ? "已安装 · 当前默认" : "未安装";
        StatusText.Text = engine == SpeechRecognitionEngine.SenseVoiceSmall
            ? "当前 ASR：SenseVoice Small。请确认本地 FunASR/OpenAI-compatible ASR 服务已启动。"
            : "当前 ASR：Whisper GGML。可使用内置默认模型或自己的 GGML 模型。";
    }

    private bool IsAsrEngineEnabled(SpeechRecognitionEngine engine) => engine switch
    {
        SpeechRecognitionEngine.SenseVoiceSmall => EnableSenseVoiceCheck.IsChecked == true,
        SpeechRecognitionEngine.WhisperGgml => EnableWhisperCheck.IsChecked == true,
        _ => false
    };

    private async void TestAsrService_Click(object sender, RoutedEventArgs e)
    {
        TestAsrButton.IsEnabled = false;
        StatusText.Text = "正在测试 ASR 服务…";
        try
        {
            var result = await VideoSubtitleService.TestSenseVoiceEndpointAsync(
                SenseVoiceUrlBox.Text.Trim(),
                SenseVoiceModelBox.Text.Trim());
            StatusText.Text = $"ASR 测试成功：{result}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"ASR 测试失败：{exception.Message}";
        }
        finally
        {
            RefreshAsrEngineUi();
        }
    }

    private async void InstallDefaultModel_Click(object sender, RoutedEventArgs e)
    {
        _modelDownloadCancellation = new CancellationTokenSource();
        SetDefaultModelBusy(true);
        var progress = new Progress<ModelDownloadProgress>(value =>
        {
            DefaultModelProgress.Value = value.Percentage;
            DefaultModelProgressText.Text = $"{value.Stage} · {value.Percentage:F0}% · {FormatBytes(value.BytesReceived)} / {FormatBytes(value.TotalBytes)}";
        });
        try
        {
            await _speechModelManager.InstallDefaultAsync(progress, _modelDownloadCancellation.Token);
            ModelPathBox.Text = _speechModelManager.DefaultModelPath;
            SaveSettings();
            StatusText.Text = "默认 Whisper 多语言模型安装完成，可以开始视频字幕。";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "默认语音模型下载已取消。";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "默认模型安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _modelDownloadCancellation?.Dispose();
            _modelDownloadCancellation = null;
            SetDefaultModelBusy(false);
            RefreshDefaultModelStatus();
        }
    }

    private void CancelDefaultDownload_Click(object sender, RoutedEventArgs e) =>
        _modelDownloadCancellation?.Cancel();

    private void UninstallDefaultModel_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            MessageBox.Show(this, "请先停止视频字幕，再卸载正在使用的模型。", "模型正在使用", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, "确定卸载默认 Whisper 模型？\n将删除软件托管目录中的约 181 MiB 模型文件。",
                "卸载默认语音模型", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _speechModelManager.UninstallDefault();
        if (!string.IsNullOrWhiteSpace(ModelPathBox.Text) &&
            Path.GetFullPath(ModelPathBox.Text.Trim()).Equals(
                Path.GetFullPath(_speechModelManager.DefaultModelPath), StringComparison.OrdinalIgnoreCase))
            ModelPathBox.Text = string.Empty;
        SaveSettings();
        RefreshDefaultModelStatus();
        StatusText.Text = "默认 Whisper 模型已卸载；你可以重新安装或选择自己的模型。";
    }

    private void RefreshDefaultModelStatus()
    {
        var installed = _speechModelManager.IsDefaultModelInstalled;
        DefaultModelPathText.Text = _speechModelManager.DefaultModelPath;
        DefaultModelStatusText.Text = installed ? "已安装 · 当前默认" : "未安装";
        DefaultModelStatusText.Foreground = installed
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(2, 122, 72))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(181, 71, 8));
        InstallDefaultModelButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        UninstallDefaultModelButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        if (installed && (string.IsNullOrWhiteSpace(ModelPathBox.Text) ||
                          !File.Exists(ModelPathBox.Text) ||
                          Path.GetFullPath(ModelPathBox.Text.Trim()).Equals(
                              Path.GetFullPath(_speechModelManager.LegacyModelPath), StringComparison.OrdinalIgnoreCase)))
            ModelPathBox.Text = _speechModelManager.DefaultModelPath;
    }

    private void SetDefaultModelBusy(bool busy)
    {
        DefaultModelProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        InstallDefaultModelButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        UninstallDefaultModelButton.Visibility = Visibility.Collapsed;
        CancelDefaultDownloadButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StartButton.IsEnabled = !busy;
    }

    private static string FormatBytes(long value) => value <= 0 ? "未知" : $"{value / 1024d / 1024d:F1} MiB";

    private async void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_running) { await StopAsync(); return; }
        try
        {
            SaveSettings();
            ConfigureVideoTranslationService();
            _latestOverlayStart = TimeSpan.MinValue;
            Segments.Clear();
            ExportButton.IsEnabled = false;
            var source = ((LanguageItem)SourceCombo.SelectedItem).Value;
            var target = ((LanguageItem)TargetCombo.SelectedItem).Value;
            if (OverlayCheck.IsChecked == true)
            {
                _overlay = new SubtitleOverlayWindow(SourceFontSlider.Value, TranslationFontSlider.Value,
                    BottomOffsetSlider.Value, OverlayWidthSlider.Value, _overlayHeight,
                    _overlayLeft, _overlayTop, OverlayInteractionCheck.IsChecked == true,
                    OverlayOpacitySlider.Value, target);
                _overlay.PlacementChanged += OverlayOnPlacementChanged;
                _overlay.InteractionModeChanged += OverlayOnInteractionModeChanged;
                _overlay.FontSizeChanged += OverlayOnFontSizeChanged;
                _overlay.TargetLanguageChanged += OverlayOnTargetLanguageChanged;
                _overlay.CloseRequested += OverlayOnCloseRequested;
                _overlay.Show();
            }
            if (source != SupportedLanguage.AutoDetect && source == target)
                throw new InvalidOperationException("源语言和目标语言不能相同。英文视频请选择 English → 简体中文，或使用自动检测 → 简体中文。");
            var concurrency = VideoConcurrencyCombo.SelectedItem is int selectedConcurrency
                ? selectedConcurrency
                : 3;
            var asrEngine = ((AsrEngineChoice)AsrEngineCombo.SelectedItem).Engine;
            if (!IsAsrEngineEnabled(asrEngine))
                throw new InvalidOperationException("当前 ASR 引擎已禁用，请先启用或切换到可用引擎。");
            await _service.StartAsync(
                asrEngine,
                ModelPathBox.Text.Trim(),
                SenseVoiceUrlBox.Text.Trim(),
                SenseVoiceModelBox.Text.Trim(),
                source,
                target,
                concurrency);
            _running = true;
            StartButton.Content = "停止翻译";
            RefreshAsrEngineUi();
            (Owner as MainWindow)?.MinimizeForSubtitle();
        }
        catch (Exception exception)
        {
            _overlay?.Close(); _overlay = null;
            MessageBox.Show(this, exception.Message, "无法启动视频字幕", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task StopAsync()
    {
        if (!_running && !_service.IsRunning) return;
        StartButton.IsEnabled = false;
        StartButton.Content = "正在停止…";
        try
        {
            await _service.StopAsync();
        }
        finally
        {
            _running = false;
            StartButton.Content = "开始翻译";
            StartButton.IsEnabled = true;
            RefreshAsrEngineUi();
            _overlay?.Close(); _overlay = null;
            if (!_allowClose)
            {
                (Owner as MainWindow)?.RestoreFromTray();
                Show();
                Activate();
            }
        }
    }

    private void VideoSubtitleWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !_running || Application.Current.Dispatcher.HasShutdownStarted) return;
        e.Cancel = true;
        Hide();
        StatusText.Text = "控制界面已隐藏，视频字幕继续在后台运行。";
    }

    private async void OverlayOnCloseRequested(object? sender, EventArgs e)
    {
        (sender as Window)?.Hide();
        await StopAsync();
        (Owner as MainWindow)?.RestoreFromTray();
        Show();
        Activate();
    }

    public void ToggleOverlayInteraction()
    {
        var enabled = OverlayInteractionCheck.IsChecked != true;
        OverlayInteractionCheck.IsChecked = enabled;
        _overlay?.SetInteractionEnabled(enabled);
        SaveSettings();
    }

    private void ServiceOnSegmentReady(object? sender, SubtitleSegment segment) => Dispatcher.Invoke(() =>
    {
        var row = new SubtitleRow(segment);
        var existing = Segments
            .Select((item, index) => (item, index))
            .FirstOrDefault(value => value.item.Segment.Start == segment.Start);
        if (existing.item is null) Segments.Add(row);
        else Segments[existing.index] = row;
        SubtitleList.ItemsSource = Segments;
        if (segment.Start >= _latestOverlayStart) SubtitleList.ScrollIntoView(row);
        ExportButton.IsEnabled = Segments.Count > 0;
        _overlay?.ShowSegment(segment, BilingualCheck.IsChecked == true || MovieModeRadio.IsChecked == true);
    });

    private void ServiceOnSourceSegmentReady(object? sender, SubtitleSegment segment) => Dispatcher.Invoke(() =>
    {
        var row = new SubtitleRow(segment);
        var existing = Segments
            .Select((item, index) => (item, index))
            .FirstOrDefault(value => value.item.Segment.Start == segment.Start);
        if (existing.item is null) Segments.Add(row);
        else Segments[existing.index] = row;
        _latestOverlayStart = segment.Start;
        SubtitleList.ItemsSource = Segments;
        SubtitleList.ScrollIntoView(row);
        _overlay?.ShowSource(segment.SourceText,
            BilingualCheck.IsChecked == true || MovieModeRadio.IsChecked == true);
    });

    private void OverlaySetting_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_settingsLoaded || _applyingOverlayPlacement) return;
        _overlay?.ApplyLayout(SourceFontSlider.Value, TranslationFontSlider.Value,
            BottomOffsetSlider.Value, OverlayWidthSlider.Value,
            ReferenceEquals(sender, BottomOffsetSlider));
        _overlayOpacity = OverlayOpacitySlider.Value;
        _overlay?.ApplyBackgroundOpacity(_overlayOpacity);
        if (ReferenceEquals(sender, BottomOffsetSlider))
        {
            _overlayLeft = null;
            _overlayTop = null;
        }
        SaveSettings();
    }

    private void OverlayInteraction_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingsLoaded) return;
        _overlay?.SetInteractionEnabled(OverlayInteractionCheck.IsChecked == true);
        SaveSettings();
    }

    private void ResetOverlayPosition_Click(object sender, RoutedEventArgs e)
    {
        _overlayLeft = null;
        _overlayTop = null;
        _overlayHeight = 150;
        OverlayWidthSlider.Value = 620;
        _overlay?.ResetPosition();
        SaveSettings();
    }

    private void OverlayOnPlacementChanged(object? sender, SubtitleOverlayPlacement placement)
    {
        _overlayLeft = placement.Left;
        _overlayTop = placement.Top;
        _overlayHeight = placement.Height;
        _applyingOverlayPlacement = true;
        OverlayWidthSlider.Value = Math.Clamp(placement.Width,
            OverlayWidthSlider.Minimum, OverlayWidthSlider.Maximum);
        _applyingOverlayPlacement = false;
        SaveSettings();
    }

    private void OverlayOnInteractionModeChanged(object? sender, bool interactionEnabled)
    {
        OverlayInteractionCheck.IsChecked = interactionEnabled;
        SaveSettings();
    }

    private void OverlayOnFontSizeChanged(object? sender, SubtitleFontSizeChanged sizes)
    {
        _applyingOverlayPlacement = true;
        SourceFontSlider.Value = sizes.SourceFontSize;
        TranslationFontSlider.Value = sizes.TranslationFontSize;
        _applyingOverlayPlacement = false;
        SaveSettings();
    }

    private void OverlayOnTargetLanguageChanged(object? sender, SupportedLanguage target)
    {
        if (_syncingTargetLanguage) return;
        var source = SourceCombo.SelectedItem is LanguageItem sourceItem
            ? sourceItem.Value
            : SupportedLanguage.AutoDetect;
        if (source != SupportedLanguage.AutoDetect && source == target)
        {
            StatusText.Text = "目标语言不能与源语言相同，请先把源语言改为自动检测或选择其他目标语言。";
            if (TargetCombo.SelectedItem is LanguageItem currentTarget)
                _overlay?.SetTargetLanguage(currentTarget.Value);
            return;
        }

        var targetItem = TargetLanguages.FirstOrDefault(item => item.Value == target);
        if (targetItem is null) return;
        ApplyTargetLanguage(targetItem, updateCombo: true);
    }

    private void TargetCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_settingsLoaded || _syncingTargetLanguage || TargetCombo.SelectedItem is not LanguageItem target) return;
        ApplyTargetLanguage(target, updateCombo: false);
    }

    private void ApplyTargetLanguage(LanguageItem targetItem, bool updateCombo)
    {
        _syncingTargetLanguage = true;
        try
        {
            if (updateCombo) TargetCombo.SelectedItem = targetItem;
            _service.SetTargetLanguage(targetItem.Value);
            _overlay?.SetTargetLanguage(targetItem.Value);
            StatusText.Text = $"后续视频字幕将翻译为：{targetItem.DisplayName}。";
            SaveSettings();
        }
        finally
        {
            _syncingTargetLanguage = false;
        }
    }

    private void VideoProvider_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_settingsLoaded || VideoProviderCombo.SelectedItem is not VideoProviderChoice choice) return;
        if (choice.Id.Equals(_selectedVideoProviderId, StringComparison.OrdinalIgnoreCase)) return;
        _selectedVideoProviderId = choice.Id;
        LoadVideoProviderModel(choice.Id, null);
        SaveSettings();
    }

    private void VideoConcurrency_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_settingsLoaded) SaveSettings();
    }

    private void VideoScene_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_settingsLoaded) SaveSettings();
    }

    private async void ChooseVideoModel_Click(object sender, RoutedEventArgs e)
    {
        TestVideoProviderButton.IsEnabled = false;
        VideoProviderStatusText.Text = "正在读取当前服务的可用翻译模型…";
        try
        {
            var provider = GetSelectedVideoProvider();
            using var discovery = _providerRouter.CreateOnlineService(provider);
            var models = (await discovery.GetAvailableModelsAsync())
                .Where(OpenAiCompatibleTranslationService.IsTextGenerationModel)
                .ToArray();
            if (models.Length == 0) throw new InvalidOperationException("服务没有返回文本生成模型。");

            var current = GetSelectedVideoModel();
            VideoModels.Clear();
            foreach (var model in models) VideoModels.Add(model);
            var selected = models.FirstOrDefault(model =>
                model.Equals(current, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                VideoModelCombo.SelectedItem = selected;
                VideoModelCombo.Text = selected;
            }
            else
            {
                VideoModelCombo.SelectedIndex = -1;
                VideoModelCombo.Text = string.Empty;
            }

            VideoProviderStatusText.Text = $"已读取 {models.Length} 个文本模型，请从翻译模型下拉框中选择。";
            VideoModelCombo.IsDropDownOpen = true;
        }
        catch (Exception exception)
        {
            VideoProviderStatusText.Text = $"模型列表读取失败：{exception.Message}";
        }
        finally
        {
            TestVideoProviderButton.IsEnabled = true;
        }
    }

    private void VideoModel_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_settingsLoaded || VideoModelCombo.SelectedItem is not string model) return;
        VideoProviderStatusText.Text = $"已选择翻译模型：{model}";
        SaveSettings();
    }

    private void VideoModel_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_settingsLoaded) return;
        var model = GetSelectedVideoModel();
        if (!string.IsNullOrWhiteSpace(model))
            VideoProviderStatusText.Text = $"已选择翻译模型：{model}";
        SaveSettings();
    }

    private void ConfigureVideoTranslationService()
    {
        var provider = GetSelectedVideoProvider();
        var model = GetSelectedVideoModel();
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("请先点击“选择翻译模型”，并从列表中选择一个模型。");
        _translationSession.Configure(_providerRouter.CreateOnlineService(
            provider with { Model = model }, GetVideoSystemPrompt()));
        var concurrency = VideoConcurrencyCombo.SelectedItem is int selectedConcurrency
            ? selectedConcurrency
            : 3;
        VideoProviderStatusText.Text =
            $"视频同传已使用：{provider.DisplayName} · {model} · {concurrency} 路并发";
    }

    private OnlineProviderSettings GetSelectedVideoProvider()
    {
        if (VideoProviderCombo.SelectedItem is not VideoProviderChoice choice)
            throw new InvalidOperationException("请选择视频翻译服务。");
        var provider = _providerSettings.OnlineProviders.First(item => item.Id == choice.Id);
        if (string.IsNullOrWhiteSpace(provider.BaseUrl))
            throw new InvalidOperationException($"{provider.DisplayName} 尚未配置 API Base URL。");
        return provider;
    }

    private string GetSelectedVideoModel() =>
        VideoModelCombo.SelectedItem is string selected && !string.IsNullOrWhiteSpace(selected)
            ? selected.Trim()
            : VideoModelCombo.Text.Trim();

    private string GetVideoSystemPrompt() => VideoSubtitleTranslationPrompt.ForScene(
        (VideoSceneCombo.SelectedItem as VideoSceneChoice)?.Id);

    private void LoadVideoProviderModel(string providerId, string? preferredModel)
    {
        var provider = _providerSettings.OnlineProviders.First(item => item.Id == providerId);
        VideoModels.Clear();
        var model = string.IsNullOrWhiteSpace(preferredModel) ? provider.Model : preferredModel;
        if (!string.IsNullOrWhiteSpace(model)) VideoModels.Add(model);
        VideoModelCombo.SelectedItem = VideoModels.FirstOrDefault();
        VideoModelCombo.Text = model;
    }

    private async void ExportSrt_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "SubRip 字幕 (*.srt)|*.srt", FileName = $"subtitle-{DateTime.Now:yyyyMMdd-HHmm}.srt" };
        if (dialog.ShowDialog(this) != true) return;
        await SrtSubtitleWriter.WriteAsync(dialog.FileName, Segments.Select(item => item.Segment), BilingualCheck.IsChecked == true);
        StatusText.Text = $"字幕已导出：{dialog.FileName}";
    }

    private void LoadSettings()
    {
        try
        {
            var settings = File.Exists(_settingsPath)
                ? JsonSerializer.Deserialize<VideoSettings>(File.ReadAllText(_settingsPath)) ?? new VideoSettings()
                : new VideoSettings();
            var migrateCompactOverlay = settings.OverlayLayoutVersion < CurrentOverlayLayoutVersion;
            ModelPathBox.Text = settings.WhisperModelPath ?? string.Empty;
            SenseVoiceUrlBox.Text = string.IsNullOrWhiteSpace(settings.SenseVoiceBaseUrl)
                ? "http://127.0.0.1:8899/v1"
                : settings.SenseVoiceBaseUrl;
            SenseVoiceModelBox.Text = string.IsNullOrWhiteSpace(settings.SenseVoiceModel)
                ? "fun-asr-nano"
                : settings.SenseVoiceModel;
            EnableSenseVoiceCheck.IsChecked = settings.EnableSenseVoice;
            EnableWhisperCheck.IsChecked = settings.EnableWhisper;
            AsrEngineCombo.SelectedItem = AsrEngines.FirstOrDefault(item =>
                                             item.Engine == settings.AsrEngine)
                                         ?? AsrEngines[0];
            SourceFontSlider.Value = migrateCompactOverlay ? 17 : settings.SourceFontSize;
            TranslationFontSlider.Value = migrateCompactOverlay ? 24 : settings.TranslationFontSize;
            BottomOffsetSlider.Value = settings.BottomOffset;
            OverlayWidthSlider.Value = migrateCompactOverlay ? 620 : settings.OverlayWidth;
            _overlayOpacity = Math.Clamp(settings.OverlayOpacity ?? 0.66, 0, 0.92);
            OverlayOpacitySlider.Value = _overlayOpacity;
            OverlayInteractionCheck.IsChecked = settings.OverlayInteractionEnabled;
            _overlayLeft = migrateCompactOverlay ? null : settings.OverlayLeft;
            _overlayTop = migrateCompactOverlay ? null : settings.OverlayTop;
            _overlayHeight = migrateCompactOverlay ? 150 : settings.OverlayHeight;
            VideoConcurrencyCombo.SelectedItem = Math.Clamp(settings.TranslationConcurrency, 1, 4);
            VideoSceneCombo.SelectedItem = VideoSceneOptions.FirstOrDefault(item =>
                                                item.Id.Equals(settings.VideoSceneId,
                                                    StringComparison.OrdinalIgnoreCase))
                                            ?? VideoSceneOptions[0];
            var providerId = !string.IsNullOrWhiteSpace(settings.VideoProviderId)
                ? settings.VideoProviderId
                : _providerSettings.ActiveProviderId;
            var providerChoice = VideoProviders.FirstOrDefault(item => item.Id.Equals(
                                     providerId, StringComparison.OrdinalIgnoreCase))
                                 ?? VideoProviders.FirstOrDefault(item =>
                                 {
                                     var provider = _providerSettings.OnlineProviders.First(value => value.Id == item.Id);
                                     return provider.BaseUrl.Length > 0 &&
                                            provider.Id is "local-service" or "custom";
                                 })
                                 ?? VideoProviders.FirstOrDefault(item =>
                                     _providerSettings.OnlineProviders.First(provider => provider.Id == item.Id)
                                         .BaseUrl.Length > 0)
                                 ?? VideoProviders.FirstOrDefault();
            _selectedVideoProviderId = providerChoice?.Id;
            VideoProviderCombo.SelectedItem = providerChoice;
            if (providerChoice is not null)
                LoadVideoProviderModel(providerChoice.Id, settings.VideoModel);
        }
        catch { }
    }

    private void SaveSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new VideoSettings
        {
            AsrEngine = (AsrEngineCombo.SelectedItem as AsrEngineChoice)?.Engine
                        ?? SpeechRecognitionEngine.SenseVoiceSmall,
            WhisperModelPath = ModelPathBox.Text.Trim(),
            SenseVoiceBaseUrl = SenseVoiceUrlBox.Text.Trim(),
            SenseVoiceModel = SenseVoiceModelBox.Text.Trim(),
            EnableSenseVoice = EnableSenseVoiceCheck.IsChecked == true,
            EnableWhisper = EnableWhisperCheck.IsChecked == true,
            SourceFontSize = SourceFontSlider.Value,
            TranslationFontSize = TranslationFontSlider.Value,
            BottomOffset = BottomOffsetSlider.Value,
            OverlayWidth = OverlayWidthSlider.Value,
            OverlayHeight = _overlayHeight,
            OverlayOpacity = OverlayOpacitySlider.Value,
            OverlayLeft = _overlayLeft,
            OverlayTop = _overlayTop,
            OverlayInteractionEnabled = OverlayInteractionCheck.IsChecked == true,
            VideoProviderId = (VideoProviderCombo.SelectedItem as VideoProviderChoice)?.Id ?? string.Empty,
            VideoModel = GetSelectedVideoModel(),
            TranslationConcurrency = VideoConcurrencyCombo.SelectedItem is int concurrency ? concurrency : 3,
            VideoSceneId = (VideoSceneCombo.SelectedItem as VideoSceneChoice)?.Id ?? "general",
            OverlayLayoutVersion = CurrentOverlayLayoutVersion
        }));
    }

    private sealed class VideoSettings
    {
        public SpeechRecognitionEngine AsrEngine { get; init; } = SpeechRecognitionEngine.SenseVoiceSmall;
        public string? WhisperModelPath { get; init; }
        public string SenseVoiceBaseUrl { get; init; } = "http://127.0.0.1:8899/v1";
        public string SenseVoiceModel { get; init; } = "fun-asr-nano";
        public bool EnableSenseVoice { get; init; } = true;
        public bool EnableWhisper { get; init; } = true;
        public double SourceFontSize { get; init; } = 17;
        public double TranslationFontSize { get; init; } = 24;
        public double BottomOffset { get; init; } = 28;
        public double OverlayWidth { get; init; } = 620;
        public double OverlayHeight { get; init; } = 150;
        public double? OverlayOpacity { get; init; } = 0.66;
        public double? OverlayLeft { get; init; }
        public double? OverlayTop { get; init; }
        public bool OverlayInteractionEnabled { get; init; } = true;
        public string VideoProviderId { get; init; } = string.Empty;
        public string VideoModel { get; init; } = PreferredVideoModel;
        public int TranslationConcurrency { get; init; } = 3;
        public string VideoSceneId { get; init; } = "general";
        public int OverlayLayoutVersion { get; init; }
    }

    private sealed class VideoTranslationSessionService : ITranslationService, IDisposable
    {
        private OpenAiCompatibleTranslationService? _service;

        public void Configure(OpenAiCompatibleTranslationService service)
        {
            _service?.Dispose();
            _service = service;
        }

        public Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken = default) =>
            (_service ?? throw new InvalidOperationException("视频翻译服务尚未配置。"))
            .TranslateAsync(request, cancellationToken);

        public void Dispose()
        {
            _service?.Dispose();
            _service = null;
        }
    }
}

public sealed record VideoProviderChoice(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record AsrEngineChoice(SpeechRecognitionEngine Engine, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record VideoSceneChoice(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record SubtitleRow(SubtitleSegment Segment)
{
    public string TimeText => $"{Segment.Start:hh\\:mm\\:ss} → {Segment.End:hh\\:mm\\:ss}";
    public string SourceText => Segment.SourceText;
    public string TranslatedText => string.IsNullOrWhiteSpace(Segment.TranslatedText) ? "翻译中…" : Segment.TranslatedText;
}
