using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using LocalTranslator.Core.Abstractions;
using LocalTranslator.Infrastructure.Configuration;
using LocalTranslator.Infrastructure.Services;

namespace LocalTranslator.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly IAppLogger _logger;
    private readonly AppOptions _options;
    private readonly LocalLlmTranslationService _localTranslationService;
    private readonly SecureTranslationProviderStore _providerStore;
    private readonly TranslationProviderRouter _providerRouter;
    private readonly System.Windows.Forms.NotifyIcon _trayIcon;
    private readonly System.Drawing.Icon _trayDrawingIcon;
    private VideoSubtitleWindow? _videoSubtitleWindow;
    private AppWindowPreferences _windowPreferences;
    private bool _trayTipShown;
    private bool _allowClose;
    private bool _exitInProgress;

    public MainWindow(
        MainViewModel viewModel,
        IScreenCaptureService screenCaptureService,
        IAppLogger logger,
        AppOptions options,
        LocalLlmTranslationService localTranslationService,
        SecureTranslationProviderStore providerStore,
        TranslationProviderRouter providerRouter)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _screenCaptureService = screenCaptureService;
        _logger = logger;
        _options = options;
        _localTranslationService = localTranslationService;
        _providerStore = providerStore;
        _providerRouter = providerRouter;
        _windowPreferences = AppWindowPreferences.Load();
        DataContext = viewModel;
        Loaded += (_, _) =>
        {
            RefreshTranslationProviderSummary();
            SourceTextBox.Focus();
        };
        _trayDrawingIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
                           ?? System.Drawing.SystemIcons.Application;
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _trayDrawingIcon,
            Text = "Local Translator · 视频字幕运行中",
            Visible = true,
            ContextMenuStrip = CreateTrayMenu()
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        Closing += MainWindow_Closing;
        Closed += (_, _) =>
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            if (!ReferenceEquals(_trayDrawingIcon, System.Drawing.SystemIcons.Application))
                _trayDrawingIcon.Dispose();
        };
    }

    private async void Translate_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.TranslateTextAsync();

    private void SwapLanguages_Click(object sender, RoutedEventArgs e) =>
        _viewModel.SwapLanguages();

    private void ClearSource_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ClearText();
        SourceTextBox.Focus();
    }

    private void PasteSource_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SetSourceText(Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty);
        SourceTextBox.Focus();
        SourceTextBox.CaretIndex = SourceTextBox.Text.Length;
    }

    private void CopyResult_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_viewModel.TranslatedText))
        {
            _viewModel.SetStatus("当前没有可复制的译文。", isError: false);
            return;
        }

        Clipboard.SetText(_viewModel.TranslatedText);
        _viewModel.SetStatus("译文已复制到剪贴板。", isError: false);
    }

    private async void ScreenshotTranslate_Click(object sender, RoutedEventArgs e) =>
        await StartScreenshotTranslationAsync();

    private void History_Click(object sender, RoutedEventArgs e) =>
        _viewModel.SetStatus("翻译历史将在本地数据库阶段接入。", isError: false);

    private void VideoSubtitle_Click(object sender, RoutedEventArgs e)
    {
        if (_videoSubtitleWindow is not null)
        {
            _videoSubtitleWindow.Show();
            _videoSubtitleWindow.Activate();
            return;
        }

        _videoSubtitleWindow = new VideoSubtitleWindow(_providerStore, _providerRouter, _logger, _options) { Owner = this };
        _videoSubtitleWindow.Closed += (_, _) => _videoSubtitleWindow = null;
        _videoSubtitleWindow.Show();
    }

    public void MinimizeToTray()
    {
        _videoSubtitleWindow?.Hide();
        Hide();
        WindowState = WindowState.Normal;
        if (_trayTipShown) return;
        _trayTipShown = true;
        _trayIcon.ShowBalloonTip(2500, "Local Translator",
            "字幕继续在后台运行。双击托盘图标可恢复设置界面。",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    public void MinimizeForSubtitle()
    {
        _videoSubtitleWindow?.Hide();
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Minimized;
    }

    public void RestoreFromTray()
    {
        ShowInTaskbar = true;
        BringWindowToFront(this);
        if (_videoSubtitleWindow is not null)
        {
            BringWindowToFront(_videoSubtitleWindow);
        }
    }

    private System.Windows.Forms.ContextMenuStrip CreateTrayMenu()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var restore = new System.Windows.Forms.ToolStripMenuItem("打开主界面");
        restore.Click += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        var toggleOverlay = new System.Windows.Forms.ToolStripMenuItem("切换字幕鼠标穿透  Ctrl+Shift+F8");
        toggleOverlay.Click += (_, _) => Dispatcher.Invoke(() => _videoSubtitleWindow?.ToggleOverlayInteraction());
        var closeBehavior = new System.Windows.Forms.ToolStripMenuItem("关闭按钮行为设置…");
        closeBehavior.Click += (_, _) => Dispatcher.Invoke(ResetCloseBehaviorPreference);
        var exit = new System.Windows.Forms.ToolStripMenuItem("退出 Local Translator");
        exit.Click += (_, _) => Dispatcher.BeginInvoke(new Action(() => _ = ExitApplicationAsync()));
        menu.Items.Add(restore);
        menu.Items.Add(toggleOverlay);
        menu.Items.Add(closeBehavior);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exit);
        return menu;
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_exitInProgress) return;

        var action = _windowPreferences.CloseAction;
        if (action == AppCloseAction.Ask)
        {
            var dialog = new CloseBehaviorDialog { Owner = this };
            if (dialog.ShowDialog() != true) return;
            action = dialog.SelectedAction;
            if (dialog.RememberChoice)
            {
                _windowPreferences = new AppWindowPreferences { CloseAction = action };
                _windowPreferences.Save();
            }
        }

        if (action == AppCloseAction.MinimizeToTray)
        {
            MinimizeToTray();
            return;
        }

        await ExitApplicationAsync();
    }

    private void ResetCloseBehaviorPreference()
    {
        _windowPreferences = new AppWindowPreferences { CloseAction = AppCloseAction.Ask };
        _windowPreferences.Save();
        _trayIcon.ShowBalloonTip(1800, "Local Translator",
            "关闭按钮已恢复为每次询问。", System.Windows.Forms.ToolTipIcon.Info);
    }

    private async Task ExitApplicationAsync()
    {
        if (_exitInProgress) return;
        _exitInProgress = true;
        try
        {
            if (_videoSubtitleWindow is not null)
                await _videoSubtitleWindow.ShutdownAsync();
        }
        catch (Exception exception)
        {
            _logger.Error("Stopping video subtitle during application exit failed.", exception);
        }
        finally
        {
            _allowClose = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private static void BringWindowToFront(Window window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        var handle = new WindowInteropHelper(window).Handle;
        ShowWindow(handle, 9);
        window.Topmost = true;
        window.Activate();
        window.Focus();
        SetForegroundWindow(handle);
        _ = window.Dispatcher.BeginInvoke(new Action(() => window.Topmost = false));
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    private void ManageModels_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = new ModelManagerWindow(
                _options,
                _localTranslationService,
                _providerStore,
                _providerRouter)
            {
                Owner = this
            };
            window.ShowDialog();
            RefreshTranslationProviderSummary();
            _viewModel.SetStatus("已刷新本地模型状态。", isError: false);
        }
        catch (Exception exception)
        {
            _logger.Error("Opening model manager failed.", exception);
            _viewModel.SetStatus($"模型管理器打开失败：{exception.Message}", isError: true);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = new ProviderSettingsWindow(
                _providerStore,
                _providerRouter,
                _localTranslationService)
            {
                Owner = this
            };
            window.ShowDialog();
            RefreshTranslationProviderSummary();
            _viewModel.SetStatus("翻译 Provider 设置已更新。", isError: false);
        }
        catch (Exception exception)
        {
            _logger.Error("Opening provider settings failed.", exception);
            _viewModel.SetStatus($"Provider 设置打开失败：{exception.Message}", isError: true);
        }
    }

    private void Favorite_Click(object sender, RoutedEventArgs e) =>
        _viewModel.SetStatus("收藏功能将在本地历史记录阶段接入。", isError: false);

    private void RefreshTranslationProviderSummary()
    {
        var settings = _providerStore.Load();
        if (settings.ActiveProviderId.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            TranslationProviderTitleText.Text = "当前启用：自己的本地 GGUF";
            TranslationProviderDetailText.Text = "普通文本翻译使用 Windows 本机离线模型；视频字幕可在视频页面单独选择同传模型。";
            return;
        }

        var provider = settings.OnlineProviders.FirstOrDefault(item =>
            item.Id.Equals(settings.ActiveProviderId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            TranslationProviderTitleText.Text = "当前启用：未选择翻译模型";
            TranslationProviderDetailText.Text = "请点击“切换翻译模型”选择自己的本地服务、局域网服务或在线 Provider。";
            return;
        }

        var model = string.IsNullOrWhiteSpace(provider.Model) ? "未选择模型" : provider.Model;
        var endpoint = string.IsNullOrWhiteSpace(provider.BaseUrl) ? "未配置地址" : provider.BaseUrl;
        TranslationProviderTitleText.Text = $"当前启用：{provider.DisplayName}";
        TranslationProviderDetailText.Text = $"模型：{model} · 地址：{endpoint}";
    }

    private async void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await _viewModel.TranslateTextAsync();
            return;
        }

        if (e.Key == Key.S &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            _viewModel.SwapLanguages();
        }
    }

    private async Task StartScreenshotTranslationAsync()
    {
        try
        {
            Hide();
            await Task.Delay(160);

            var selectionWindow = new RegionSelectionWindow();
            var selected = selectionWindow.ShowDialog() == true;
            var region = selectionWindow.SelectedRegion;

            if (!selected || !region.IsValid)
            {
                _viewModel.SetStatus("已取消截图。", isError: false);
                return;
            }

            var png = await _screenCaptureService.CapturePngAsync(region);
            _logger.Info(
                $"Screenshot captured. X={region.X}, Y={region.Y}, " +
                $"Width={region.Width}, Height={region.Height}, Bytes={png.Length}.");
            await _viewModel.TranslateScreenshotAsync(png);
        }
        catch (Exception exception)
        {
            _logger.Error("Screenshot translation failed.", exception);
            _viewModel.SetStatus($"截图翻译失败：{exception.Message}", isError: true);
        }
        finally
        {
            Show();
            Activate();
        }
    }
}
