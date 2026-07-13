using System.Buffers;
using System.Security.Cryptography;
using LocalTranslator.Core.Exceptions;
using LocalTranslator.Infrastructure.Configuration;

namespace LocalTranslator.Infrastructure.Services;

public sealed class SpeechModelManager : IDisposable
{
    public const string DefaultModelName = "Whisper Small Q5_1 · 中文均衡版";
    public const long DefaultModelSize = 190_085_487;
    public const string DefaultModelSha256 = "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb";
    public const string DefaultModelUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin?download=true";
    public const string ParakeetModelName = "Meetily Parakeet TDT 0.6B V3 INT8";

    private static readonly ParakeetFile[] ParakeetFiles =
    [
        new("encoder-model.int8.onnx", 652_183_999),
        new("decoder_joint-model.int8.onnx", 18_202_004),
        new("nemo128.onnx", 139_764),
        new("vocab.txt", 93_939)
    ];
    private const string ParakeetBaseUrl =
        "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main";

    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    public SpeechModelManager(AppOptions options)
    {
        ModelsRoot = Path.Combine(AppStoragePaths.ResolveDataRoot(options), "Models", "speech");
        DefaultModelPath = Path.Combine(ModelsRoot, "whisper-small-q5_1", "ggml-small-q5_1.bin");
        LegacyModelPath = Path.Combine(ModelsRoot, "whisper-base-q5_1", "ggml-base-q5_1.bin");
        ParakeetModelDirectory = Path.Combine(ModelsRoot, "parakeet-tdt-0.6b-v3-int8");
    }

    public string ModelsRoot { get; }
    public string DefaultModelPath { get; }
    public string LegacyModelPath { get; }
    public string ParakeetModelDirectory { get; }
    public bool IsDefaultModelInstalled => File.Exists(DefaultModelPath) &&
                                           new FileInfo(DefaultModelPath).Length == DefaultModelSize;
    public bool IsParakeetModelInstalled => IsValidParakeetModel(ParakeetModelDirectory);

    public async Task InstallParakeetAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ParakeetModelDirectory);
        var total = ParakeetFiles.Sum(file => file.Size);
        long completed = 0;
        foreach (var modelFile in ParakeetFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(ParakeetModelDirectory, modelFile.Name);
            if (File.Exists(destination) && new FileInfo(destination).Length == modelFile.Size)
            {
                completed += modelFile.Size;
                progress?.Report(new ModelDownloadProgress(
                    completed, total, $"已校验 {modelFile.Name}"));
                continue;
            }

            var temporary = destination + ".download";
            try
            {
                using var response = await _httpClient.GetAsync(
                    $"{ParakeetBaseUrl}/{modelFile.Name}?download=true",
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var output = new FileStream(
                    temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                    1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
                long received = 0;
                long lastReported = 0;
                try
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                            .ConfigureAwait(false);
                        received += read;
                        if (received - lastReported >= 1024 * 1024)
                        {
                            lastReported = received;
                            progress?.Report(new ModelDownloadProgress(
                                completed + received, total, $"正在下载 {modelFile.Name}"));
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                await output.DisposeAsync().ConfigureAwait(false);
                if (received != modelFile.Size)
                    throw new OfflineEngineException(
                        $"Parakeet 文件 {modelFile.Name} 大小不正确：应为 {modelFile.Size}，实际为 {received}。");
                File.Move(temporary, destination, true);
                completed += modelFile.Size;
                progress?.Report(new ModelDownloadProgress(
                    completed, total, $"已安装 {modelFile.Name}"));
            }
            catch
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                throw;
            }
        }

        ValidateParakeetModel(ParakeetModelDirectory);
        progress?.Report(new ModelDownloadProgress(total, total, "Parakeet 安装完成"));
    }

    public void UninstallParakeet()
    {
        var fullPath = Path.GetFullPath(ParakeetModelDirectory);
        var root = Path.GetFullPath(ModelsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new OfflineEngineException("拒绝删除语音模型目录之外的文件。");
        if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
    }

    public static void ValidateParakeetModel(string modelDirectory)
    {
        foreach (var file in ParakeetFiles)
        {
            var path = Path.Combine(modelDirectory, file.Name);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size)
                throw new OfflineEngineException($"Meetily Parakeet 模型不完整：{file.Name} 缺失或大小错误。");
        }
    }

    private static bool IsValidParakeetModel(string modelDirectory)
    {
        try
        {
            ValidateParakeetModel(modelDirectory);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task InstallDefaultAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DefaultModelPath)!);
        var temporaryPath = DefaultModelPath + ".download";
        try
        {
            using var response = await _httpClient.GetAsync(
                DefaultModelUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? DefaultModelSize;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            long received = 0;
            try
            {
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    progress?.Report(new ModelDownloadProgress(received, total, "正在下载"));
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            await output.DisposeAsync().ConfigureAwait(false);
            if (received != DefaultModelSize)
                throw new OfflineEngineException($"默认语音模型大小不正确：应为 {DefaultModelSize}，实际为 {received}。");

            progress?.Report(new ModelDownloadProgress(received, total, "正在校验"));
            string hash;
            await using (var stream = File.OpenRead(temporaryPath))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!hash.Equals(DefaultModelSha256, StringComparison.OrdinalIgnoreCase))
                throw new OfflineEngineException("默认语音模型 SHA-256 校验失败，已拒绝安装。");

            File.Move(temporaryPath, DefaultModelPath, true);
            RemoveLegacyManagedModel();
            progress?.Report(new ModelDownloadProgress(received, total, "安装完成"));
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }

    public void UninstallDefault()
    {
        var fullPath = Path.GetFullPath(DefaultModelPath);
        var root = Path.GetFullPath(ModelsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new OfflineEngineException("拒绝删除语音模型目录之外的文件。");
        if (File.Exists(fullPath)) File.Delete(fullPath);
        var directory = Path.GetDirectoryName(fullPath)!;
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }

    private void RemoveLegacyManagedModel()
    {
        var legacyDirectory = Path.GetDirectoryName(LegacyModelPath)!;
        if (File.Exists(LegacyModelPath)) File.Delete(LegacyModelPath);
        if (Directory.Exists(legacyDirectory) && !Directory.EnumerateFileSystemEntries(legacyDirectory).Any())
            Directory.Delete(legacyDirectory);
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed record ParakeetFile(string Name, long Size);
}
