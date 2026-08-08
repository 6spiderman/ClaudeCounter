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
        Include = new()
        {
            "settings.json", "CLAUDE.md", "commands/**", "agents/**", "plugins/**/*.json",
        },
        Exclude = new() { "projects/**", "statsig/**", "**/*cache*" },
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
