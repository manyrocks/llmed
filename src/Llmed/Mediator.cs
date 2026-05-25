using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Llmed;

internal sealed class Mediator : IMediator
{
    private readonly IServiceProvider _root;
    private readonly object? _serviceKey;
    private readonly IReadOnlyList<Type> _behaviorTypes;
    private readonly ConcurrentDictionary<Type, RequestHandlerWrapperBase> _wrapperCache = new();

    public Mediator(IServiceProvider root, object? serviceKey, IReadOnlyList<Type> behaviorTypes)
    {
        _root = root;
        _serviceKey = serviceKey;
        _behaviorTypes = behaviorTypes;
    }

    public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = MediatorDiagnostics.ActivitySource.StartActivity("Mediator.Send");
        activity?.SetTag("request.type", request.GetType().FullName);

        // Use the GetOrAdd<TArg> overload that passes state as an argument so the
        // factory stays static — no per-call closure allocation on cache misses.
        var wrapper = (RequestHandlerWrapper<TResponse>)_wrapperCache.GetOrAdd(
            request.GetType(),
            static (reqType, respType) =>
            {
                var wrapperType = typeof(RequestHandlerWrapper<,>)
                    .MakeGenericType(reqType, respType);
                return (RequestHandlerWrapperBase)Activator.CreateInstance(wrapperType)!;
            },
            typeof(TResponse));

        using var scope = _root.CreateScope();
        return await wrapper
            .Handle(request, scope.ServiceProvider, _serviceKey, _behaviorTypes, ct)
            .ConfigureAwait(false);
    }

    // Test-only accessor (internal) so ConcurrentSendStressTests can assert
    // the cache holds exactly one wrapper per distinct request type.
    internal int WrapperCacheCount => _wrapperCache.Count;
}
