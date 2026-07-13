using System.IO;
using System.Text.Json;

namespace LocalTranslator.App;

public enum AppCloseAction
{
    Ask,
    MinimizeToTray,
    Exit
}

public sealed class AppWindowPreferences
{
    public AppCloseAction CloseAction { get; init; } = AppCloseAction.Ask;

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalTranslator",
        "window-preferences.json");

    public static AppWindowPreferences Load()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<AppWindowPreferences>(File.ReadAllText(SettingsPath))
                  ?? new AppWindowPreferences()
                : new AppWindowPreferences();
        }
        catch
        {
            return new AppWindowPreferences();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
    }
}
