using MultiKerbal.Relay;

namespace MultiKerbal.Tests;

public sealed class RateLimiterTests
{
    [Fact]
    public void Reserve_LetsTheFirstSecondThrough_ThenMakesTheNextBytesWait()
    {
        double now = 100;
        var limiter = new RateLimiter(1000, () => now);

        Assert.Equal(TimeSpan.Zero, limiter.Reserve(1000));
        Assert.Equal(TimeSpan.FromSeconds(1), limiter.Reserve(1000));

        now += 1;
        Assert.Equal(TimeSpan.Zero, limiter.Reserve(1000));
    }

    [Fact]
    public void Reserve_NeverSavesMoreThanOneSecond()
    {
        double now = 0;
        var limiter = new RateLimiter(1000, () => now);

        now += 60; // un minuto parado no da un minuto de crédito
        Assert.Equal(TimeSpan.Zero, limiter.Reserve(1000));
        Assert.Equal(TimeSpan.FromMilliseconds(500), limiter.Reserve(500));
    }

    [Fact]
    public void Reserve_ABlockLargerThanTheBucket_PassesWhenTheBucketIsFull()
    {
        double now = 0;
        var limiter = new RateLimiter(1000, () => now);

        // Un trozo mayor que el cubo no puede esperar para siempre: pasa con el cubo lleno y deja deuda.
        Assert.Equal(TimeSpan.Zero, limiter.Reserve(3000));
        Assert.True(limiter.Reserve(1) > TimeSpan.FromSeconds(1.9));
    }
}
