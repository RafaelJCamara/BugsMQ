namespace VSaga.Core.Dsl;

/// <summary>
/// Bounded, in-process retry for a single step's actions — for transient technical failures (e.g. a
/// broker connection blip), not saga-level business failures. Re-running a step re-runs ALL of its
/// actions from the start, so actions should tolerate being invoked more than once for the same message.
/// </summary>
public sealed class RetryPolicy
{
    public int MaxAttempts { get; }

    public TimeSpan BaseDelay { get; }

    private RetryPolicy(int maxAttempts, TimeSpan baseDelay)
    {
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "At least one attempt is required.");

        MaxAttempts = maxAttempts;
        BaseDelay = baseDelay;
    }

    /// <summary>Single attempt, no retry.</summary>
    public static readonly RetryPolicy None = new(1, TimeSpan.Zero);

    public static RetryPolicy Exponential(int maxAttempts, TimeSpan baseDelay) => new(maxAttempts, baseDelay);

    internal TimeSpan DelayForAttempt(int attempt) =>
        TimeSpan.FromMilliseconds(BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
}
