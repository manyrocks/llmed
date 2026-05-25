using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Llmed;

public sealed class OrchestratorMediator : IMediator
{
    private static readonly MethodInfo SendInternalMethod = typeof(OrchestratorMediator)
        .GetMethod(nameof(SendInternal), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo PublishInternalMethod = typeof(OrchestratorMediator)
        .GetMethod(nameof(PublishInternal), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly Func<Type, object?> _singleResolver;
    private readonly Func<Type, IEnumerable<object>> _multiResolver;

    public OrchestratorMediator(
        Func<Type, object?> singleResolver,
        Func<Type, IEnumerable<object>> multiResolver)
    {
        _singleResolver = singleResolver ?? throw new ArgumentNullException(nameof(singleResolver));
        _multiResolver = multiResolver ?? throw new ArgumentNullException(nameof(multiResolver));
    }

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sendMethod = SendInternalMethod.MakeGenericMethod(request.GetType(), typeof(TResponse));
        try
        {
            return (Task<TResponse>)sendMethod.Invoke(this, [request, cancellationToken])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    public Task Publish<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : IEvent
    {
        ArgumentNullException.ThrowIfNull(@event);
        return PublishInternal(@event, cancellationToken);
    }

    public Task Publish(IEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var publishMethod = PublishInternalMethod.MakeGenericMethod(@event.GetType());
        try
        {
            return (Task)publishMethod.Invoke(this, [@event, cancellationToken])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private Task<TResponse> SendInternal<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken)
        where TRequest : IRequest<TResponse>
    {
        var handlerType = typeof(IRequestHandler<TRequest, TResponse>);
        var handler = _singleResolver(handlerType) as IRequestHandler<TRequest, TResponse>;
        if (handler is null)
        {
            throw new InvalidOperationException($"No handler registered for request '{typeof(TRequest).Name}'.");
        }

        var behaviors = _multiResolver(typeof(IPipelineBehavior<TRequest, TResponse>))
            .Cast<IPipelineBehavior<TRequest, TResponse>>()
            .Reverse();

        RequestHandlerDelegate<TResponse> pipeline = () => handler.Handle(request, cancellationToken);

        foreach (var behavior in behaviors)
        {
            var next = pipeline;
            pipeline = () => behavior.Handle(request, next, cancellationToken);
        }

        return pipeline();
    }

    private Task PublishInternal<TEvent>(TEvent @event, CancellationToken cancellationToken)
        where TEvent : IEvent
    {
        var handlers = _multiResolver(typeof(IEventHandler<TEvent>))
            .Cast<IEventHandler<TEvent>>()
            .Select(handler => handler.Handle(@event, cancellationToken));

        return Task.WhenAll(handlers);
    }
}
