using LocalTranslator.Core.Abstractions;
using LocalTranslator.Core.Models;
using LocalTranslator.Infrastructure.Configuration;
using LocalTranslator.Infrastructure.Services;
using SkiaSharp;

var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var generatedOutputPath = Path.Combine(repositoryRoot, "tests", "LocalTranslator.OcrSmokeTest", "ocr-smoke-input.png");
var inputPath = args.FirstOrDefault();
var outputPath = string.IsNullOrWhiteSpace(inputPath)
    ? generatedOutputPath
    : Path.GetFullPath(inputPath);
var imageBytes = string.IsNullOrWhiteSpace(inputPath)
    ? CreateTestImage(outputPath)
    : File.ReadAllBytes(outputPath);

var options = new AppOptions
{
    ModelsRoot = Path.Combine(repositoryRoot, "Models"),
    Ocr = new OcrOptions
    {
        Engine = "PaddleOcrOnnx",
        DetectionModel = "ocr/detection/model.onnx",
        ClassificationModel = "ocr/classification/model.onnx",
        RecognitionModel = "ocr/recognition/model.onnx",
        CharacterDictionary = "ocr/recognition/character_dict.txt"
    }
};

using var ocr = new OfflineOcrService(options, new ConsoleLogger());
var result = await ocr.RecognizeAsync(
    new OcrRequest(imageBytes, SupportedLanguage.ChineseSimplified));

Console.WriteLine();
Console.WriteLine("===== OCR RESULT =====");
Console.WriteLine(result.Text);
Console.WriteLine($"===== {result.Elapsed.TotalMilliseconds:F0} ms =====");
Console.WriteLine($"Test image: {outputPath}");

if (string.IsNullOrWhiteSpace(result.Text))
{
    throw new InvalidOperationException("OCR smoke test failed: no text was recognized.");
}

static byte[] CreateTestImage(string outputPath)
{
    using var bitmap = new SKBitmap(new SKImageInfo(1600, 480, SKColorType.Bgra8888, SKAlphaType.Premul));
    using var canvas = new SKCanvas(bitmap);
    using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
    using var chineseTypeface = SKTypeface.FromFamilyName("Microsoft YaHei");
    using var japaneseTypeface = SKTypeface.FromFamilyName("Yu Gothic UI");
    using var latinTypeface = SKTypeface.FromFamilyName("Segoe UI");
    using var chineseFont = new SKFont(chineseTypeface, 56);
    using var japaneseFont = new SKFont(japaneseTypeface, 56);
    using var latinFont = new SKFont(latinTypeface, 56);

    canvas.Clear(SKColors.White);
    canvas.DrawText("离线截图翻译测试 2026", 60, 110, chineseFont, paint);
    canvas.DrawText("Offline screenshot translation test", 60, 245, latinFont, paint);
    canvas.DrawText("日本語のスクリーン翻訳テスト", 60, 380, japaneseFont, paint);

    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    var bytes = data.ToArray();
    File.WriteAllBytes(outputPath, bytes);
    return bytes;
}

file sealed class ConsoleLogger : IAppLogger
{
    public void Info(string message) => Console.WriteLine($"[INFO] {message}");

    public void Error(string message, Exception exception) =>
        Console.WriteLine($"[ERROR] {message}{Environment.NewLine}{exception}");
}
