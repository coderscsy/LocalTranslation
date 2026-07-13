using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalTranslator.Infrastructure.Configuration;

public static class AppOptionsLoader
{
    public static AppOptions Load(string path)
        => Load(path, null);

    public static AppOptions Load(string path, string? localOverridePath)
    {
        if (!File.Exists(path))
        {
            return new AppOptions();
        }

        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        if (!string.IsNullOrWhiteSpace(localOverridePath) && File.Exists(localOverridePath))
        {
            var localRoot = JsonNode.Parse(File.ReadAllText(localOverridePath)) as JsonObject;
            if (localRoot is not null)
            {
                Merge(root, localRoot);
            }
        }

        return root.Deserialize<AppOptions>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new AppOptions();
    }

    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach (var (key, sourceValue) in source)
        {
            if (sourceValue is JsonObject sourceObject && target[key] is JsonObject targetObject)
            {
                Merge(targetObject, sourceObject);
                continue;
            }

            target[key] = sourceValue?.DeepClone();
        }
    }
}
