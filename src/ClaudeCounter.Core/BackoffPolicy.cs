namespace ClaudeCounter.Core;

public static class BackoffPolicy
{
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Delay before the next poll after a failure. A server-provided Retry-After
    /// wins; otherwise exponential: 30s, 60s, 120s, ... capped at 15 min.
    /// </summary>
    public static TimeSpan NextDelay(int consecutiveFailures, TimeSpan? retryAfter)
    {
        if (retryAfter is { } ra)
            return ra > MaxDelay ? MaxDelay : ra < MinDelay ? MinDelay : ra;

        if (consecutiveFailures <= 0)
            return BaseDelay;

        var seconds = BaseDelay.TotalSeconds * Math.Pow(2, Math.Min(consecutiveFailures - 1, 10));
        return seconds >= MaxDelay.TotalSeconds ? MaxDelay : TimeSpan.FromSeconds(seconds);
    }
}
