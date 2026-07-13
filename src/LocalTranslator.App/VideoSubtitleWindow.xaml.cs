using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using LocalTranslator.Core.Abstractions;
using LocalTranslator.Core.Models;
using LocalTranslator.Infrastructure.Configuration;
using LocalTranslator.Infrastructure.Services;
using Microsoft.Win32;

namespace LocalTranslator.App;

public partial class VideoSubtitleWindow : Window
{
    private const string PreferredVideoModel = "gemma-4-26b-a4b-it-mlx";
    private const int CurrentOverlayLayoutVersion = 4;
    private const int CurrentAsrConfigurationVersion = 1;
    private const string DefaultAsrStartCommand = "funasr-server --host 127.0.0.1 --port 8899 --device cpu --model sensevoice";
    private readonly VideoSubtitleService _service;
    private readonly VideoTranslationSessionService _translationSession = new();
    private readonly TranslationProviderRouter _providerRouter;
    private readonly TranslationProviderSettings _providerSettings;
    private readonly SpeechModelManager _speechModelManager;
    private readonly string _dataRoot;
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
    private CancellationTokenSource? _asrServerStartCancellation;
    private Process? _managedAsrServerProcess;
    private readonly StringBuilder _asrServerOutput = new();
    private readonly object _asrServerOutputLock = new();
    private bool _asrDependenciesInstalled;
    private bool _checkingAsrDependencies;
    private string ManagedAsrEnvironmentRoot => Path.Combine(_dataRoot, "Runtime", "Python");
    private string ManagedAsrPythonPath => Path.Combine(ManagedAsrEnvironmentRoot, "Scripts", "python.exe");

    public VideoSubtitleWindow(
        SecureTranslationProviderStore providerStore,
        TranslationProviderRouter providerRouter,
        IAppLogger logger,
        AppOptions options)
    {
        InitializeComponent();
        _dataRoot = AppStoragePaths.ResolveDataRoot(options);
        _speechModelManager = new SpeechModelManager(options);
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
        _settingsPath = Path.Combine(_dataRoot, "video-subtitle-settings.json");
        LoadSettings();
        _settingsLoaded = true;
        SaveSettings();
        RefreshDefaultModelStatus();
        RefreshAsrEngineUi();
        Loaded += async (_, _) => await RefreshAsrDependencyUiAsync();
        Closing += VideoSubtitleWindow_Closing;
        Closed += (_, _) =>
        {
            StopManagedAsrServer();
            _asrServerStartCancellation?.Dispose();
            _speechModelManager.Dispose();
            _translationSession.Dispose();
        };
    }

    public IReadOnlyList<LanguageItem> SourceLanguages { get; }
    public IReadOnlyList<LanguageItem> TargetLanguages { get; }
    public IReadOnlyList<AsrEngineChoice> AsrEngines { get; } =
    [
        new(SpeechRecognitionEngine.SenseVoiceSmall, "SenseVoice Small（推荐，开始翻译时自动启动）"),
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
        if (WhisperModelTitle is null ||
            ModelPathBox is null ||
            DefaultModelPathText is null ||
            InstallDefaultModelButton is null ||
            UninstallDefaultModelButton is null ||
            SenseVoiceUrlBox is null ||
            SenseVoiceModelBox is null ||
            TestAsrButton is null ||
            AsrStartCommandBox is null ||
            ResetAsrCommandButton is null ||
            InstallAsrDependenciesButton is null ||
            StartAsrServerButton is null ||
            AsrStatusText is null ||
            AsrDependencyProgressPanel is null ||
            AsrDependencyProgressText is null ||
            StartButton is null ||
            DefaultModelStatusText is null ||
            StatusText is null)
        {
            return;
        }

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
        AsrStartCommandBox.IsEnabled = !_running;
        ResetAsrCommandButton.IsEnabled = !_running;
        InstallAsrDependenciesButton.IsEnabled = !_asrDependenciesInstalled && !_checkingAsrDependencies;
        InstallAsrDependenciesButton.Content = _checkingAsrDependencies
            ? "正在检查…"
            : _asrDependenciesInstalled ? "依赖已安装" : "安装依赖";
        if (!_running) StartButton.IsEnabled = anyAsrEnabled;
        DefaultModelStatusText.Text = engine == SpeechRecognitionEngine.SenseVoiceSmall
            ? "推荐默认"
            : _speechModelManager.IsDefaultModelInstalled ? "已安装 · 当前默认" : "未安装";
        SetAsrStatus(engine == SpeechRecognitionEngine.SenseVoiceSmall
            ? "当前 ASR：SenseVoice Small。点击“开始翻译”后软件会自动启动并加载 G 盘模型。"
            : "\u5f53\u524d ASR\uff1aWhisper GGML\u3002\u53ef\u4f7f\u7528\u5185\u7f6e\u9ed8\u8ba4\u6a21\u578b\u6216\u81ea\u5df1\u7684 GGML \u6a21\u578b\u3002");
    }

    private void SetAsrStatus(string message)
    {
        if (AsrStatusText is not null) AsrStatusText.Text = message;
        if (StatusText is not null) StatusText.Text = message;
    }

    private bool IsAsrEngineEnabled(SpeechRecognitionEngine engine) => engine switch
    {
        SpeechRecognitionEngine.SenseVoiceSmall => EnableSenseVoiceCheck?.IsChecked == true,
        SpeechRecognitionEngine.WhisperGgml => EnableWhisperCheck?.IsChecked == true,
        _ => false
    };

    private async void TestAsrService_Click(object sender, RoutedEventArgs e)
    {
        TestAsrButton.IsEnabled = false;
        SetAsrStatus("\u6b63\u5728\u6d4b\u8bd5 ASR \u670d\u52a1\u2026");
        try
        {
            var result = await VideoSubtitleService.TestSenseVoiceEndpointAsync(
                SenseVoiceUrlBox.Text.Trim(),
                SenseVoiceModelBox.Text.Trim());
            SetAsrStatus($"ASR \u6d4b\u8bd5\u6210\u529f\uff1a{result}");
        }
        catch (Exception exception)
        {
            SetAsrStatus($"ASR \u6d4b\u8bd5\u5931\u8d25\uff1a{exception.Message}");
        }
        finally
        {
            TestAsrButton.IsEnabled =
                AsrEngineCombo.SelectedItem is AsrEngineChoice { Engine: SpeechRecognitionEngine.SenseVoiceSmall } &&
                EnableSenseVoiceCheck.IsChecked == true &&
                !_running;
        }
    }

    private async void InstallAsrDependencies_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await CheckAsrDependenciesAsync() is { IsComplete: true })
            {
                await RefreshAsrDependencyUiAsync(showStatus: true);
                return;
            }
        }
        catch
        {
            // The installation flow below reports missing Python or packages with a styled status message.
        }

        var confirmation = new StyledConfirmDialog(
            "安装 ASR 依赖",
            "软件将使用 Python 3.11/3.12 安装 FunASR、PyTorch 和本地 API 服务组件。首次安装需要下载较大的 PyTorch 包，耗时取决于网络速度；安装进度会直接显示在当前页面。",
            "开始安装")
        {
            Owner = this
        };
        if (confirmation.ShowDialog() != true) return;

        InstallAsrDependenciesButton.IsEnabled = false;
        InstallAsrDependenciesButton.Content = "正在安装…";
        AsrDependencyProgressPanel.Visibility = Visibility.Visible;
        AsrDependencyProgressText.Text = "正在查找兼容的 Python 运行环境…";
        SetAsrStatus("正在安装 ASR 依赖，窗口可以继续查看，但请不要关闭软件。");
        try
        {
            var bootstrapPython = await FindSupportedPythonCommandAsync();
            var progress = new Progress<string>(line =>
            {
                var summary = FormatInstallationProgress(line);
                if (!string.IsNullOrWhiteSpace(summary))
                    AsrDependencyProgressText.Text = summary;
            });
            if (!File.Exists(ManagedAsrPythonPath))
            {
                AsrDependencyProgressText.Text = $"正在 G 盘创建应用专用 Python 环境：{ManagedAsrEnvironmentRoot}";
                Directory.CreateDirectory(Path.GetDirectoryName(ManagedAsrEnvironmentRoot)!);
                await RunCommandWithProgressAsync(
                    $"{bootstrapPython} -m venv \"{ManagedAsrEnvironmentRoot}\"",
                    TimeSpan.FromMinutes(5),
                    progress);
            }

            var python = QuoteCommandPath(ManagedAsrPythonPath);
            await RunCommandWithProgressAsync(
                $"{python} -m pip install -U pip setuptools wheel && " +
                $"{python} -m pip install torch torchaudio funasr fastapi uvicorn python-multipart",
                TimeSpan.FromMinutes(30),
                progress);

            var check = await CheckAsrDependenciesAsync();
            if (!check.IsComplete)
                throw new InvalidOperationException($"安装命令已结束，但仍缺少组件：{string.Join("、", check.MissingComponents)}");

            if (string.IsNullOrWhiteSpace(AsrStartCommandBox.Text) ||
                AsrStartCommandBox.Text.Contains("py -3.12 -m funasr.server", StringComparison.OrdinalIgnoreCase))
            {
                AsrStartCommandBox.Text = DefaultAsrStartCommand;
                SaveSettings();
            }
            _asrDependenciesInstalled = true;
            AsrDependencyProgressText.Text = "FunASR、PyTorch 与 API 服务组件均已就绪。";
            SetAsrStatus("ASR 依赖安装完成，可以启动本地 ASR 服务。");
        }
        catch (Exception exception)
        {
            SetAsrStatus($"ASR 依赖安装失败：{exception.Message}");
            AsrDependencyProgressText.Text = "安装未完成，请查看上方错误后重试。";
        }
        finally
        {
            AsrDependencyProgressPanel.Visibility = Visibility.Collapsed;
            await RefreshAsrDependencyUiAsync();
        }
    }

    private async Task RefreshAsrDependencyUiAsync(bool showStatus = false)
    {
        if (_checkingAsrDependencies || InstallAsrDependenciesButton is null) return;
        _checkingAsrDependencies = true;
        InstallAsrDependenciesButton.IsEnabled = false;
        InstallAsrDependenciesButton.Content = "正在检查…";
        try
        {
            var check = await CheckAsrDependenciesAsync();
            _asrDependenciesInstalled = check.IsComplete;
            InstallAsrDependenciesButton.Content = check.IsComplete ? "依赖已安装" : "安装依赖";
            InstallAsrDependenciesButton.IsEnabled = !check.IsComplete;
            InstallAsrDependenciesButton.ToolTip = check.IsComplete
                ? $"已使用 {check.PythonCommand}，FunASR 与 PyTorch 均可用。"
                : check.MissingComponents.Count > 0
                    ? $"缺少：{string.Join("、", check.MissingComponents)}"
                    : "需要 Python 3.10～3.12。";
            if (showStatus)
                SetAsrStatus(check.IsComplete
                    ? "ASR 依赖已安装，无需重复安装。"
                    : $"ASR 依赖尚未完整安装：{string.Join("、", check.MissingComponents)}");
        }
        catch (Exception exception)
        {
            _asrDependenciesInstalled = false;
            InstallAsrDependenciesButton.Content = "安装依赖";
            InstallAsrDependenciesButton.IsEnabled = true;
            InstallAsrDependenciesButton.ToolTip = exception.Message;
            if (showStatus) SetAsrStatus($"ASR 依赖检查失败：{exception.Message}");
        }
        finally
        {
            _checkingAsrDependencies = false;
        }
    }

    private async Task<AsrDependencyCheckResult> CheckAsrDependenciesAsync()
    {
        if (!File.Exists(ManagedAsrPythonPath))
            return new AsrDependencyCheckResult(ManagedAsrPythonPath, ["应用专用 ASR 运行环境"]);
        var python = QuoteCommandPath(ManagedAsrPythonPath);
        var missing = (await RunCommandToExitAsync(
            $"{python} -c \"import importlib.util as u; names=('funasr','fastapi','uvicorn','multipart','torch','torchaudio'); print(','.join(n for n in names if u.find_spec(n) is None))\"",
            TimeSpan.FromSeconds(20))).Trim();
        var components = missing.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(GetDependencyDisplayName)
            .ToList();
        var server = await FindFunAsrServerCommandAsync(python);
        if (server.Equals("funasr-server", StringComparison.OrdinalIgnoreCase))
            components.Add("funasr-server");
        return new AsrDependencyCheckResult(python, components);
    }

    private static string GetDependencyDisplayName(string component) => component switch
    {
        "torch" => "PyTorch",
        "torchaudio" => "TorchAudio",
        "multipart" => "python-multipart",
        _ => component
    };

    private async void ToggleAsrServer_Click(object sender, RoutedEventArgs e)
    {
        if (_managedAsrServerProcess is not null && !_managedAsrServerProcess.HasExited)
        {
            StopAsrServer();
            return;
        }

        await StartAsrServerAsync();
    }

    private void ResetAsrCommand_Click(object sender, RoutedEventArgs e)
    {
        AsrStartCommandBox.Text = DefaultAsrStartCommand;
        SaveSettings();
        SetAsrStatus("ASR 启动命令已恢复为安全默认值。");
    }

    private async Task<bool> StartAsrServerAsync()
    {
        StartAsrServerButton.IsEnabled = false;
        _asrServerStartCancellation?.Cancel();
        _asrServerStartCancellation?.Dispose();
        _asrServerStartCancellation = new CancellationTokenSource();
        var token = _asrServerStartCancellation.Token;

        try
        {
            SetAsrStatus("正在检查 ASR 服务是否已经启动…");
            if (await TryTestAsrEndpointAsync(token))
            {
                SetAsrStatus("ASR 服务已经可用。");
                StartAsrServerButton.Content = "ASR 服务已运行";
                return true;
            }

            var dependencyCheck = await CheckAsrDependenciesAsync();
            _asrDependenciesInstalled = dependencyCheck.IsComplete;
            if (!dependencyCheck.IsComplete)
            {
                await RefreshAsrDependencyUiAsync();
                throw new InvalidOperationException(
                    $"ASR 依赖不完整，请先安装：{string.Join("、", dependencyCheck.MissingComponents)}。");
            }

            var configuredCommand = ValidateAsrStartCommand(AsrStartCommandBox.Text);
            var command = await NormalizeAsrStartCommandAsync(configuredCommand);
            if (string.IsNullOrWhiteSpace(command))
                throw new InvalidOperationException("ASR 服务启动命令不能为空。");

            lock (_asrServerOutputLock) _asrServerOutput.Clear();
            _managedAsrServerProcess = StartBackgroundCommand(command, CaptureAsrServerOutput, _dataRoot);
            SetAsrStatus(IsSenseVoiceModelCached()
                ? "正在从 G 盘加载已下载的 SenseVoice 模型，无需再次下载…"
                : "首次使用正在下载 SenseVoice 模型到 G 盘缓存；后续启动将直接复用…");

            var deadline = DateTimeOffset.Now.AddMinutes(5);
            while (DateTimeOffset.Now < deadline)
            {
                token.ThrowIfCancellationRequested();
                if (_managedAsrServerProcess.HasExited)
                    throw new InvalidOperationException(
                        $"ASR 服务进程已退出（代码 {_managedAsrServerProcess.ExitCode}）：{GetAsrServerOutputTail()}");

                await Task.Delay(2000, token);
                if (await TryTestAsrEndpointAsync(token))
                {
                    SetAsrStatus("ASR 服务启动成功，可以开始视频字幕。");
                    StartAsrServerButton.Content = "停止 ASR 服务";
                    return true;
                }

                var latestOutput = GetAsrServerOutputTail(260, latestLineOnly: true);
                if (!string.IsNullOrWhiteSpace(latestOutput))
                    SetAsrStatus($"ASR 服务正在启动：{latestOutput}");
            }

            throw new TimeoutException($"ASR 服务在 5 分钟内未就绪：{GetAsrServerOutputTail()}");
        }
        catch (OperationCanceledException)
        {
            StopManagedAsrServer();
            SetAsrStatus("ASR 服务启动已取消。");
            return false;
        }
        catch (Exception exception)
        {
            StopManagedAsrServer();
            SetAsrStatus($"ASR 服务启动失败：{exception.Message}");
            return false;
        }
        finally
        {
            StartAsrServerButton.IsEnabled = true;
        }
    }

    private bool IsSenseVoiceModelCached()
    {
        var modelScopeRoot = Path.Combine(_dataRoot, "Cache", "modelscope", "models");
        if (!Directory.Exists(modelScopeRoot)) return false;
        try
        {
            return Directory.EnumerateFiles(modelScopeRoot, "model.pt", SearchOption.AllDirectories)
                .Any(path => path.Contains("SenseVoiceSmall", StringComparison.OrdinalIgnoreCase) &&
                             new FileInfo(path).Length > 100L * 1024 * 1024);
        }
        catch
        {
            return false;
        }
    }

    private void StopAsrServer()
    {
        _asrServerStartCancellation?.Cancel();
        if (_managedAsrServerProcess is null || _managedAsrServerProcess.HasExited)
        {
            SetAsrStatus("当前没有由本软件启动的 ASR 服务进程。");
            return;
        }

        try
        {
            StopManagedAsrServer();
            StartAsrServerButton.Content = "启动 ASR 服务";
            SetAsrStatus("已停止由本软件启动的 ASR 服务。");
        }
        catch (Exception exception)
        {
            SetAsrStatus($"停止 ASR 服务失败：{exception.Message}");
        }
    }

    private void StopManagedAsrServer()
    {
        if (_managedAsrServerProcess is null) return;
        try
        {
            if (!_managedAsrServerProcess.HasExited)
            {
                _managedAsrServerProcess.Kill(entireProcessTree: true);
                _managedAsrServerProcess.WaitForExit(5000);
            }
        }
        catch
        {
            // Best effort cleanup only.
        }
        finally
        {
            _managedAsrServerProcess.Dispose();
            _managedAsrServerProcess = null;
        }
    }

    private async Task<bool> TryTestAsrEndpointAsync(CancellationToken cancellationToken)
    {
        try
        {
            await VideoSubtitleService.TestSenseVoiceEndpointAsync(
                SenseVoiceUrlBox.Text.Trim(),
                SenseVoiceModelBox.Text.Trim(),
                cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> NormalizeAsrStartCommandAsync(string command)
    {
        command = string.IsNullOrWhiteSpace(command) ? DefaultAsrStartCommand : command.Trim();
        if (command.StartsWith("funasr-server", StringComparison.OrdinalIgnoreCase) ||
            command.StartsWith("python ", StringComparison.OrdinalIgnoreCase))
        {
            var python = File.Exists(ManagedAsrPythonPath)
                ? QuoteCommandPath(ManagedAsrPythonPath)
                : await FindSupportedPythonCommandAsync();
            if (command.StartsWith("funasr-server", StringComparison.OrdinalIgnoreCase))
            {
                var arguments = command["funasr-server".Length..].Trim();
                var server = await FindFunAsrServerCommandAsync(python);
                return $"{server} {arguments}".Trim();
            }

            if (command.StartsWith("python ", StringComparison.OrdinalIgnoreCase))
                return $"{python} {command["python".Length..].Trim()}";

            return command;
        }

        return command;
    }

    private static string ValidateAsrStartCommand(string? value)
    {
        var command = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException("ASR 启动命令为空。请点击“恢复默认”后重试。");
        if (command.IndexOfAny(['\r', '\n', '&', '|', '>', '<', '^']) >= 0)
            throw new InvalidOperationException("启动命令包含管道、重定向或多命令符号，已被安全拦截。请使用单个 ASR 启动命令。");

        var executable = command;
        var arguments = string.Empty;
        if (command.StartsWith('"'))
        {
            var closingQuote = command.IndexOf('"', 1);
            if (closingQuote < 0)
                throw new InvalidOperationException("启动命令中的路径引号不完整。请修正或点击“恢复默认”。");
            executable = command[1..closingQuote];
            arguments = command[(closingQuote + 1)..].Trim();
        }
        else
        {
            var separator = command.IndexOf(' ');
            if (separator > 0)
            {
                executable = command[..separator];
                arguments = command[(separator + 1)..].Trim();
            }
        }

        var name = Path.GetFileName(executable).ToLowerInvariant();
        if (name is "funasr-server" or "funasr-server.exe") return command;
        if (name is "python" or "python.exe" or "py" or "py.exe")
        {
            if (arguments.Contains("-c", StringComparison.OrdinalIgnoreCase) ||
                !arguments.Contains("-m funasr", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Python 命令仅允许启动 FunASR 模块，不能执行任意脚本。请点击“恢复默认”使用推荐命令。");
            return command;
        }

        throw new InvalidOperationException("仅允许 funasr-server 或 Python FunASR 启动命令。请点击“恢复默认”恢复可用配置。");
    }

    private static async Task<string> FindSupportedPythonCommandAsync()
    {
        var candidates = new[]
        {
            "py -3.12",
            "py -3.11",
            "python"
        };
        var newestUnsupported = string.Empty;
        foreach (var candidate in candidates)
        {
            try
            {
                var version = await RunCommandToExitAsync(
                    $"{candidate} -c \"import sys; print(str(sys.version_info.major)+'.'+str(sys.version_info.minor))\"",
                    TimeSpan.FromSeconds(10));
                version = version.Trim();
                if (Version.TryParse(version, out var parsed))
                {
                    if (parsed.Major == 3 && parsed.Minor is >= 10 and <= 12)
                        return candidate;
                    newestUnsupported = $"{candidate} = Python {version}";
                }
            }
            catch
            {
                // Try the next candidate.
            }
        }

        throw new InvalidOperationException(
            $"没有找到可用于 FunASR 的 Python 3.10～3.12。{newestUnsupported}。当前 Python 3.14 太新，editdistance 等依赖容易安装失败；请安装 Python 3.12 后重试。");
    }

    private static async Task<string> FindFunAsrServerCommandAsync(string pythonCommand)
    {
        var scripts = (await RunCommandToExitAsync(
            $"{pythonCommand} -c \"import sysconfig; print(sysconfig.get_path('scripts'))\"",
            TimeSpan.FromSeconds(10))).Trim();
        var exe = Path.Combine(scripts, "funasr-server.exe");
        if (File.Exists(exe)) return QuoteCommandPath(exe);
        var script = Path.Combine(scripts, "funasr-server");
        if (File.Exists(script)) return QuoteCommandPath(script);
        return "funasr-server";
    }

    private static string QuoteCommandPath(string path) =>
        path.Contains(' ') ? $"\"{path}\"" : path;

    private static Process StartBackgroundCommand(string command, Action<string> onOutput, string dataRoot)
    {
        var cacheRoot = Path.Combine(dataRoot, "Cache");
        var huggingFaceRoot = Path.Combine(cacheRoot, "huggingface");
        var modelScopeRoot = Path.Combine(cacheRoot, "modelscope");
        var torchRoot = Path.Combine(cacheRoot, "torch");
        Directory.CreateDirectory(huggingFaceRoot);
        Directory.CreateDirectory(modelScopeRoot);
        Directory.CreateDirectory(torchRoot);
        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c {command}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["HF_HOME"] = huggingFaceRoot;
        startInfo.Environment["MODELSCOPE_CACHE"] = modelScopeRoot;
        startInfo.Environment["TORCH_HOME"] = torchRoot;
        startInfo.Environment["XDG_CACHE_HOME"] = cacheRoot;
        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data)) onOutput(args.Data);
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data)) onOutput(args.Data);
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private void CaptureAsrServerOutput(string line)
    {
        lock (_asrServerOutputLock)
        {
            _asrServerOutput.AppendLine(line);
            if (_asrServerOutput.Length > 12_000)
                _asrServerOutput.Remove(0, _asrServerOutput.Length - 8_000);
        }
    }

    private string GetAsrServerOutputTail(int maxLength = 1_200, bool latestLineOnly = false)
    {
        lock (_asrServerOutputLock)
        {
            var lines = SanitizeConsoleText(_asrServerOutput.ToString())
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var value = latestLineOnly
                ? lines.LastOrDefault() ?? string.Empty
                : string.Join(' ', lines).Trim();
            if (value.Length <= maxLength) return value;
            if (latestLineOnly) return $"{value[..maxLength]}…";
            return value[^maxLength..];
        }
    }

    private static string SanitizeConsoleText(string value) =>
        Regex.Replace(value, "\\x1B\\[[0-?]*[ -/]*[@-~]", string.Empty);

    private static async Task<string> RunCommandToExitAsync(string command, TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(timeout);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(TrimProcessOutput(error.Length > 0 ? error : output));
        return output.Length > 0 ? output : error;
    }

    private static async Task<string> RunCommandWithProgressAsync(
        string command,
        TimeSpan timeout,
        IProgress<string> progress)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };
        var output = new StringBuilder();
        var sync = new object();
        var outputClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void HandleLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (sync) output.AppendLine(line);
            progress.Report(line);
        }

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null) outputClosed.TrySetResult();
            else HandleLine(args.Data);
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null) errorClosed.TrySetResult();
            else HandleLine(args.Data);
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new TimeoutException($"安装超过 {timeout.TotalMinutes:F0} 分钟仍未完成，已停止安装进程。请检查网络后重试。");
        }
        await Task.WhenAll(outputClosed.Task, errorClosed.Task).WaitAsync(TimeSpan.FromSeconds(5));
        string result;
        lock (sync) result = output.ToString();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(TrimProcessOutput(result));
        return result;
    }

    private static string FormatInstallationProgress(string line)
    {
        var value = line.Trim();
        if (value.Length == 0) return string.Empty;
        if (value.StartsWith("Collecting ", StringComparison.OrdinalIgnoreCase))
            return $"正在准备 {value["Collecting ".Length..]}";
        if (value.StartsWith("Downloading ", StringComparison.OrdinalIgnoreCase))
            return $"正在下载 {value["Downloading ".Length..]}";
        if (value.StartsWith("Installing collected packages", StringComparison.OrdinalIgnoreCase))
            return "正在写入并配置依赖组件…";
        if (value.StartsWith("Successfully installed", StringComparison.OrdinalIgnoreCase))
            return "依赖组件安装成功，正在执行完整性检查…";
        if (value.StartsWith("Requirement already satisfied", StringComparison.OrdinalIgnoreCase))
            return "正在检查已有依赖…";
        return value.Length <= 180 ? value : $"{value[..180]}…";
    }

    private static string TrimProcessOutput(string text)
    {
        var value = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return value.Length <= 1_200 ? value : $"…{value[^1_200..]}";
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
        StartButton.IsEnabled = false;
        StartButton.Content = "正在启动…";
        try
        {
            SaveSettings();
            ConfigureVideoTranslationService();
            var source = ((LanguageItem)SourceCombo.SelectedItem).Value;
            var target = ((LanguageItem)TargetCombo.SelectedItem).Value;
            if (source != SupportedLanguage.AutoDetect && source == target)
                throw new InvalidOperationException("源语言和目标语言不能相同。英文视频请选择 English → 简体中文，或使用自动检测 → 简体中文。");
            var concurrency = VideoConcurrencyCombo.SelectedItem is int selectedConcurrency
                ? selectedConcurrency
                : 3;
            var asrEngine = ((AsrEngineChoice)AsrEngineCombo.SelectedItem).Engine;
            if (!IsAsrEngineEnabled(asrEngine))
                throw new InvalidOperationException("当前 ASR 引擎已禁用，请先启用或切换到可用引擎。");

            if (asrEngine == SpeechRecognitionEngine.SenseVoiceSmall)
            {
                SetAsrStatus("正在自动启动并检查 SenseVoice ASR 服务…");
                if (!await StartAsrServerAsync())
                    throw new InvalidOperationException(
                        "SenseVoice ASR 服务自动启动失败。请查看页面中的启动错误；软件不会再静默改用 Whisper。");
            }

            _latestOverlayStart = TimeSpan.MinValue;
            Segments.Clear();
            ExportButton.IsEnabled = false;
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
            StartButton.IsEnabled = true;
            RefreshAsrEngineUi();
            (Owner as MainWindow)?.MinimizeForSubtitle();
        }
        catch (Exception exception)
        {
            _overlay?.Close(); _overlay = null;
            StopManagedAsrServer();
            _translationSession.Reset();
            StartButton.Content = "开始翻译";
            StartButton.IsEnabled = true;
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
            StopManagedAsrServer();
            StartAsrServerButton.Content = "启动 ASR 服务";
            _translationSession.Reset();
            StartButton.Content = "开始翻译";
            StartButton.IsEnabled = true;
            RefreshAsrEngineUi();
            SetAsrStatus("翻译已停止；由软件启动的 ASR 服务和模型内存已经释放。");
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
            .FirstOrDefault(value => SegmentsMatch(value.item.Segment, segment));
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
            .FirstOrDefault(value => SegmentsMatch(value.item.Segment, segment));
        if (existing.item is null) Segments.Add(row);
        else Segments[existing.index] = row;
        _latestOverlayStart = segment.Start;
        SubtitleList.ItemsSource = Segments;
        SubtitleList.ScrollIntoView(row);
        _overlay?.ShowSource(segment,
            BilingualCheck.IsChecked == true || MovieModeRadio.IsChecked == true);
    });

    private static bool SegmentsMatch(SubtitleSegment existing, SubtitleSegment incoming) =>
        incoming.Sequence > 0 && existing.Sequence > 0
            ? incoming.Sequence == existing.Sequence
            : incoming.Start == existing.Start;

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
            SenseVoiceUrlBox.Text = NormalizeSenseVoiceBaseUrl(settings.SenseVoiceBaseUrl);
            SenseVoiceModelBox.Text = settings.AsrConfigurationVersion < CurrentAsrConfigurationVersion
                ? "sensevoice"
                : string.IsNullOrWhiteSpace(settings.SenseVoiceModel)
                    ? "sensevoice"
                    : settings.SenseVoiceModel;
            AsrStartCommandBox.Text = string.IsNullOrWhiteSpace(settings.AsrStartCommand)
                ? DefaultAsrStartCommand
                : NormalizeSavedAsrStartCommand(settings.AsrStartCommand);
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
            AsrStartCommand = AsrStartCommandBox.Text.Trim(),
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
            OverlayLayoutVersion = CurrentOverlayLayoutVersion,
            AsrConfigurationVersion = CurrentAsrConfigurationVersion
        }));
    }

    private static string NormalizeSenseVoiceBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "http://127.0.0.1:8899/v1";
        var trimmed = value.Trim();
        return trimmed.Equals("http://127.0.0.1:10095/v1", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("http://localhost:10095/v1", StringComparison.OrdinalIgnoreCase)
            ? "http://127.0.0.1:8899/v1"
            : trimmed;
    }

    private static string NormalizeSavedAsrStartCommand(string value) =>
        value.Contains("py -3.12 -m funasr.server", StringComparison.OrdinalIgnoreCase)
            ? DefaultAsrStartCommand
            : value.Trim();

    private sealed class VideoSettings
    {
        public SpeechRecognitionEngine AsrEngine { get; init; } = SpeechRecognitionEngine.SenseVoiceSmall;
        public string? WhisperModelPath { get; init; }
        public string SenseVoiceBaseUrl { get; init; } = "http://127.0.0.1:8899/v1";
        public string SenseVoiceModel { get; init; } = "sensevoice";
        public string AsrStartCommand { get; init; } = DefaultAsrStartCommand;
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
        public int AsrConfigurationVersion { get; init; }
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

        public void Reset()
        {
            _service?.Dispose();
            _service = null;
        }

        public void Dispose()
        {
            Reset();
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

internal sealed record AsrDependencyCheckResult(
    string PythonCommand,
    IReadOnlyList<string> MissingComponents)
{
    public bool IsComplete => MissingComponents.Count == 0;
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
