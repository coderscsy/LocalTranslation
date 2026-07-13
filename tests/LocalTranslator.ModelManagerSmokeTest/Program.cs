using System.Net;
using System.Security.Cryptography;
using LocalTranslator.Infrastructure.Configuration;
using LocalTranslator.Infrastructure.Services;

var payload = RandomNumberGenerator.GetBytes(128 * 1024);
var sha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
var testRoot = Path.Combine(Path.GetTempPath(), "LocalTranslator-ModelManager-SmokeTest");
if (Directory.Exists(testRoot))
{
    Directory.Delete(testRoot, true);
}

const string prefix = "http://127.0.0.1:18992/";
using var listener = new HttpListener();
listener.Prefixes.Add(prefix);
listener.Start();
var serverTask = Task.Run(async () =>
{
    var context = await listener.GetContextAsync();
    context.Response.StatusCode = 200;
    context.Response.ContentLength64 = payload.Length;
    await context.Response.OutputStream.WriteAsync(payload);
    context.Response.Close();
});

var options = new AppOptions
{
    Translation = new TranslationOptions
    {
        LocalLlm = new LocalLlmOptions
        {
            ModelsRoot = testRoot,
            Models =
            [
                new LocalLlmModelOptions
                {
                    Id = "smoke-model",
                    DisplayName = "Smoke Model",
                    RelativePath = "translation/smoke/model.gguf",
                    DownloadUrl = prefix + "model.gguf",
                    SizeBytes = payload.Length,
                    Sha256 = sha256
                }
            ]
        }
    }
};

using var manager = new LocalModelManager(options);
await manager.InstallAsync("smoke-model");
await serverTask;
var installed = manager.GetModels().Single().IsInstalled;
manager.Uninstall("smoke-model");
var removed = !manager.GetModels().Single().IsInstalled;

Console.WriteLine($"INSTALL_OK={installed}");
Console.WriteLine($"UNINSTALL_OK={removed}");
if (!installed || !removed)
{
    throw new InvalidOperationException("Model manager smoke test failed.");
}

if (Directory.Exists(testRoot))
{
    Directory.Delete(testRoot, true);
}
