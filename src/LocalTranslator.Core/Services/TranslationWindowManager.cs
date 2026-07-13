using System.Text;

namespace LocalTranslator.Core.Services;

/// <summary>
/// Maintains the current ASR stream and the last three finalized source sentences.
/// All members are safe to call from concurrent ASR and UI callbacks.
/// </summary>
public sealed class TranslationWindowManager
{
    private const int MaximumHistoricalSentences = 3;
    private readonly object _syncRoot = new();
    private readonly Queue<string> _history = new(MaximumHistoricalSentences);
    private string _activeStream = string.Empty;

    public string HistoricalContext
    {
        get
        {
            lock (_syncRoot) return string.Join("\n", _history);
        }
    }

    public string ActiveStream
    {
        get
        {
            lock (_syncRoot) return _activeStream;
        }
    }

    /// <summary>
    /// Accepts either an accumulated ASR partial result or a newly emitted text fragment.
    /// Accumulated results replace the current value; independent fragments are appended.
    /// </summary>
    public void UpdateStream(string newText)
    {
        var incoming = Normalize(newText);
        if (incoming.Length == 0) return;

        lock (_syncRoot)
        {
            if (_activeStream.Length == 0 || incoming.StartsWith(_activeStream, StringComparison.Ordinal))
            {
                _activeStream = incoming;
                return;
            }

            if (_activeStream.EndsWith(incoming, StringComparison.Ordinal)) return;

            var separator = NeedsSeparator(_activeStream[^1], incoming[0]) ? " " : string.Empty;
            _activeStream = string.Concat(_activeStream, separator, incoming);
        }
    }

    public void FinalizeSentence(string finalText)
    {
        lock (_syncRoot)
        {
            var sentence = Normalize(finalText);
            if (sentence.Length == 0) sentence = _activeStream;

            if (sentence.Length > 0)
            {
                _history.Enqueue(sentence);
                while (_history.Count > MaximumHistoricalSentences) _history.Dequeue();
            }

            _activeStream = string.Empty;
        }
    }

    public string GetPromptPayload()
    {
        lock (_syncRoot)
        {
            var builder = new StringBuilder(32 + _history.Sum(item => item.Length) + _activeStream.Length);
            builder.Append("【前文背景】：")
                .Append(string.Join("\n", _history))
                .Append('\n')
                .Append("【当前待译文本】：")
                .Append(_activeStream);
            return builder.ToString();
        }
    }

    public void Reset()
    {
        lock (_syncRoot)
        {
            _history.Clear();
            _activeStream = string.Empty;
        }
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private static bool NeedsSeparator(char left, char right) =>
        char.IsLetterOrDigit(left) && char.IsLetterOrDigit(right) && left <= 0x7F && right <= 0x7F;
}
