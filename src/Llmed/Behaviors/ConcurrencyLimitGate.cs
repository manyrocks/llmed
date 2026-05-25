using Microsoft.Extensions.Options;

namespace Llmed.Behaviors;

// Non-generic singleton owns the SemaphoreSlim. Each closed generic of an
// open-generic type has its own static fields in .NET, so the gate cannot
// live as a static field on the behavior itself — it would not be shared
// across request types.
public sealed class ConcurrencyLimitGate : IDisposable
{
    public ConcurrencyLimitGate(IOptions<ConcurrencyLimitOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var max = options.Value.MaxConcurrency;
        if (max < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"{nameof(ConcurrencyLimitOptions.MaxConcurrency)} must be >= 1, got {max}.");
        }
        Semaphore = new SemaphoreSlim(max, max);
    }

    public SemaphoreSlim Semaphore { get; }

    public void Dispose() => Semaphore.Dispose();
}
