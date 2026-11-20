using Charon.Features.Tweaks;
using Xunit;

namespace Charon.Tests.Features.Tweaks;

public class FpsCounterTests
{
    [Fact]
    public void NothingIsShownBeforeTheFirstSecond()
    {
        var counter = new FpsCounter();

        for (var i = 0; i < 30; i++)
            Assert.False(counter.Record(1000.0 / 60));

        Assert.Null(counter.Fps);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(16)]
    public void ASteadyRateReadsAsThatRate(int rate)
    {
        var counter = new FpsCounter();

        for (var i = 0; i < rate * 3; i++)
            counter.Record(1000.0 / rate);

        Assert.NotNull(counter.Fps);
        Assert.Equal(rate, counter.Fps!.Value, 0.5);
    }

    [Fact]
    public void AChangeOfRateShowsInTheNextWindow()
    {
        var counter = new FpsCounter();
        for (var i = 0; i < 120; i++)
            counter.Record(1000.0 / 60);

        for (var i = 0; i < 40; i++)
            counter.Record(50);

        Assert.Equal(20, counter.Fps!.Value, 0.5);
    }

    [Fact]
    public void AFrameWithNoLengthIsNotCounted()
    {
        var counter = new FpsCounter();

        Assert.False(counter.Record(0));
        Assert.False(counter.Record(double.NaN));
        Assert.False(counter.Record(-5));
        Assert.Null(counter.Fps);
    }
}
