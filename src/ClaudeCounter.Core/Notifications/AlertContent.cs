using ClaudeCounter.Core;

namespace ClaudeCounter.Notifications;

/// <summary>
/// The title and body of an alert popup. Pure and platform-neutral, shared by
/// the Windows (AlertPopupForm) and Linux (AlertPopupWindow) popups so their
/// wording cannot drift apart.
/// </summary>
public static class AlertContent
{
    public static (string Title, string Body) For(AlertEvent e, DateTimeOffset now)
    {
        var reset = e.ResetsAt is { } at ? $", resets in {TimeText.Countdown(at, now)}" : "";
        if (e.Level == AlertLevel.Maxed)
            return ("Time to touch some grass",
                    $"{e.WindowLabel} is at 100%{reset}.");
        if (e.Level == AlertLevel.Warn)
            return ($"{e.WindowLabel} usage warning",
                    $"At {e.Utilization:0}%{reset}.");
        return ($"{e.WindowLabel} usage critical",
                $"At {e.Utilization:0}%{reset}.");
    }
}
