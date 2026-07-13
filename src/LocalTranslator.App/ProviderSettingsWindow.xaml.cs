using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LocalTranslator.Infrastructure.Services;

namespace LocalTranslator.App;

public partial class ProviderSettingsWindow : Window
{
    private readonly SecureTranslationProviderStore _store;
    private readonly TranslationProviderRouter _router;
    private readonly LocalLlmTranslationService _localService;
    private TranslationProviderSettings _settings;
    private bool _isLoading;

    public ProviderSettingsWindow(
        SecureTranslationProviderStore store,
        TranslationProviderRouter router,
        LocalLlmTranslationService localService,
        string? initialProviderId = null)
    {
        InitializeComponent();
        _store = store;
        _router = router;
        _localService = localService;
        _settings = store.Load();
        Providers = BuildProviderChoices(_settings);
        DataContext = this;

        _isLoading = true;
        ProviderComboBox.SelectedItem = Providers.FirstOrDefault(item =>
            item.Id.Equals(initialProviderId ?? _settings.ActiveProviderId, StringComparison.OrdinalIgnoreCase))
            ?? Providers[0];
        _isLoading = false;
        LoadSelectedProvider();
    }

    public ObservableCollection<ProviderChoice> Providers { get; }
    public ObservableCollection<string> AvailableModels { get; } = [];

    private static ObservableCollection<ProviderChoice> BuildProviderChoices(
        TranslationProviderSettings settings)
    {
        var result = new ObservableCollection<ProviderChoice>
        {
            new("local", "自己的本地 GGUF", ProviderKind.Local)
        };
        foreach (var provider in settings.OnlineProviders)
        {
            result.Add(new ProviderChoice(provider.Id, provider.DisplayName, ProviderKind.Online));
        }
        return result;
    }

    private void ProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoading)
        {
            LoadSelectedProvider();
        }
    }

    private void LoadSelectedProvider()
    {
        if (ProviderComboBox.SelectedItem is not ProviderChoice choice)
        {
            return;
        }

        ConnectionStatusText.Text = "尚未测试连接";
        var editable = choice.Kind == ProviderKind.Online;
        BaseUrlTextBox.IsEnabled = editable;
        ModelTextBox.IsEnabled = editable;
        ApiKeyPasswordBox.IsEnabled = editable;
        ApiKeyPasswordBox.Password = string.Empty;

        if (choice.Kind == ProviderKind.Local)
        {
            var active = _localService.ModelManager.GetActiveModel();
            ProviderDescriptionText.Text = "Windows 本机离线推理";
            BaseUrlTextBox.Text = _localService.ModelManager.ModelsRoot;
            ModelTextBox.Text = active?.Options.DisplayName ?? "未安装模型";
            return;
        }

        var provider = _settings.OnlineProviders.First(item => item.Id == choice.Id);
        ProviderDescriptionText.Text = provider.DisplayName;
        BaseUrlTextBox.Text = provider.BaseUrl;
        AvailableModels.Clear();
        if (!string.IsNullOrWhiteSpace(provider.Model)) AvailableModels.Add(provider.Model);
        ModelTextBox.Text = provider.Model;
        ModelTextBox.SelectedItem = AvailableModels.FirstOrDefault(item =>
            item.Equals(provider.Model, StringComparison.OrdinalIgnoreCase));
        ApiKeyPasswordBox.Password = provider.ApiKey;
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (ProviderComboBox.SelectedItem is not ProviderChoice choice)
        {
            return;
        }

        TestConnectionButton.IsEnabled = false;
        ConnectionStatusText.Text = "正在测试…";
        try
        {
            if (choice.Kind == ProviderKind.Local)
            {
                var model = _localService.ModelManager.GetActiveModel()
                    ?? throw new InvalidOperationException("尚未安装离线翻译模型。");
                ConnectionStatusText.Text = $"可用：{model.Options.DisplayName}";
            }
            else
            {
                var provider = ReadOnlineProvider(choice);
                using var service = _router.CreateOnlineService(provider);
                var models = (await service.GetAvailableModelsAsync())
                    .Where(OpenAiCompatibleTranslationService.IsTextGenerationModel)
                    .ToArray();
                if (models.Length == 0)
                    throw new InvalidOperationException("服务在线，但 /models 没有返回可用的文本生成模型。");

                AvailableModels.Clear();
                foreach (var model in models) AvailableModels.Add(model);

                var selectedModel = GetSelectedModelName();
                if (string.IsNullOrWhiteSpace(selectedModel) ||
                    !models.Contains(selectedModel, StringComparer.OrdinalIgnoreCase))
                    selectedModel = OpenAiCompatibleTranslationService.SelectPreferredModel(models);
                ModelTextBox.Text = selectedModel;
                ModelTextBox.SelectedItem = models.First(item =>
                    item.Equals(selectedModel, StringComparison.OrdinalIgnoreCase));
                BaseUrlTextBox.Text = OpenAiCompatibleTranslationService.NormalizeBaseUrl(BaseUrlTextBox.Text);
                ConnectionStatusText.Text = $"连接成功 · 发现 {models.Length} 个文本模型 · 已选择 {selectedModel}";
            }
        }
        catch (Exception exception)
        {
            ConnectionStatusText.Text = $"连接失败：{exception.Message}";
        }
        finally
        {
            TestConnectionButton.IsEnabled = true;
        }
    }

    private void ModelTextBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelTextBox.SelectedItem is string selectedModel)
            ModelTextBox.Text = selectedModel;
    }

    private string GetSelectedModelName() =>
        ModelTextBox.SelectedItem is string selectedModel && !string.IsNullOrWhiteSpace(selectedModel)
            ? selectedModel.Trim()
            : ModelTextBox.Text.Trim();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ProviderComboBox.SelectedItem is not ProviderChoice choice)
        {
            return;
        }

        try
        {
            if (choice.Kind == ProviderKind.Online)
            {
                var updated = ReadOnlineProvider(choice);
                var index = _settings.OnlineProviders.FindIndex(item => item.Id == choice.Id);
                _settings.OnlineProviders[index] = updated;
            }

            _settings.ActiveProviderId = choice.Id;
            _store.Save(_settings);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Provider 配置不完整",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private OnlineProviderSettings ReadOnlineProvider(ProviderChoice choice)
    {
        if (string.IsNullOrWhiteSpace(BaseUrlTextBox.Text))
        {
            throw new InvalidOperationException("API Base URL 不能为空。");
        }
        return new OnlineProviderSettings(
            choice.Id,
            choice.DisplayName,
            OpenAiCompatibleTranslationService.NormalizeBaseUrl(BaseUrlTextBox.Text),
            GetSelectedModelName(),
            ApiKeyPasswordBox.Password);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed record ProviderChoice(string Id, string DisplayName, ProviderKind Kind);

public enum ProviderKind
{
    Local,
    Online
}
