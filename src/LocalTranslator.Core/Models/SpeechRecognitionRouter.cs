namespace LocalTranslator.Core.Models;

public static class SpeechRecognitionRouter
{
    public static SpeechRecognitionEngine Resolve(
        SupportedLanguage source,
        SpeechRecognitionEngine selected,
        bool senseVoiceEnabled,
        bool whisperEnabled)
    {
        if (selected != SpeechRecognitionEngine.MeetilyParakeet || source == SupportedLanguage.English)
            return selected;

        if (source is SupportedLanguage.AutoDetect or SupportedLanguage.ChineseSimplified or SupportedLanguage.Japanese)
        {
            if (senseVoiceEnabled) return SpeechRecognitionEngine.SenseVoiceSmall;
            if (whisperEnabled) return SpeechRecognitionEngine.WhisperGgml;
        }

        return selected;
    }
}
