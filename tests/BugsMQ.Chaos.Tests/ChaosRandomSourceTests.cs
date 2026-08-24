namespace BugsMQ.Chaos.Tests;

public sealed class ChaosRandomSourceTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void RollTrigger_NonPositiveProbability_NeverTriggersWithoutConsultingSource(double probability)
    {
        // 1.0 would trigger every roll if the source were actually consulted — proves the <= 0 branch
        // short-circuits instead.
        var random = new FixedChaosRandomSource(1.0);

        Assert.False(random.RollTrigger(probability));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void RollTrigger_ProbabilityAtOrAboveOne_AlwaysTriggersWithoutConsultingSource(double probability)
    {
        // 0.0 would never trigger (0.0 < probability is false when probability is 0) if the source
        // were actually consulted — proves the >= 1 branch short-circuits instead.
        var random = new FixedChaosRandomSource(0.0);

        Assert.True(random.RollTrigger(probability));
    }

    [Theory]
    [InlineData(0.49, 0.5, true)]
    [InlineData(0.5, 0.5, false)]
    [InlineData(0.51, 0.5, false)]
    public void RollTrigger_MidRangeProbability_ComparesAgainstSourceOutput(double sourceValue, double probability, bool expected)
    {
        var random = new FixedChaosRandomSource(sourceValue);

        Assert.Equal(expected, random.RollTrigger(probability));
    }

    [Fact]
    public void NextDelay_PicksUniformlyWithinRange()
    {
        var random = new FixedChaosRandomSource(0.25);
        var min = TimeSpan.FromSeconds(1);
        var max = TimeSpan.FromSeconds(5);

        var delay = random.NextDelay(min, max);

        Assert.Equal(TimeSpan.FromSeconds(2), delay);
    }

    [Fact]
    public void NextDelay_MaxNotGreaterThanMin_ReturnsMinUnchanged()
    {
        var random = new FixedChaosRandomSource(0.9);
        var min = TimeSpan.FromSeconds(3);

        Assert.Equal(min, random.NextDelay(min, min));
        Assert.Equal(min, random.NextDelay(min, TimeSpan.FromSeconds(1)));
    }
}
