using System.Reflection;

namespace ClaudeCounter.Core;

/// <summary>
/// Identity of the running build. The version comes from the git tag via
/// <c>-p:Version=</c> in the release workflow; a local build reports the
/// <c>0.0.0-dev</c> default from Directory.Build.props.
/// </summary>
public static class AppInfo
{
    public const string RepoOwner = "6spiderman";
    public const string RepoName = "ClaudeCounter";
    public const string RepoSlug = $"{RepoOwner}/{RepoName}";
    public const string RepoUrl = $"https://github.com/{RepoSlug}";
    public const string ReleasesUrl = $"{RepoUrl}/releases";
    public const string LicenseUrl = $"{RepoUrl}/blob/main/LICENSE";
    public const string WingetId = $"{RepoOwner}.{RepoName}";

    public static string Version { get; } = ReadInformationalVersion();

    public static string DisplayVersion => $"v{Version}";

    /// <summary>True for a build that never came from a tagged release.</summary>
    public static bool IsDevBuild => Version.StartsWith("0.0.0", StringComparison.Ordinal);

    public static string? ExePath => Environment.ProcessPath;

    /// <summary>
    /// True when running from the location the installer uses, as opposed to a
    /// portable copy. Only affects which update instructions the About box
    /// shows, so a wrong answer is cosmetic.
    /// </summary>
    public static bool IsInstalled =>
        ExePath?.Contains(@"\Programs\ClaudeCounter", StringComparison.OrdinalIgnoreCase) == true;

    public static string UserAgent => $"ClaudeCounter/{Version}";

    private static string ReadInformationalVersion()
    {
        var raw = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(raw))
            return "0.0.0-unknown";

        // Directory.Build.props turns off source-revision stamping, but strip a
        // "+<sha>" suffix anyway so a build with different properties cannot
        // put a commit hash in the About box.
        var plus = raw.IndexOf('+');
        return plus >= 0 ? raw[..plus] : raw;
    }
}
