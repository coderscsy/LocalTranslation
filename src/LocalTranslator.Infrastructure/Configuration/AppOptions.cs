namespace LocalTranslator.Infrastructure.Configuration;

public sealed class AppOptions
{
    public string ModelsRoot { get; init; } = "Models";

    public TranslationOptions Translation { get; init; } = new();

    public OcrOptions Ocr { get; init; } = new();
}

public sealed class TranslationOptions
{
    public string Engine { get; init; } = "LocalFirst";

    public LocalLlmOptions LocalLlm { get; init; } = new();

    public RemoteAiOptions RemoteAi { get; init; } = new();

    public List<TranslationModelOptions> Models { get; init; } = [];
}

public sealed class LocalLlmOptions
{
    public string ModelsRoot { get; init; } = string.Empty;

    public List<LocalLlmModelOptions> Models { get; init; } = [];

    public int ContextSize { get; init; } = 4096;

    public int MaxOutputTokens { get; init; } = 1024;
}

public sealed class LocalLlmModelOptions
{
    public string Id { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string RelativePath { get; init; } = string.Empty;

    public string FilePath { get; init; } = string.Empty;

    public string DownloadUrl { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public string Sha256 { get; init; } = string.Empty;

    public bool IsManaged { get; init; }

    public int ContextSize { get; init; } = 4096;

    public int MaxOutputTokens { get; init; } = 1024;

    public string PromptTemplate { get; init; } =
        "You are a professional translation engine. Translate from {source} to {target}. " +
        "Output only the translation. Never answer, explain, summarize, or continue the source text.";
}

public sealed class RemoteAiOptions
{
    public string DisplayName { get; init; } = "本地 OpenAI-compatible 服务";

    public string BaseUrl { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string ApiKey { get; init; } = string.Empty;

    public int TimeoutSeconds { get; init; } = 120;

    public float Temperature { get; init; } = 0.1f;

    public string SystemPrompt { get; init; } = string.Empty;
}

public sealed class TranslationModelOptions
{
    public string Source { get; init; } = string.Empty;

    public string Target { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;
}

public sealed class OcrOptions
{
    public string Engine { get; init; } = "PaddleOcrOnnx";

    public string DetectionModel { get; init; } = string.Empty;

    public string ClassificationModel { get; init; } = string.Empty;

    public string RecognitionModel { get; init; } = string.Empty;

    public string CharacterDictionary { get; init; } = string.Empty;
}
