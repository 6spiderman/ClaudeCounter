using System.Text.Json;
using ClaudeCounter.Core;
using Xunit;

namespace ClaudeCounter.Tests;

public class UsageModelsTests
{
    // Exact shape observed from the live endpoint during research.
    private const string SampleJson = """
        {
            "five_hour": {
                "utilization": 33.0,
                "resets_at": "2026-04-11T07:00:00.528743+00:00"
            },
            "seven_day": {
                "utilization": 13.0,
                "resets_at": "2026-04-17T00:59:59.951713+00:00"
            },
            "seven_day_opus": null,
            "seven_day_sonnet": {
                "utilization": 1.0,
                "resets_at": "2026-04-16T03:00:00.951719+00:00"
            },
            "extra_usage": {
                "is_enabled": false,
                "monthly_limit": null,
                "used_credits": null,
                "utilization": null
            }
        }
        """;

    [Fact]
    public void DeserializesSampleResponse()
    {
        var snapshot = JsonSerializer.Deserialize<UsageSnapshot>(SampleJson, UsageJson.Options)!;

        Assert.NotNull(snapshot.FiveHour);
        Assert.Equal(33.0, snapshot.FiveHour!.Utilization);
        var expectedReset = new DateTimeOffset(2026, 4, 11, 7, 0, 0, TimeSpan.Zero);
        Assert.True((snapshot.FiveHour.ResetsAt!.Value - expectedReset).Duration() < TimeSpan.FromSeconds(1));

        Assert.Equal(13.0, snapshot.SevenDay!.Utilization);
        Assert.Null(snapshot.SevenDayOpus);
        Assert.Equal(1.0, snapshot.SevenDaySonnet!.Utilization);
        Assert.False(snapshot.ExtraUsage!.IsEnabled);
        Assert.Null(snapshot.ExtraUsage.Utilization);
    }

    [Fact]
    public void ToleratesNullResetsAtInAWindow()
    {
        // Observed live: a model-specific weekly window present but with a null
        // resets_at. A non-nullable ResetsAt made the ENTIRE response fail to
        // parse, so every poll threw and the tray showed stale data.
        const string json = """
            {
                "five_hour": {"utilization": 10.0, "resets_at": "2026-06-12T08:30:01+00:00"},
                "seven_day_sonnet": {"utilization": 0.0, "resets_at": null}
            }
            """;
        var snapshot = JsonSerializer.Deserialize<UsageSnapshot>(json, UsageJson.Options)!;
        Assert.Equal(10.0, snapshot.FiveHour!.Utilization);
        Assert.Equal(0.0, snapshot.SevenDaySonnet!.Utilization);
        Assert.Null(snapshot.SevenDaySonnet.ResetsAt);
    }

    [Fact]
    public void ToleratesUnknownFieldsAndMissingWindows()
    {
        const string json = """{"five_hour": null, "brand_new_field": {"x": 1}}""";
        var snapshot = JsonSerializer.Deserialize<UsageSnapshot>(json, UsageJson.Options)!;
        Assert.Null(snapshot.FiveHour);
        Assert.Null(snapshot.SevenDay);
    }

    [Fact]
    public void ParsesEnabledExtraUsage()
    {
        const string json = """
            {"extra_usage": {"is_enabled": true, "monthly_limit": 50, "used_credits": 12.5, "utilization": 25.0}}
            """;
        var snapshot = JsonSerializer.Deserialize<UsageSnapshot>(json, UsageJson.Options)!;
        Assert.True(snapshot.ExtraUsage!.IsEnabled);
        Assert.Equal(50m, snapshot.ExtraUsage.MonthlyLimit);
        Assert.Equal(12.5m, snapshot.ExtraUsage.UsedCredits);
        Assert.Equal(25.0, snapshot.ExtraUsage.Utilization);
    }
}
