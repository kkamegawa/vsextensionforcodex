using System.IO;
using System.Text.Json;

namespace Codex.VisualStudio.Extension;

// Minimal user settings persisted to %APPDATA%\Kkamegawa.CodexForVisualStudio\settings.json.
// Dependency-free and resilient: any IO/JSON failure falls back to defaults so a missing or
// corrupt file never blocks the tool window.
public sealed class ExtensionSettings
{
    // Opts the codex app-server into experimental APIs (e.g. request_user_input interactive
    // choices). Off by default; enabling it takes effect at the next app-server initialize.
    public bool ExperimentalApiEnabled { get; set; }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Kkamegawa.CodexForVisualStudio",
        "settings.json");

    public static ExtensionSettings Load()
    {
        try
        {
            string path = SettingsPath;
            if (File.Exists(path))
            {
                ExtensionSettings? settings = JsonSerializer.Deserialize<ExtensionSettings>(File.ReadAllText(path));
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("Failed to load extension settings; using defaults", ex);
        }

        return new ExtensionSettings();
    }

    public void Save()
    {
        try
        {
            string path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch (Exception ex)
        {
            ExtensionDiagnostics.Write("Failed to save extension settings", ex);
        }
    }
}
