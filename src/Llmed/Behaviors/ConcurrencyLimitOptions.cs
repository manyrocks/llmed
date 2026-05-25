namespace Llmed.Behaviors;

public sealed class ConcurrencyLimitOptions
{
    public int MaxConcurrency { get; set; } = 1;
}
