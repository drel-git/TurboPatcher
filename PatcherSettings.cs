using System.IO;
using System.Text.Json;

namespace TurboPatcher;

public class PatcherSettings
{
    public string MacroQuestFolder { get; set; } = "";
    public string InstalledSha { get; set; } = "";
    public string InstalledVersion { get; set; } = "";

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TurboPatcher", "settings.json");

    public static PatcherSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<PatcherSettings>(File.ReadAllText(SettingsPath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
