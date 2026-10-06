using ClaudeCounter.Core;
using Xunit;

namespace ClaudeCounter.Tests;

public class BackoffPolicyTests
{
    [Fact]
    public void RetryAfterIsHonored()
    {
        Assert.Equal(TimeSpan.FromSeconds(45),
            BackoffPolicy.NextDelay(3, TimeSpan.FromSeconds(45)));
    }

    [Fact]
    public void RetryAfterIsClampedToMax()
    {
        Assert.Equal(BackoffPolicy.MaxDelay,
            BackoffPolicy.NextDelay(1, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void RetryAfterIsClampedToMin()
    {
        Assert.Equal(TimeSpan.FromSeconds(1),
            BackoffPolicy.NextDelay(1, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    public void ExponentialSequence(int failures, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds),
            BackoffPolicy.NextDelay(failures, null));
    }

    [Fact]
    public void ExponentialIsCappedAt15Minutes()
    {
        Assert.Equal(BackoffPolicy.MaxDelay, BackoffPolicy.NextDelay(10, null));
        Assert.Equal(BackoffPolicy.MaxDelay, BackoffPolicy.NextDelay(100, null));
    }
}
