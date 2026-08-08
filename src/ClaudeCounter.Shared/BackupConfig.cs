using System.Text.Json;

namespace ClaudeBackup;

public sealed class GitTarget
{
    public bool Enabled { get; set; }
    public string RemoteUrl { get; set; } = "";
    public string Branch { get; set; } = "main";
}

public sealed class DriveTarget
{
    public bool Enabled { get; set; }
    public string RcloneRemote { get; set; } = "";
}

public sealed class ScheduleConfig
{
    public string Frequency { get; set; } = "daily"; // daily | weekly | hourly
    public string Time { get; set; } = "09:00";
}

public sealed class BackupConfig
{
    public string SourceRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    public List<string> Include { get; set; } = new();
    public List<string> Exclude { get; set; } = new();
    public GitTarget Github { get; set; } = new();
    public DriveTarget Drive { get; set; } = new();
    public ScheduleConfig Schedule { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeCounter", "backup.json");

    public static BackupConfig Default() => new()
    {
        // plugins/*.json (not plugins/**/*.json): the only irreplaceable
        // state under plugins/ is the top-level manifests (installed_plugins.json,
        // known_marketplaces.json, blocklist.json). Everything nested one or
        // more directories deeper is re-downloadable third-party plugin
        // content, some of which carries secret-shaped keys (e.g. .mcp.json
        // "Authorization" fields) that the file-name denylist cannot see
        // inside. A user who wants the full plugin tree backed up can opt in
        // explicitly; it should not be a silent default.
        Include = new()
        {
            "settings.json", "CLAUDE.md", "commands/**", "agents/**", "plugins/*.json",
        },
        // "**/*cache*" only ever constrains a file's own name (it does not
        // end in "**", so FileSelector will not prune a whole directory on
        // its account) and was measured to remove almost nothing on a real
        // machine, so it is gone entirely rather than left as dead weight -
        // "**/cache/**" is the pattern that actually prunes a cache
        // directory's contents.
        Exclude = new() { "projects/**", "statsig/**", "**/cache/**" },
    };

    public static BackupConfig Load(string path)
    {
        if (!File.Exists(path))
            return Default();
        try
        {
            return JsonSerializer.Deserialize<BackupConfig>(File.ReadAllText(path)) ?? Default();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return Default();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
