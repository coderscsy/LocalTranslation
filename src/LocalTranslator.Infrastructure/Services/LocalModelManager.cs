using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using LocalTranslator.Core.Exceptions;
using LocalTranslator.Infrastructure.Configuration;

namespace LocalTranslator.Infrastructure.Services;

public sealed class LocalModelManager : IDisposable
{
    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly object _sync = new();
    private readonly string _registryPath;
    private LocalModelRegistry _registry;

    public LocalModelManager(AppOptions options)
    {
        var configuredRoot = options.Translation.LocalLlm.ModelsRoot;
        var dataRoot = AppStoragePaths.ResolveDataRoot(options);
        ModelsRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(dataRoot, "Models")
            : Environment.ExpandEnvironmentVariables(configuredRoot));
        Directory.CreateDirectory(ModelsRoot);
        _registryPath = Path.Combine(Path.GetDirectoryName(ModelsRoot)!, "local-models.json");
        _registry = LoadRegistry();
        MergeBuiltInModels(options.Translation.LocalLlm.Models);
    }

    public string ModelsRoot { get; }

    public IReadOnlyList<LocalModelStatus> GetModels()
    {
        lock (_sync)
        {
            return _registry.Models.Select(model =>
            {
                var path = ResolveModelPath(model);
                var installed = File.Exists(path) && (model.SizeBytes <= 0 || new FileInfo(path).Length == model.SizeBytes);
                return new LocalModelStatus(model, path, installed,
                    installed && model.Id.Equals(_registry.ActiveModelId, StringComparison.OrdinalIgnoreCase));
            }).ToArray();
        }
    }

    public LocalModelStatus? GetActiveModel() => GetModels().FirstOrDefault(item => item.IsActive)
        ?? GetModels().FirstOrDefault(item => item.IsInstalled);

    public void AddOrUpdate(LocalLlmModelOptions model)
    {
        if (string.IsNullOrWhiteSpace(model.Id) || string.IsNullOrWhiteSpace(model.DisplayName))
            throw new OfflineEngineException("模型名称不能为空。");
        if (string.IsNullOrWhiteSpace(model.FilePath) && string.IsNullOrWhiteSpace(model.RelativePath))
            throw new OfflineEngineException("请选择 GGUF 模型文件，或配置托管下载路径。");
        lock (_sync)
        {
            var index = _registry.Models.FindIndex(item => item.Id.Equals(model.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) _registry.Models[index] = model; else _registry.Models.Add(model);
            if (string.IsNullOrWhiteSpace(_registry.ActiveModelId)) _registry.ActiveModelId = model.Id;
            SaveRegistry();
        }
    }

    public void SetActive(string modelId)
    {
        var model = GetModels().FirstOrDefault(item => item.Options.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new OfflineEngineException("找不到本地模型配置。");
        if (!model.IsInstalled) throw new OfflineEngineException("模型文件不存在，请先修正文件路径。");
        lock (_sync) { _registry.ActiveModelId = modelId; SaveRegistry(); }
    }

    public void RemoveConfiguration(string modelId)
    {
        lock (_sync)
        {
            _registry.Models.RemoveAll(item => item.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
            if (_registry.ActiveModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase))
                _registry.ActiveModelId = _registry.Models.FirstOrDefault()?.Id ?? string.Empty;
            SaveRegistry();
        }
    }

    public async Task InstallAsync(string modelId, IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var model = FindModel(modelId);
        if (!model.IsManaged || string.IsNullOrWhiteSpace(model.DownloadUrl))
            throw new OfflineEngineException("这个配置引用用户自己的模型文件，不支持自动下载。");
        var targetPath = ResolveModelPath(model);
        var temporaryPath = targetPath + ".download";
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        try
        {
            using var response = await _httpClient.GetAsync(model.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? model.SizeBytes;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            long received = 0;
            try
            {
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    received += read;
                    progress?.Report(new ModelDownloadProgress(received, total, "正在下载"));
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            await output.FlushAsync(cancellationToken);
            if (model.SizeBytes > 0 && received != model.SizeBytes) throw new OfflineEngineException("下载文件大小与配置不一致。");
            if (!string.IsNullOrWhiteSpace(model.Sha256))
            {
                progress?.Report(new ModelDownloadProgress(received, total, "正在校验"));
                await using var stream = File.OpenRead(temporaryPath);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
                if (!hash.Equals(model.Sha256, StringComparison.OrdinalIgnoreCase)) throw new OfflineEngineException("模型 SHA-256 校验失败。");
            }
            File.Move(temporaryPath, targetPath, true);
        }
        catch { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); throw; }
    }

    public void Uninstall(string modelId)
    {
        var model = FindModel(modelId);
        if (!model.IsManaged) { RemoveConfiguration(modelId); return; }
        var path = ResolveModelPath(model);
        EnsureInsideManagedRoot(path);
        if (File.Exists(path)) File.Delete(path);
    }

    private LocalLlmModelOptions FindModel(string id) => _registry.Models.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new OfflineEngineException($"未知的本地模型：{id}");

    private string ResolveModelPath(LocalLlmModelOptions model) => !string.IsNullOrWhiteSpace(model.FilePath)
        ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(model.FilePath))
        : ResolveManagedPath(model.RelativePath);

    private string ResolveManagedPath(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(ModelsRoot, relativePath));
        EnsureInsideManagedRoot(path);
        return path;
    }

    private void EnsureInsideManagedRoot(string path)
    {
        var prefix = ModelsRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new OfflineEngineException("托管模型路径超出允许目录。");
    }

    private LocalModelRegistry LoadRegistry()
    {
        try { return File.Exists(_registryPath) ? JsonSerializer.Deserialize<LocalModelRegistry>(File.ReadAllText(_registryPath)) ?? new() : new(); }
        catch { return new(); }
    }

    private void MergeBuiltInModels(IEnumerable<LocalLlmModelOptions> builtInModels)
    {
        var changed = false;
        foreach (var model in builtInModels)
        {
            if (_registry.Models.Any(item => item.Id.Equals(model.Id, StringComparison.OrdinalIgnoreCase))) continue;
            _registry.Models.Add(model);
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(_registry.ActiveModelId) && _registry.Models.Count > 0)
        {
            _registry.ActiveModelId = _registry.Models[0].Id;
            changed = true;
        }
        if (changed) SaveRegistry();
    }

    private void SaveRegistry()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_registryPath)!);
        File.WriteAllText(_registryPath, JsonSerializer.Serialize(_registry, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Dispose() => _httpClient.Dispose();
}

public sealed class LocalModelRegistry { public string ActiveModelId { get; set; } = string.Empty; public List<LocalLlmModelOptions> Models { get; set; } = []; }
public sealed record LocalModelStatus(LocalLlmModelOptions Options, string FullPath, bool IsInstalled, bool IsActive);
public sealed record ModelDownloadProgress(long BytesReceived, long TotalBytes, string Stage) { public double Percentage => TotalBytes <= 0 ? 0 : BytesReceived * 100d / TotalBytes; }
