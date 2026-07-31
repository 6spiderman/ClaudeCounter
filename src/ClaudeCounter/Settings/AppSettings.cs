namespace ClaudeCounter.Settings;

public sealed class AppSettings
{
    public static readonly int[] IntervalPresets = [1, 2, 5, 10, 15, 30, 60];

    public int PollIntervalMinutes { get; set; } = 5;
    public int WarnThreshold { get; set; } = 75;
    public int CriticalThreshold { get; set; } = 90;
    public bool AutostartEnabled { get; set; } = true;

    /// <summary>Ask GitHub once a day whether a newer release exists.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>When the last update check ran, so a restart does not re-check.</summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>A version the user dismissed; do not nag about it again.</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>
    /// False on a settings file written before onboarding existed, so an
    /// upgrading user is walked through sign-in once rather than silently
    /// dropped onto the bootstrap path.
    /// </summary>
    public bool OnboardingCompleted { get; set; }

    public void Normalize()
    {
        if (!IntervalPresets.Contains(PollIntervalMinutes))
            PollIntervalMinutes = 5;
        WarnThreshold = Math.Clamp(WarnThreshold, 1, 100);
        CriticalThreshold = Math.Clamp(CriticalThreshold, 1, 100);
        if (WarnThreshold >= CriticalThreshold)
        {
            WarnThreshold = 75;
            CriticalThreshold = 90;
        }
    }
}
