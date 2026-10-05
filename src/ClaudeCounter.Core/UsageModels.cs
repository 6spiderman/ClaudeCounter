using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeCounter.Core;

// resets_at can be null (e.g. a model-specific weekly window at 0% usage that
// has no active limit yet), so it must be nullable - a non-nullable
// DateTimeOffset here makes the WHOLE response fail to deserialize.
public sealed record UsageWindow(double Utilization, DateTimeOffset? ResetsAt);

public sealed record ExtraUsage(
    bool IsEnabled,
    decimal? MonthlyLimit,
    decimal? UsedCredits,
    double? Utilization);

public sealed record UsageSnapshot(
    UsageWindow? FiveHour,
    UsageWindow? SevenDay,
    UsageWindow? SevenDayOpus,
    UsageWindow? SevenDaySonnet,
    ExtraUsage? ExtraUsage);

public static class UsageJson
{
    // The endpoint is undocumented; be tolerant of unknown fields and nulls.
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        AllowTrailingCommas = true,
    };

    // extra_usage.monthly_limit/used_credits come back as integer cents, not
    // dollars - confirmed live against a real account (an actual $2.21 of a
    // $95 limit was reported as 221/9500, showing as "$221 / $9500" in the
    // tray). utilization is already a percentage and needs no conversion.
    public static UsageSnapshot? Parse(string json)
    {
        var snapshot = JsonSerializer.Deserialize<UsageSnapshot>(json, Options);
        if (snapshot?.ExtraUsage is not { } extra)
            return snapshot;

        return snapshot with
        {
            ExtraUsage = extra with
            {
                MonthlyLimit = extra.MonthlyLimit / 100m,
                UsedCredits = extra.UsedCredits / 100m,
            },
        };
    }
}
