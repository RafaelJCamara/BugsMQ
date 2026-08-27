namespace VSaga.Chaos;

/// <summary>
/// Testability seam around randomness for fault probability rolls and delay-range picking — unit
/// tests substitute a deterministic source instead of depending on <see cref="Random.Shared"/>'s
/// actual output.
/// </summary>
public interface IChaosRandomSource
{
    /// <summary>A pseudo-random value in [0, 1) — same contract as <see cref="Random.NextDouble"/>.</summary>
    double NextDouble();
}

/// <summary>Default production source: <see cref="Random.Shared"/>, safe for concurrent use across the transport's publish/subscribe callers.</summary>
public sealed class ThreadRandomChaosSource : IChaosRandomSource
{
    public double NextDouble() => Random.Shared.NextDouble();
}

public static class ChaosRandomSourceExtensions
{
    /// <summary>True with probability <paramref name="probability"/>, clamped to [0, 1] — 0 never triggers, 1 always does (and skips the roll entirely).</summary>
    public static bool RollTrigger(this IChaosRandomSource random, double probability) =>
        probability switch
        {
            <= 0 => false,
            >= 1 => true,
            _ => random.NextDouble() < probability,
        };

    /// <summary>A uniformly distributed delay in [<paramref name="min"/>, <paramref name="max"/>]; returns <paramref name="min"/> unchanged if <paramref name="max"/> &lt;= <paramref name="min"/>.</summary>
    public static TimeSpan NextDelay(this IChaosRandomSource random, TimeSpan min, TimeSpan max)
    {
        if (max <= min)
            return min;

        var rangeTicks = (max - min).Ticks;
        return min + TimeSpan.FromTicks((long)(rangeTicks * random.NextDouble()));
    }
}
