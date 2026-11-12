using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxamlPreview.Services;

public sealed class AppSettings
{
    public PreviewTheme Theme { get; set; } = PreviewTheme.None;
    public bool FollowEditor { get; set; }
    public string? LastProject { get; set; }
    /// <summary>The app's own theme: "system", "zed" (match Zed) or a theme id ("builtin:Nord", "zed:Fleet Dark").</summary>
    public string AppTheme { get; set; } = "system";
    /// <summary>Preview Zed's unsaved text as you type instead of waiting for a save.</summary>
    public bool LiveTyping { get; set; }

    private static string FilePath
    {
        get
        {
            var config = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(config, "axaml-previewer", "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJson.Default.AppSettings) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception) { return new AppSettings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings));
        }
        catch (Exception) { }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
