using System.Text;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LocalTranslator.Infrastructure.Services;

/// <summary>
/// In-process Parakeet TDT recognizer adapted from Meetily's MIT-licensed
/// Parakeet engine. It avoids the Python/FunASR sidecar and keeps model memory
/// inside the WPF process so it can be released deterministically on Stop.
/// </summary>
public sealed class MeetilyParakeetRecognizer : IDisposable
{
    private const int SubsamplingFactor = 8;
    private const float WindowSizeSeconds = 0.01f;
    private const int MaxTokensPerStep = 3;
    private static readonly int[] TdtDurations = [0, 1, 2, 3, 4];
    private static readonly Regex DecodeSpacePattern = new(
        @"\A\s|\s\B|(\s)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly InferenceSession _preprocessor;
    private readonly string[] _vocabulary;
    private readonly int _blankIndex;
    private readonly int _vocabularySize;
    private readonly int[] _state1Shape;
    private readonly int[] _state2Shape;
    private bool _disposed;

    public MeetilyParakeetRecognizer(string modelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        SpeechModelManager.ValidateParakeetModel(modelDirectory);

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_PARALLEL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount - 1, 2, 8),
            InterOpNumThreads = 2
        };
        _encoder = new InferenceSession(Path.Combine(modelDirectory, "encoder-model.int8.onnx"), options);
        _decoder = new InferenceSession(Path.Combine(modelDirectory, "decoder_joint-model.int8.onnx"), options);
        _preprocessor = new InferenceSession(Path.Combine(modelDirectory, "nemo128.onnx"), options);
        (_vocabulary, _blankIndex) = LoadVocabulary(Path.Combine(modelDirectory, "vocab.txt"));
        _vocabularySize = _vocabulary.Length;
        _state1Shape = ResolveStateShape(_decoder, "input_states_1");
        _state2Shape = ResolveStateShape(_decoder, "input_states_2");
    }

    public Task<ParakeetRecognitionResult> TranscribeAsync(
        ReadOnlyMemory<byte> pcm16Mono,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Transcribe(pcm16Mono.Span, cancellationToken), cancellationToken);

    private ParakeetRecognitionResult Transcribe(
        ReadOnlySpan<byte> pcm16Mono,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pcm16Mono.Length < 2) return new ParakeetRecognitionResult(string.Empty, []);

        var samples = new float[pcm16Mono.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToInt16(pcm16Mono.Slice(i * 2, 2)) / 32768f;

        var waveform = new DenseTensor<float>(samples, new[] { 1, samples.Length });
        var waveformLength = new DenseTensor<long>(
            new[] { samples.LongLength }, new[] { 1 });
        using var preprocessed = _preprocessor.Run(
        [
            NamedOnnxValue.CreateFromTensor("waveforms", waveform),
            NamedOnnxValue.CreateFromTensor("waveforms_lens", waveformLength)
        ]);
        var features = CopyTensor(preprocessed, "features");
        var featureLengths = CopyTensor<long>(preprocessed, "features_lens");

        using var encoded = _encoder.Run(
        [
            NamedOnnxValue.CreateFromTensor("audio_signal", features),
            NamedOnnxValue.CreateFromTensor("length", featureLengths)
        ]);
        var encoderOutput = encoded.First(value => value.Name == "outputs").AsTensor<float>();
        var encodedLength = checked((int)encoded.First(value => value.Name == "encoded_lengths")
            .AsTensor<long>()[0]);
        var dimensions = encoderOutput.Dimensions.ToArray();
        if (dimensions.Length != 3 || dimensions[0] != 1)
            throw new InvalidDataException($"Parakeet encoder 输出维度异常：[{string.Join(',', dimensions)}]。");

        // NeMo encoder output is [batch, channels, time].
        var channels = dimensions[1];
        var timeSteps = Math.Min(encodedLength, dimensions[2]);
        var tokens = new List<int>();
        var timestamps = new List<float>();
        var state1 = new float[Product(_state1Shape)];
        var state2 = new float[Product(_state2Shape)];
        var frame = new float[channels];
        var t = 0;
        var emittedAtFrame = 0;

        while (t < timeSteps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var channel = 0; channel < channels; channel++)
                frame[channel] = encoderOutput[0, channel, t];

            var targetToken = tokens.Count == 0 ? _blankIndex : tokens[^1];
            var encoderTensor = new DenseTensor<float>(frame, new[] { 1, channels, 1 });
            var targetTensor = new DenseTensor<int>(new[] { targetToken }, new[] { 1, 1 });
            var targetLength = new DenseTensor<int>(new[] { 1 }, new[] { 1 });
            var state1Tensor = new DenseTensor<float>(state1, _state1Shape);
            var state2Tensor = new DenseTensor<float>(state2, _state2Shape);
            using var decoded = _decoder.Run(
            [
                NamedOnnxValue.CreateFromTensor("encoder_outputs", encoderTensor),
                NamedOnnxValue.CreateFromTensor("targets", targetTensor),
                NamedOnnxValue.CreateFromTensor("target_length", targetLength),
                NamedOnnxValue.CreateFromTensor("input_states_1", state1Tensor),
                NamedOnnxValue.CreateFromTensor("input_states_2", state2Tensor)
            ]);

            var logits = decoded.First(value => value.Name == "outputs").AsTensor<float>().ToArray();
            var token = ArgMax(logits, 0, Math.Min(_vocabularySize, logits.Length));
            if (token != _blankIndex)
            {
                state1 = decoded.First(value => value.Name == "output_states_1").AsTensor<float>().ToArray();
                state2 = decoded.First(value => value.Name == "output_states_2").AsTensor<float>().ToArray();
                tokens.Add(token);
                timestamps.Add(WindowSizeSeconds * SubsamplingFactor * t);
                emittedAtFrame++;
            }

            if (logits.Length > _vocabularySize)
            {
                var durationIndex = ArgMax(logits, _vocabularySize, logits.Length - _vocabularySize);
                var skip = durationIndex < TdtDurations.Length ? TdtDurations[durationIndex] : 1;
                if (skip == 0 && (token == _blankIndex || emittedAtFrame >= MaxTokensPerStep)) skip = 1;
                if (skip > 0)
                {
                    t += skip;
                    emittedAtFrame = 0;
                }
            }
            else if (token == _blankIndex || emittedAtFrame >= MaxTokensPerStep)
            {
                t++;
                emittedAtFrame = 0;
            }
        }

        return new ParakeetRecognitionResult(Decode(tokens), timestamps);
    }

    private string Decode(IEnumerable<int> tokenIds)
    {
        var builder = new StringBuilder();
        foreach (var id in tokenIds)
            if ((uint)id < (uint)_vocabulary.Length) builder.Append(_vocabulary[id]);
        return DecodeSpacePattern.Replace(builder.ToString(), match => match.Groups[1].Success ? " " : string.Empty)
            .Trim();
    }

    private static (string[] Vocabulary, int BlankIndex) LoadVocabulary(string path)
    {
        var entries = new List<(string Token, int Id)>();
        var maxId = 0;
        var blank = -1;
        foreach (var line in File.ReadLines(path))
        {
            var separator = line.LastIndexOf(' ');
            if (separator <= 0 || !int.TryParse(line[(separator + 1)..], out var id)) continue;
            var token = line[..separator].Replace('▁', ' ');
            entries.Add((token, id));
            maxId = Math.Max(maxId, id);
            if (token == "<blk>") blank = id;
        }
        if (blank < 0) throw new InvalidDataException("Parakeet vocab.txt 缺少 <blk> token。");
        var vocabulary = new string[maxId + 1];
        foreach (var (token, id) in entries) vocabulary[id] = token;
        return (vocabulary, blank);
    }

    private static int[] ResolveStateShape(InferenceSession session, string name)
    {
        if (!session.InputMetadata.TryGetValue(name, out var metadata))
            throw new InvalidDataException($"Parakeet decoder 缺少输入 {name}。");
        var dimensions = metadata.Dimensions.Select((value, index) =>
            value > 0 ? value : index == 1 ? 1 : throw new InvalidDataException(
                $"Parakeet decoder 的 {name} 存在无法解析的动态维度。")).ToArray();
        return dimensions;
    }

    private static DenseTensor<T> CopyTensor<T>(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> values,
        string name) where T : unmanaged
    {
        var tensor = values.First(value => value.Name == name).AsTensor<T>();
        return new DenseTensor<T>(tensor.ToArray(), tensor.Dimensions.ToArray());
    }

    private static DenseTensor<float> CopyTensor(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> values,
        string name) => CopyTensor<float>(values, name);

    private static int ArgMax(float[] values, int start, int count)
    {
        if (count <= 0) return 0;
        var best = 0;
        var bestValue = values[start];
        for (var i = 1; i < count; i++)
        {
            var value = values[start + i];
            if (value <= bestValue) continue;
            bestValue = value;
            best = i;
        }
        return best;
    }

    private static int Product(IEnumerable<int> dimensions) =>
        dimensions.Aggregate(1, checked((result, value) => result * value));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _preprocessor.Dispose();
        _decoder.Dispose();
        _encoder.Dispose();
    }
}

public sealed record ParakeetRecognitionResult(string Text, IReadOnlyList<float> Timestamps);
