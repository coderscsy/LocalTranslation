using System.IO;
using System.Windows;
using LocalTranslator.Infrastructure.Configuration;
using LocalTranslator.Infrastructure.Services;

namespace LocalTranslator.App;

public partial class App : System.Windows.Application
{
    private OfflineOcrService? _ocrService;
    private LocalLlmTranslationService? _localTranslationService;
    private SecureTranslationProviderStore? _providerStore;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        FileAppLogger? logger = null;

        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            var localConfigPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LocalTranslator",
                "appsettings.local.json");
            var options = AppOptionsLoader.Load(configPath, localConfigPath);
            logger = new FileAppLogger(options);
            _localTranslationService = new LocalLlmTranslationService(options, logger);
            _providerStore = new SecureTranslationProviderStore(options);
            var translationService = new TranslationProviderRouter(
                _localTranslationService,
                _providerStore,
                logger);
            _ocrService = new OfflineOcrService(options, logger);
            var screenCaptureService = new DesktopScreenCaptureService();
            var viewModel = new MainViewModel(translationService, _ocrService, logger);

            var window = new MainWindow(
                viewModel,
                screenCaptureService,
                logger,
                options,
                _localTranslationService,
                _providerStore,
                translationService);
            MainWindow = window;
            window.Show();
            logger.Info("Application started.");
        }
        catch (Exception exception)
        {
            logger?.Error("Application startup failed.", exception);
            MessageBox.Show(
                $"程序启动失败：{exception.Message}",
                "Local Translator",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ocrService?.Dispose();
        _localTranslationService?.Dispose();
        base.OnExit(e);
    }
}
