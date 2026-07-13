using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using LLama;
using LLama.Common;
using LLama.Sampling;
using LLama.Transformers;
using LocalTranslator.Core.Abstractions;
using LocalTranslator.Core.Exceptions;
using LocalTranslator.Core.Models;
using LocalTranslator.Infrastructure.Configuration;

namespace LocalTranslator.Infrastructure.Services;

public sealed class LocalLlmTranslationService : ITranslationService, IDisposable
{
    private readonly AppOptions _options;
    private readonly IAppLogger _logger;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private LLamaWeights? _weights;
    private string? _loadedModelId;
    private bool _disposed;

    public LocalLlmTranslationService(AppOptions options, IAppLogger logger)
    {
        _options = options;
        _logger = logger;
        ModelManager = new LocalModelManager(options);
    }

    public LocalModelManager ModelManager { get; }

    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return new TranslationResult(string.Empty, TimeSpan.Zero);
        }

        if (request.SourceLanguage == request.TargetLanguage)
        {
            return new TranslationResult(request.Text, TimeSpan.Zero);
        }

        await _inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var activeModel = ModelManager.GetActiveModel()
                ?? throw new OfflineEngineException("尚未安装离线翻译模型，请在“本地模型管理”中点击安装。");
            await EnsureModelLoadedAsync(activeModel, cancellationToken).ConfigureAwait(false);

            var stopwatch = Stopwatch.StartNew();
            var modelParams = CreateModelParams(activeModel.FullPath);
            using var context = _weights!.CreateContext(modelParams);
            var executor = new InteractiveExecutor(context);
            var history = new ChatHistory();
            history.AddMessage(
                AuthorRole.System,
                BuildSystemPrompt(activeModel.Options, request.SourceLanguage, request.TargetLanguage));
            var session = new ChatSession(executor, history)
                .WithHistoryTransform(new PromptTemplateTransformer(_weights, withAssistant: true));
            var inferenceParams = new InferenceParams
            {
                MaxTokens = Math.Clamp(activeModel.Options.MaxOutputTokens, 32, 8192),
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = 0.3f,
                    TopK = 20,
                    TopP = 0.8f,
                    MinP = 0,
                    PresencePenalty = 1.2f,
                    Seed = 42
                }
            };

            var output = new StringBuilder();
            var prompt = request.Text;
            await foreach (var token in session.ChatAsync(
                               new ChatHistory.Message(AuthorRole.User, prompt),
                               inferenceParams,
                               cancellationToken).ConfigureAwait(false))
            {
                output.Append(token);
            }

            stopwatch.Stop();
            var translatedText = CleanOutput(output.ToString());
            if (string.IsNullOrWhiteSpace(translatedText))
            {
                throw new OfflineEngineException("离线模型返回了空译文，请尝试质量版模型。");
            }

            _logger.Info(
                $"Local LLM translation completed. Model={activeModel.Options.Id}, " +
                $"Direction={request.SourceLanguage.ToCode()}->{request.TargetLanguage.ToCode()}, " +
                $"Chars={request.Text.Length}, ElapsedMs={stopwatch.ElapsedMilliseconds}.");
            return new TranslationResult(translatedText, stopwatch.Elapsed);
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    public Task InstallModelAsync(
        string modelId,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        ModelManager.InstallAsync(modelId, progress, cancellationToken);

    public async Task UninstallModelAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        await _inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loadedModelId?.Equals(modelId, StringComparison.OrdinalIgnoreCase) == true)
            {
                _weights?.Dispose();
                _weights = null;
                _loadedModelId = null;
            }
            ModelManager.Uninstall(modelId);
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    private async Task EnsureModelLoadedAsync(
        LocalModelStatus model,
        CancellationToken cancellationToken)
    {
        if (_weights is not null && _loadedModelId == model.Options.Id)
        {
            return;
        }

        _weights?.Dispose();
        _weights = null;
        _loadedModelId = null;
        _logger.Info($"Loading local translation model: {model.Options.Id}.");
        _weights = await LLamaWeights.LoadFromFileAsync(
            CreateModelParams(model.FullPath),
            cancellationToken).ConfigureAwait(false);
        _loadedModelId = model.Options.Id;
        _logger.Info($"Local translation model loaded: {model.Options.Id}.");
    }

    private ModelParams CreateModelParams(string path) => new(path)
    {
        ContextSize = (uint)Math.Clamp(
            ModelManager.GetActiveModel()?.Options.ContextSize ?? _options.Translation.LocalLlm.ContextSize,
            512,
            32768),
        GpuLayerCount = 0,
        Threads = Math.Max(2, Environment.ProcessorCount - 2),
        BatchThreads = Math.Max(2, Environment.ProcessorCount - 2),
        UseMemorymap = true
    };

    private static string BuildSystemPrompt(
        LocalLlmModelOptions model,
        SupportedLanguage source,
        SupportedLanguage target) => model.PromptTemplate
            .Replace("{source}", source.ToDisplayName(), StringComparison.OrdinalIgnoreCase)
            .Replace("{target}", target.ToDisplayName(), StringComparison.OrdinalIgnoreCase);

    private static string CleanOutput(string output)
    {
        var withoutThinking = Regex.Replace(
            output,
            "<think>.*?</think>",
            string.Empty,
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return withoutThinking
            .Trim()
            .Trim('"', '“', '”');
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _weights?.Dispose();
        _inferenceLock.Dispose();
        ModelManager.Dispose();
    }
}
