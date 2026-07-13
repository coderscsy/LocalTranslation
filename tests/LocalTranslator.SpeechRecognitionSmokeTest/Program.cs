using System.Diagnostics;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using LocalTranslator.Infrastructure.Configuration;
using LocalTranslator.Infrastructure.Services;
using NAudio.Wave;
using Whisper.net;

var dataRoot = args.FirstOrDefault(value => !value.StartsWith("--", StringComparison.Ordinal))
               ?? (Directory.Exists(@"G:\LocalTranslationData")
                   ? @"G:\LocalTranslationData"
                   : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                       "LocalTranslator"));
var options = new AppOptions { DataRoot = dataRoot };
using var modelManager = new SpeechModelManager(options);

if (args.Contains("--install-parakeet", StringComparer.OrdinalIgnoreCase) &&
    !modelManager.IsParakeetModelInstalled)
{
    var progress = new Progress<ModelDownloadProgress>(value =>
        Console.WriteLine($"DOWNLOAD={value.Percentage:F1}% {value.Stage}"));
    await modelManager.InstallParakeetAsync(progress);
}

await TestWhisperChineseAsync(modelManager.DefaultModelPath);
if (!modelManager.IsParakeetModelInstalled)
{
    Console.WriteLine("PARAKEET=SKIPPED (run with --install-parakeet)");
    return;
}

await TestParakeetEnglishAsync(modelManager.ParakeetModelDirectory);

static async Task TestWhisperChineseAsync(string modelPath)
{
    if (!File.Exists(modelPath)) throw new FileNotFoundException("Whisper Small model is missing.", modelPath);
    var wavePath = Path.Combine(Path.GetTempPath(), $"local-translator-zh-{Guid.NewGuid():N}.wav");
    try
    {
        CreateSpeechWave(wavePath, "Microsoft Huihui Desktop", "今天天气很好，我们一起去公园散步。");
        using var factory = WhisperFactory.FromPath(modelPath);
        using var processor = factory.CreateBuilder()
            .WithLanguage("zh")
            .WithPrompt("以下是简体中文人物对白字幕，请准确识别口语和标点。")
            .WithTemperature(0)
            .WithBeamSearchSamplingStrategy(options => options.WithBeamSize(3))
            .WithProbabilities()
            .Build();
        await using var wave = File.OpenRead(wavePath);
        var parts = new List<string>();
        await foreach (var result in processor.ProcessAsync(wave)) parts.Add(result.Text.Trim());
        var text = string.Join(string.Empty, parts);
        Console.WriteLine($"WHISPER={text}");
        if (!text.Contains("今天天气很好", StringComparison.Ordinal) ||
            !text.Contains("公园", StringComparison.Ordinal))
            throw new InvalidOperationException("Chinese Whisper recognition smoke test failed.");
    }
    finally
    {
        if (File.Exists(wavePath)) File.Delete(wavePath);
    }
}

static async Task TestParakeetEnglishAsync(string modelDirectory)
{
    const string sentence = "Following my completion of undergraduate studies, I am driven by a strong passion to pursue further academic accomplishments in the field of finance.";
    var wavePath = Path.Combine(Path.GetTempPath(), $"local-translator-en-{Guid.NewGuid():N}.wav");
    try
    {
        CreateSpeechWave(wavePath, "Microsoft Zira Desktop", sentence);
        byte[] pcm;
        using (var reader = new WaveFileReader(wavePath))
        {
            if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.BitsPerSample != 16 ||
                reader.WaveFormat.Channels != 1)
                throw new InvalidDataException($"Unexpected smoke-test WAV format: {reader.WaveFormat}");
            pcm = new byte[reader.Length];
            var read = reader.Read(pcm, 0, pcm.Length);
            if (read != pcm.Length) Array.Resize(ref pcm, read);
        }

        var process = Process.GetCurrentProcess();
        var baselinePrivateBytes = process.PrivateMemorySize64;
        var stopwatch = Stopwatch.StartNew();
        ParakeetRecognitionResult result;
        using (var recognizer = new MeetilyParakeetRecognizer(modelDirectory))
        {
            stopwatch.Stop();
            Console.WriteLine($"PARAKEET_LOAD_MS={stopwatch.ElapsedMilliseconds}");
            stopwatch.Restart();
            result = await recognizer.TranscribeAsync(pcm);
            stopwatch.Stop();
            Console.WriteLine($"PARAKEET_INFERENCE_MS={stopwatch.ElapsedMilliseconds}");
            process.Refresh();
            Console.WriteLine($"PARAKEET_PRIVATE_MIB={process.PrivateMemorySize64 / 1024d / 1024d:F1}");

            var snapshotStepBytes = 16000 * 2 * 135 / 100;
            var snapshotBytes = 16000 * 2 * 18 / 10;
            var snapshotIndex = 0;
            string latestRevision = string.Empty;
            while (snapshotBytes < pcm.Length)
            {
                var snapshot = pcm.AsSpan(0, snapshotBytes).ToArray();
                var snapshotResult = await recognizer.TranscribeAsync(snapshot);
                latestRevision = snapshotResult.Text;
                Console.WriteLine($"PARAKEET_STREAM_{++snapshotIndex}={latestRevision}");
                snapshotBytes += snapshotStepBytes;
            }

            latestRevision = result.Text;
            Console.WriteLine($"PARAKEET_STREAM_FINAL={latestRevision}");
            if (latestRevision.Contains("family", StringComparison.OrdinalIgnoreCase) ||
                latestRevision.Contains("dance", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A provisional Parakeet guess leaked into the final revision.");
        }
        Console.WriteLine($"PARAKEET={result.Text}");
        var normalized = result.Text.ToLowerInvariant();
        string[] required = ["undergraduate studies", "strong passion", "academic accomplishments", "field of finance"];
        if (required.Any(value => !normalized.Contains(value, StringComparison.Ordinal)))
            throw new InvalidOperationException("Meetily Parakeet English recognition smoke test failed.");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Task.Delay(300);
        process.Refresh();
        Console.WriteLine($"PARAKEET_RELEASED_MIB={(process.PrivateMemorySize64 - baselinePrivateBytes) / 1024d / 1024d:F1}");
    }
    finally
    {
        if (File.Exists(wavePath)) File.Delete(wavePath);
    }
}

static void CreateSpeechWave(string path, string preferredVoice, string text)
{
    using var synthesizer = new SpeechSynthesizer();
    try
    {
        synthesizer.SelectVoice(preferredVoice);
    }
    catch (ArgumentException)
    {
        // Use the Windows default voice when the preferred desktop voice is unavailable.
    }
    synthesizer.SetOutputToWaveFile(
        path,
        new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
    synthesizer.Speak(text);
}
