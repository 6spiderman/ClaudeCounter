using System.Text.Json;

namespace ClaudeCounter.Settings;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    public SettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClaudeCounter", "settings.json");
    }

    /// <returns>Loaded settings and whether this is a first run (no file yet).</returns>
    public (AppSettings Settings, bool IsFirstRun) Load()
    {
        if (!File.Exists(_path))
        {
            // Normalize a brand-new settings object too (not just loaded ones),
            // so NotificationStateVersion is already stamped at the current
            // value before the first save. Skipping this would leave a fresh
            // install's first settings.json on file with version 0 (the
            // "pre-Warn" sentinel), causing Normalize() to spuriously clear
            // real same-version dedupe state the very first time it is loaded
            // back on a later run.
            var fresh = new AppSettings();
            fresh.Normalize();
            return (fresh, true);
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions)
                           ?? new AppSettings();
            settings.Normalize();
            return (settings, false);
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // Never crash a tray app over a corrupt settings file.
            return (new AppSettings(), false);
        }
    }

    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
    }
}
