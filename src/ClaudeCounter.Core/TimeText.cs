namespace ClaudeCounter.Core;

public static class TimeText
{
    public static string Countdown(DateTimeOffset resetsAt, DateTimeOffset now)
    {
        var delta = resetsAt - now;
        if (delta <= TimeSpan.Zero)
            return "now";
        if (delta.TotalMinutes < 60)
            return $"{Math.Max(1, (int)Math.Ceiling(delta.TotalMinutes))} min";
        if (delta.TotalHours < 48)
            return $"{(int)delta.TotalHours} h {delta.Minutes} min";
        return $"{(int)delta.TotalDays} d {delta.Hours} h";
    }

    /// <summary>
    /// Past-tense counterpart to <see cref="Countdown"/> - "X ago" rather than
    /// "resets in X". S11b: used by BackupHealthPresenter for "last succeeded
    /// N ago". Single-unit, like Countdown's own minute/hour/day tiers - this
    /// only ever needs to answer "roughly how long ago", not a full duration
    /// breakdown.
    /// </summary>
    public static string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        var delta = now - when;
        if (delta <= TimeSpan.FromMinutes(1))
            return "just now";
        if (delta.TotalMinutes < 60)
            return $"{(int)delta.TotalMinutes} min ago";
        if (delta.TotalHours < 48)
            return $"{(int)delta.TotalHours} h ago";
        return $"{(int)delta.TotalDays} d ago";
    }
}
