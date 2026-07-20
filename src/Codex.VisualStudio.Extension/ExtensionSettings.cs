using System.IO;
using System.Text.Json;

namespace Codex.VisualStudio.Extension;

// Minimal user settings persisted to %APPDATA%\Kkamegawa.CodexForVisualStudio\settings.json.
// Dependency-free and resilient: any IO/JSON failure falls back to defaults so a missing or
// corrupt file never blocks the tool window.
public sealed class ExtensionSettings
{
    private static readonly object SettingsGate = new();
    // Opts the codex app-server into experimental APIs (e.g. request_user_input interactive
    // choices). Off by default; enabling it takes effect at the next app-server initialize.
    public bool ExperimentalApiEnabled { get; set; }

    // Stable approval-mode ID. Display text is intentionally not persisted so wording can evolve
    // without invalidating user settings.
    public string ApprovalModeId { get; set; } = ApprovalModeCatalog.CustomId;

    // Empty means that Codex config.toml supplies the reasoning effort. A non-empty value is a
    // stable catalog ID; display text and descriptions remain app-server owned and are not stored.
    public string ReasoningEffortId { get; set; } = ReasoningEffortCatalog.DefaultId;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Kkamegawa.CodexForVisualStudio",
        "settings.json");

    public static ExtensionSettings Load()
    {
        lock (SettingsGate)
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
        }

        return new ExtensionSettings();
    }

    public void Save()
    {
        lock (SettingsGate)
        {
            string? temporaryPath = null;
            try
            {
                string path = SettingsPath;
                string directory = Path.GetDirectoryName(path)!;
                Directory.CreateDirectory(directory);
                temporaryPath = Path.Combine(directory, $"settings.{Guid.NewGuid():N}.tmp");
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this));
                File.Move(temporaryPath, path, overwrite: true);
            }
            catch (Exception ex)
            {
                ExtensionDiagnostics.Write("Failed to save extension settings", ex);
                if (temporaryPath is not null)
                {
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch (Exception cleanupException)
                    {
                        ExtensionDiagnostics.Write("Failed to remove temporary extension settings", cleanupException);
                    }
                }
            }
        }
    }
}

internal interface IExtensionSettingsStore
{
    ExtensionSettings Load();

    void Save(ExtensionSettings settings);
}

internal sealed class FileExtensionSettingsStore : IExtensionSettingsStore
{
    public ExtensionSettings Load() => ExtensionSettings.Load();

    public void Save(ExtensionSettings settings) => settings.Save();
}
