namespace Llmed.Behaviors;

public sealed class ConcurrencyLimitBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ConcurrencyLimitGate _gate;

    public ConcurrencyLimitBehavior(ConcurrencyLimitGate gate) => _gate = gate;

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken ct)
    {
        await _gate.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await next().ConfigureAwait(false);
        }
        finally
        {
            _gate.Semaphore.Release();
        }
    }
}
