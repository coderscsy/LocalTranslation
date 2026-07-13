using OpenccNetLib;

namespace LocalTranslator.Infrastructure.Services;

public static class ChineseTextNormalizer
{
    private static readonly Lazy<Opencc> TraditionalToSimplified =
        new(() => new Opencc(OpenccConfig.T2S), LazyThreadSafetyMode.ExecutionAndPublication);

    public static string ToSimplified(string text) =>
        string.IsNullOrWhiteSpace(text) ? text : TraditionalToSimplified.Value.Convert(text);
}
