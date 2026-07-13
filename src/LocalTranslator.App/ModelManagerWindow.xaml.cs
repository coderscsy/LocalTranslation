using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using LocalTranslator.Infrastructure.Configuration;
using LocalTranslator.Infrastructure.Services;

namespace LocalTranslator.App;

public partial class ModelManagerWindow : Window
{
    private static readonly SolidColorBrush InstalledBrush = new(Color.FromRgb(18, 183, 106));
    private static readonly SolidColorBrush MissingBrush = new(Color.FromRgb(247, 144, 9));
    private readonly AppOptions _options;
    private readonly LocalLlmTranslationService _localTranslationService;
    private readonly SecureTranslationProviderStore _providerStore;
    private readonly TranslationProviderRouter _providerRouter;
    private readonly string _ocrModelsRoot;
    private readonly Dictionary<string, CancellationTokenSource> _modelDownloads = [];

    public ModelManagerWindow(
        AppOptions options,
        LocalLlmTranslationService localTranslationService,
        SecureTranslationProviderStore providerStore,
        TranslationProviderRouter providerRouter)
    {
        InitializeComponent();
        _options = options;
        _localTranslationService = localTranslationService;
        _providerStore = providerStore;
        _providerRouter = providerRouter;
        _ocrModelsRoot = Path.GetFullPath(Path.IsPathRooted(options.ModelsRoot)
            ? options.ModelsRoot
            : Path.Combine(AppContext.BaseDirectory, options.ModelsRoot));
        ModelsRoot = localTranslationService.ModelManager.ModelsRoot;
        DataContext = this;
        RefreshStatus();
        Closed += (_, _) =>
        {
            foreach (var cancellation in _modelDownloads.Values)
            {
                cancellation.Cancel();
            }
        };
    }

    public string ModelsRoot { get; }

    public ObservableCollection<ModelStatusItem> OcrModels { get; } = [];

    public ObservableCollection<ModelStatusItem> TranslationModels { get; } = [];

    public ObservableCollection<LocalModelItemViewModel> OfflineModels { get; } = [];

    private void RefreshStatus()
    {
        OcrModels.Clear();
        AddOcrModel("文本检测", _options.Ocr.DetectionModel);
        AddOcrModel("方向分类", _options.Ocr.ClassificationModel);
        AddOcrModel("文字识别", _options.Ocr.RecognitionModel);
        AddOcrModel("字符字典", _options.Ocr.CharacterDictionary);

        OfflineModels.Clear();
        foreach (var model in _localTranslationService.ModelManager.GetModels())
        {
            OfflineModels.Add(new LocalModelItemViewModel(model));
        }
        var installedLocalModels = OfflineModels.Count(item => item.IsInstalled);
        OfflineSummaryText.Text = $"{installedLocalModels}/{OfflineModels.Count} 已安装";

        TranslationModels.Clear();
        var remoteAi = GetDisplayedProvider();
        var endpointConfigured = !string.IsNullOrWhiteSpace(remoteAi.BaseUrl);
        TranslationModels.Add(CreateServiceItem("Provider", remoteAi.DisplayName, true, "当前"));
        TranslationModels.Add(CreateServiceItem(
            "API 地址",
            endpointConfigured ? remoteAi.BaseUrl : "未配置",
            endpointConfigured,
            endpointConfigured ? "已配置" : "未配置"));
        TranslationModels.Add(CreateServiceItem(
            "模型选择",
            string.IsNullOrWhiteSpace(remoteAi.Model) ? "自动选择首个模型" : remoteAi.Model,
            true,
            string.IsNullOrWhiteSpace(remoteAi.Model) ? "自动" : "已配置"));

        var ocrInstalled = OcrModels.Count(item => item.IsInstalled);
        OcrSummaryText.Text = $"{ocrInstalled}/{OcrModels.Count} 已安装";
        TranslationSummaryText.Text = endpointConfigured ? "已配置 · 待测试" : "尚未配置";
    }

    private void AddOcrModel(string name, string relativePath)
    {
        var installed = !string.IsNullOrWhiteSpace(relativePath) &&
                        File.Exists(Path.GetFullPath(Path.Combine(_ocrModelsRoot, relativePath)));
        OcrModels.Add(CreateItem(name, relativePath, installed));
    }

    private ModelStatusItem CreateItem(string name, string relativePath, bool installed) =>
        new(
            name,
            string.IsNullOrWhiteSpace(relativePath) ? "未配置路径" : relativePath,
            installed,
            installed ? "已安装" : "待安装",
            installed ? InstalledBrush : MissingBrush);

    private ModelStatusItem CreateServiceItem(
        string name,
        string value,
        bool configured,
        string status) =>
        new(name, value, configured, status, configured ? InstalledBrush : MissingBrush);

    private string ResolvePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(ModelsRoot, relativePath ?? string.Empty));

    private void OpenModelsFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ModelsRoot);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{ModelsRoot}\"",
            UseShellExecute = true
        });
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshStatus();

    private void AddLocalModel_Click(object sender, RoutedEventArgs e)
    {
        var editor = new LocalModelEditorWindow { Owner = this };
        if (editor.ShowDialog() == true && editor.Model is not null)
        {
            _localTranslationService.ModelManager.AddOrUpdate(editor.Model);
            RefreshStatus();
        }
    }

    private void EditLocalModel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LocalModelItemViewModel item) return;
        var current = _localTranslationService.ModelManager.GetModels().First(model => model.Options.Id == item.Id).Options;
        var editor = new LocalModelEditorWindow(current) { Owner = this };
        if (editor.ShowDialog() == true && editor.Model is not null)
        {
            _localTranslationService.ModelManager.AddOrUpdate(editor.Model);
            RefreshStatus();
        }
    }

    private void ActivateLocalModel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LocalModelItemViewModel item) return;
        _localTranslationService.ModelManager.SetActive(item.Id);
        RefreshStatus();
    }

    private void RemoveLocalModel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LocalModelItemViewModel item) return;
        if (MessageBox.Show(this, $"移除“{item.DisplayName}”的配置？\n不会删除你自己的模型文件。", "移除配置",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _localTranslationService.ModelManager.RemoveConfiguration(item.Id);
        RefreshStatus();
    }

    private async void TestMacAi_Click(object sender, RoutedEventArgs e)
    {
        TestMacAiButton.IsEnabled = false;
        TranslationSummaryText.Text = "正在连接…";
        try
        {
            var provider = GetDisplayedProvider();
            using var service = _providerRouter.CreateOnlineService(provider);
            var models = (await service.GetAvailableModelsAsync())
                .Where(OpenAiCompatibleTranslationService.IsTextGenerationModel)
                .ToArray();
            TranslationModels.Remove(TranslationModels.FirstOrDefault(item => item.Name == "连接状态")!);
            TranslationModels.Add(CreateServiceItem(
                "连接状态",
                models.Length == 0 ? "服务在线，但没有加载模型" : $"在线 · {models.Length} 个模型",
                models.Length > 0,
                models.Length > 0 ? "连接成功" : "无模型"));
            TranslationSummaryBadge.Background = new SolidColorBrush(Color.FromRgb(236, 253, 243));
            TranslationSummaryText.Foreground = new SolidColorBrush(Color.FromRgb(2, 122, 72));
            TranslationSummaryText.Text = models.Length > 0 ? "连接成功" : "在线 · 无模型";
        }
        catch (Exception exception)
        {
            TranslationModels.Remove(TranslationModels.FirstOrDefault(item => item.Name == "连接状态")!);
            TranslationModels.Add(CreateServiceItem("连接状态", exception.Message, false, "连接失败"));
            TranslationSummaryText.Text = "连接失败";
        }
        finally
        {
            TestMacAiButton.IsEnabled = true;
        }
    }

    private void ConfigureService_Click(object sender, RoutedEventArgs e)
    {
        var provider = GetDisplayedProvider();
        var window = new ProviderSettingsWindow(
            _providerStore,
            _providerRouter,
            _localTranslationService,
            provider.Id)
        {
            Owner = this
        };
        if (window.ShowDialog() == true)
        {
            RefreshStatus();
        }
    }

    private OnlineProviderSettings GetDisplayedProvider()
    {
        var settings = _providerStore.Load();
        return settings.OnlineProviders.FirstOrDefault(item =>
                   item.Id.Equals(settings.ActiveProviderId, StringComparison.OrdinalIgnoreCase))
               ?? settings.OnlineProviders.First(item => item.Id == "local-service");
    }

    private async void InstallOfflineModel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LocalModelItemViewModel model)
        {
            return;
        }

        model.BeginOperation("准备下载…");
        var cancellation = new CancellationTokenSource();
        _modelDownloads[model.Id] = cancellation;
        var progress = new Progress<ModelDownloadProgress>(value =>
            model.ReportProgress(value.Percentage, $"{value.Stage} {value.Percentage:F0}%"));
        try
        {
            await _localTranslationService.InstallModelAsync(model.Id, progress, cancellation.Token);
            RefreshStatus();
        }
        catch (OperationCanceledException)
        {
            RefreshStatus();
        }
        catch (Exception exception)
        {
            model.EndOperation($"安装失败：{exception.Message}");
            MessageBox.Show(
                this,
                exception.Message,
                "模型安装失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _modelDownloads.Remove(model.Id);
            cancellation.Dispose();
        }
    }

    private void CancelOfflineModelInstall_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LocalModelItemViewModel model &&
            _modelDownloads.TryGetValue(model.Id, out var cancellation))
        {
            model.ReportProgress(model.Progress, "正在取消…");
            cancellation.Cancel();
        }
    }

    private async void UninstallOfflineModel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LocalModelItemViewModel model)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"确定卸载 {model.DisplayName}？\n模型文件将从本机删除，之后可以重新下载。",
            "卸载离线模型",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        model.BeginOperation("正在卸载…");
        try
        {
            await _localTranslationService.UninstallModelAsync(model.Id);
            RefreshStatus();
        }
        catch (Exception exception)
        {
            model.EndOperation($"卸载失败：{exception.Message}");
            MessageBox.Show(
                this,
                exception.Message,
                "模型卸载失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed record ModelStatusItem(
    string Name,
    string RelativePath,
    bool IsInstalled,
    string StatusText,
    System.Windows.Media.Brush StatusBrush);

public sealed class LocalModelItemViewModel : INotifyPropertyChanged
{
    private bool _isBusy;
    private double _progress;
    private string _statusText;

    public LocalModelItemViewModel(LocalModelStatus status)
    {
        Id = status.Options.Id;
        DisplayName = status.Options.DisplayName;
        Description = status.Options.Description;
        SizeText = FormatBytes(status.Options.SizeBytes);
        IsInstalled = status.IsInstalled;
        IsActive = status.IsActive;
        IsManaged = status.Options.IsManaged;
        _statusText = status.IsActive ? "使用中" : status.IsInstalled ? "已安装" : "未安装";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public string SizeText { get; }
    public bool IsInstalled { get; }
    public bool IsActive { get; }
    public bool IsManaged { get; }
    public System.Windows.Media.Brush StatusBrush => IsInstalled
        ? new SolidColorBrush(Color.FromRgb(18, 183, 106))
        : new SolidColorBrush(Color.FromRgb(247, 144, 9));
    public Visibility InstallVisibility => IsManaged && !IsInstalled && !IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UninstallVisibility => IsManaged && IsInstalled && !IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActivateVisibility => IsInstalled && !IsActive && !IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProgressVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            Notify();
            Notify(nameof(InstallVisibility));
            Notify(nameof(UninstallVisibility));
            Notify(nameof(ProgressVisibility));
        }
    }

    public double Progress
    {
        get => _progress;
        private set
        {
            _progress = value;
            Notify();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            Notify();
        }
    }

    public void BeginOperation(string status)
    {
        Progress = 0;
        StatusText = status;
        IsBusy = true;
    }

    public void ReportProgress(double progress, string status)
    {
        Progress = progress;
        StatusText = status;
    }

    public void EndOperation(string status)
    {
        StatusText = status;
        IsBusy = false;
    }

    private static string FormatBytes(long bytes) => $"{bytes / 1024d / 1024d:F0} MB";

    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
