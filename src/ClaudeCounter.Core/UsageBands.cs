namespace ClaudeCounter.Core;

/// <summary>The colour band a usage percentage falls in.</summary>
public enum UsageBand
{
    Green,
    Amber,
    Red,
    Gray,
}

/// <summary>
/// The one definition of which band a percentage is in, shared by both tray
/// icons, <c>claudecounter --status</c> and the Plasma widget, so they never
/// disagree about when something turns amber or red.
/// </summary>
public static class UsageBands
{
    public static UsageBand For(double utilization, int warnThreshold, int criticalThreshold) =>
        utilization >= criticalThreshold ? UsageBand.Red
        : utilization >= warnThreshold ? UsageBand.Amber
        : UsageBand.Green;

    /// <summary>The lowercase name used in the status JSON ("green", "amber", "red", "gray").</summary>
    public static string Name(UsageBand band) => band switch
    {
        UsageBand.Green => "green",
        UsageBand.Amber => "amber",
        UsageBand.Red => "red",
        _ => "gray",
    };
}
