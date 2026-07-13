using LocalTranslator.Core.Abstractions;
using LocalTranslator.Core.Models;
using LocalTranslator.Infrastructure.Configuration;
using LocalTranslator.Infrastructure.Services;

var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var baseConfig = Path.Combine(repositoryRoot, "src", "LocalTranslator.App", "appsettings.json");
var localConfig = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "LocalTranslator",
    "appsettings.local.json");
var options = AppOptionsLoader.Load(baseConfig, localConfig);

using var service = new LocalLlmTranslationService(options, new ConsoleLogger());
var result = await service.TranslateAsync(new TranslationRequest(
    "The offline translation model is working correctly.",
    SupportedLanguage.English,
    SupportedLanguage.ChineseSimplified));

Console.WriteLine();
Console.WriteLine($"TRANSLATION={result.Text}");
Console.WriteLine($"ELAPSED_MS={result.Elapsed.TotalMilliseconds:F0}");
if (string.IsNullOrWhiteSpace(result.Text))
{
    throw new InvalidOperationException("Local LLM translation returned empty text.");
}

file sealed class ConsoleLogger : IAppLogger
{
    public void Info(string message) => Console.WriteLine($"[INFO] {message}");

    public void Error(string message, Exception exception) =>
        Console.WriteLine($"[ERROR] {message}: {exception.Message}");
}
