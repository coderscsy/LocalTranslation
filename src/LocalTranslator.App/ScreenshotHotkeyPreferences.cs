using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace LocalTranslator.App;

public sealed record ScreenshotHotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    public static ScreenshotHotkeyGesture Default { get; } =
        new(ModifierKeys.Control | ModifierKeys.Shift, Key.F9);

    [JsonIgnore]
    public int VirtualKey => KeyInterop.VirtualKeyFromKey(Key);

    [JsonIgnore]
    public bool IsValid =>
        Key is not (Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) &&
        Modifiers != ModifierKeys.None;

    [JsonIgnore]
    public string DisplayText
    {
        get
        {
            var parts = new List<string>(5);
            if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(Key.ToString());
            return string.Join(" + ", parts);
        }
    }
}

public sealed class ScreenshotHotkeyPreferences
{
    public ModifierKeys Modifiers { get; init; } = ScreenshotHotkeyGesture.Default.Modifiers;
    public Key Key { get; init; } = ScreenshotHotkeyGesture.Default.Key;

    [JsonIgnore]
    public ScreenshotHotkeyGesture Gesture => new(Modifiers, Key);

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalTranslator",
        "screenshot-hotkey.json");

    public static ScreenshotHotkeyPreferences Load()
    {
        try
        {
            var preferences = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<ScreenshotHotkeyPreferences>(File.ReadAllText(SettingsPath))
                : null;
            return preferences?.Gesture.IsValid == true ? preferences : new ScreenshotHotkeyPreferences();
        }
        catch
        {
            return new ScreenshotHotkeyPreferences();
        }
    }

    public static void Save(ScreenshotHotkeyGesture gesture)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var preferences = new ScreenshotHotkeyPreferences
        {
            Modifiers = gesture.Modifiers,
            Key = gesture.Key
        };
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(preferences));
    }
}
