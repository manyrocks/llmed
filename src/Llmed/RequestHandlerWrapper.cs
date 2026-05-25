using Microsoft.Extensions.DependencyInjection;

namespace Llmed;

internal abstract class RequestHandlerWrapperBase
{
}

internal abstract class RequestHandlerWrapper<TResponse> : RequestHandlerWrapperBase
{
    public abstract Task<TResponse> Handle(
        IRequest<TResponse> request,
        IServiceProvider sp,
        object? serviceKey,
        IReadOnlyList<Type> behaviorTypes,
        CancellationToken ct);
}

internal sealed class RequestHandlerWrapper<TRequest, TResponse>
    : RequestHandlerWrapper<TResponse>
    where TRequest : IRequest<TResponse>
{
    public override Task<TResponse> Handle(
        IRequest<TResponse> request,
        IServiceProvider sp,
        object? serviceKey,
        IReadOnlyList<Type> behaviorTypes,
        CancellationToken ct)
    {
        var typed = (TRequest)request;

        // serviceKey == null -> plain DI; non-null -> keyed DI (one container,
        // multiple isolated mediators). Handler and behaviors live in the same
        // (keyed or unkeyed) service namespace as their parent mediator.
        var handler = serviceKey is null
            ? sp.GetRequiredService<IRequestHandler<TRequest, TResponse>>()
            : sp.GetRequiredKeyedService<IRequestHandler<TRequest, TResponse>>(serviceKey);

        RequestHandlerDelegate<TResponse> chain = () => handler.Handle(typed, ct);

        // Fold behaviors in REVERSE so registration order = outermost order.
        // For behaviors [B1, B2, B3] we want: B1 -> B2 -> B3 -> handler.
        // Walking back-to-front, each iteration wraps the running 'chain' as
        // the 'next' parameter of the more-outer behavior; after the loop the
        // returned delegate is B1's Handle with B2's wrapped delegate as next.
        for (int i = behaviorTypes.Count - 1; i >= 0; i--)
        {
            var behaviorType = behaviorTypes[i]
                .MakeGenericType(typeof(TRequest), typeof(TResponse));
            var behavior = (IPipelineBehavior<TRequest, TResponse>)(serviceKey is null
                ? sp.GetRequiredService(behaviorType)
                : sp.GetRequiredKeyedService(behaviorType, serviceKey));
            var next = chain;
            chain = () => behavior.Handle(typed, next, ct);
        }

        return chain();
    }
}
