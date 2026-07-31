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
}
