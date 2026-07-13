using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using Whisper.net;

var modelPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "LocalTranslator", "Models", "speech", "whisper-small-q5_1", "ggml-small-q5_1.bin");
if (!File.Exists(modelPath)) throw new FileNotFoundException("Whisper Small model is missing.", modelPath);

var wavePath = Path.Combine(Path.GetTempPath(), $"local-translator-zh-{Guid.NewGuid():N}.wav");
try
{
    using (var synthesizer = new SpeechSynthesizer())
    {
        synthesizer.SelectVoice("Microsoft Huihui Desktop");
        synthesizer.SetOutputToWaveFile(
            wavePath,
            new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        synthesizer.Speak("今天天气很好，我们一起去公园散步。");
    }

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
    Console.WriteLine($"RECOGNIZED={text}");
    if (!text.Contains("今天天气很好", StringComparison.Ordinal) ||
        !text.Contains("公园", StringComparison.Ordinal))
        throw new InvalidOperationException("Chinese speech recognition smoke test failed.");
}
finally
{
    if (File.Exists(wavePath)) File.Delete(wavePath);
}
