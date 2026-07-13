using LocalTranslator.Core.Abstractions;
using LocalTranslator.Core.Exceptions;
using LocalTranslator.Core.Models;
using LocalTranslator.Infrastructure.Configuration;

namespace LocalTranslator.Infrastructure.Services;

public sealed class OfflineTranslationService(AppOptions options, IAppLogger logger) : ITranslationService
{
    public Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return Task.FromResult(new TranslationResult(string.Empty, TimeSpan.Zero));
        }

        if (request.SourceLanguage == request.TargetLanguage)
        {
            return Task.FromResult(new TranslationResult(request.Text, TimeSpan.Zero));
        }

        var sourceCode = request.SourceLanguage.ToCode();
        var targetCode = request.TargetLanguage.ToCode();
        var model = options.Translation.Models.FirstOrDefault(item =>
            item.Source.Equals(sourceCode, StringComparison.OrdinalIgnoreCase) &&
            item.Target.Equals(targetCode, StringComparison.OrdinalIgnoreCase));

        if (model is null)
        {
            throw new OfflineEngineException($"未配置 {sourceCode} → {targetCode} 的离线翻译模型。");
        }

        var modelPath = ResolveModelPath(options.ModelsRoot, model.Path);
        if (!Directory.Exists(modelPath))
        {
            throw new OfflineEngineException($"离线翻译模型尚未安装：{modelPath}");
        }

        logger.Info($"Translation model located: {sourceCode}->{targetCode}, path={modelPath}");
        throw new OfflineEngineException(
            $"已找到 {sourceCode} → {targetCode} 模型，但 ONNX 推理适配器将在下一阶段接入。");
    }

    private static string ResolveModelPath(string root, string path)
    {
        var modelsRoot = Path.IsPathRooted(root)
            ? root
            : Path.Combine(AppContext.BaseDirectory, root);
        return Path.GetFullPath(Path.Combine(modelsRoot, path));
    }
}

