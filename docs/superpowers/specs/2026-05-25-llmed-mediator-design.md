# Llmed — Minimal In-Process Mediator (Design)

**Date:** 2026-05-25
**Status:** Approved for implementation
**Target:** net8.0, MIT

## Goal

A small, dependency-light mediator library for an in-process message pipeline: MediatR-style request/response with pipeline behaviors, hand-rolled to avoid the MediatR license. Designed for use as both a standalone orchestration tool (the named use case is gating calls to a single-forward-pass model resource) and as a building block inside larger Clean Architecture application layers.

## Non-goals (v1)

- `Publish` / notifications (one-to-many dispatch)
- Streaming requests (`IStreamRequest<T>` → `IAsyncEnumerable<T>`)
- Source-generated dispatch
- Pre/post processors (separate from behaviors)
- Companion behaviors package (`Llmed.Behaviors` with retry/timeout/logging)

All of the above are recorded in **Future considerations** below.

## Requirements

1. **Send dispatch** — `IMediator.Send(IRequest<TResponse>, CancellationToken)` resolves exactly one handler by the request's runtime type and invokes it through an ordered chain of pipeline behaviors.
2. **Behavior chain via fold** — On each `Send`, resolve the handler plus the ordered behaviors and fold them into a nested `RequestHandlerDelegate` chain that wraps the handler call. Behaviors execute in registration order (first registered = outermost in the chain). The fold itself is the one non-obvious bit and is commented inline.
3. **Fully async** — `Task`-based throughout; `CancellationToken` honored end to end.
4. **Concurrency-safe** — `IMediator` is a stateless singleton, safe to call `Send` from many threads/tasks concurrently. No shared mutable state in the dispatcher beyond a `ConcurrentDictionary` cache.
5. **Open-generic behaviors** — Behaviors are registered as open generic type definitions so one behavior applies across all request types.
6. **DI integration** — `services.AddMediator(params Assembly[])` returns a `MediatorBuilder` for ordered `.AddBehavior(...)` calls. Handlers are scanned and registered transient. Handlers are resolved per-`Send` from a fresh scope.
7. **Instance isolation** — The library MUST support multiple independent mediator instances in a single process, each with its own behavior stack and handler set, with no crosstalk. Supported via either (a) separate DI containers or (b) `services.AddKeyedMediator(key, ...)` in a single container.
8. **Built-in `ConcurrencyLimitBehavior<TRequest, TResponse>`** — Configurable via `ConcurrencyLimitOptions { int MaxConcurrency = 1 }` through the options pattern. Backed by a single shared `SemaphoreSlim` so the gate applies across all request types routed through the behavior.
9. **Observability** — `ActivitySource("Llmed.Mediator")` started per `Send`, with `request.type` tag. Zero cost when no listener is attached.
10. **Footprint** — Library code in the ballpark of 150–250 LOC, clean and readable. Single runtime dependency on `Microsoft.Extensions.DependencyInjection.Abstractions` plus `Microsoft.Extensions.Options` for the built-in behavior.

## Architecture

Three logical layers, all under the `Llmed` namespace:

1. **Public contracts** — `IRequest<TResponse>`, `IRequestHandler<TRequest, TResponse>`, `RequestHandlerDelegate<TResponse>`, `IPipelineBehavior<TRequest, TResponse>`, `IMediator`. Exactly the surface in the PRD.
2. **Dispatcher** — `Mediator` class. Stateless singleton. Holds an `IServiceProvider` (root) and a `ConcurrentDictionary<Type, RequestHandlerWrapperBase>` per-request-type invoker cache.
3. **DI integration** — `ServiceCollectionExtensions.AddMediator` and `AddKeyedMediator`, both returning a `MediatorBuilder` for fluent `.AddBehavior(typeof(X<,>))` calls.

### Public API surface

```csharp
namespace Llmed;

public interface IRequest<TResponse> { }

public interface IRequestHandler<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken ct);
}

public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();

public interface IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request,
                           RequestHandlerDelegate<TResponse> next,
                           CancellationToken ct);
}

public interface IMediator
{
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request,
                                    CancellationToken ct = default);
}
```

### Built-in behavior

```csharp
namespace Llmed.Behaviors;

public sealed class ConcurrencyLimitOptions
{
    public int MaxConcurrency { get; set; } = 1;
}

// Non-generic singleton owns the SemaphoreSlim. Each closed generic of an
// open-generic type has its own static fields in .NET, so the gate cannot
// live as a static field on the behavior itself — it would not be shared
// across request types.
public sealed class ConcurrencyLimitGate : IDisposable
{
    public ConcurrencyLimitGate(IOptions<ConcurrencyLimitOptions> options);
    public SemaphoreSlim Semaphore { get; }
    public void Dispose();
}

public sealed class ConcurrencyLimitBehavior<TRequest, TResponse>
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public ConcurrencyLimitBehavior(ConcurrencyLimitGate gate);
    // Wait on gate.Semaphore, call next, release in finally.
}
```

### DI surface

```csharp
namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static MediatorBuilder AddMediator(
        this IServiceCollection services,
        params Assembly[] handlerAssemblies);

    public static MediatorBuilder AddKeyedMediator(
        this IServiceCollection services,
        object serviceKey,
        params Assembly[] handlerAssemblies);
}

public sealed class MediatorBuilder
{
    public MediatorBuilder AddBehavior(Type openGenericBehaviorType);
}
```

DI extensions live under `Microsoft.Extensions.DependencyInjection` so they surface in IntelliSense for anyone with `services` in scope — standard MS convention.

## Dispatch internals

### The wrapper

`Send<TResponse>` knows `TResponse` but only `request.GetType()` for the request type. To bridge into strongly-typed `IRequestHandler<TRequest, TResponse>` without per-call reflection, the dispatcher uses a wrapper pattern:

```csharp
internal abstract class RequestHandlerWrapperBase { }

internal abstract class RequestHandlerWrapper<TResponse> : RequestHandlerWrapperBase
{
    public abstract Task<TResponse> Handle(IRequest<TResponse> request,
                                           IServiceProvider sp,
                                           object? serviceKey,
                                           IReadOnlyList<Type> behaviorTypes,
                                           CancellationToken ct);
}

internal sealed class RequestHandlerWrapper<TRequest, TResponse>
    : RequestHandlerWrapper<TResponse>
    where TRequest : IRequest<TResponse>
{
    public override Task<TResponse> Handle(IRequest<TResponse> request,
                                           IServiceProvider sp,
                                           object? serviceKey,
                                           IReadOnlyList<Type> behaviorTypes,
                                           CancellationToken ct)
    {
        var typed = (TRequest)request;

        // serviceKey == null → plain DI; non-null → keyed DI (one container,
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
```

The wrapper instance is built once per request type via reflection and cached in `ConcurrentDictionary<Type, RequestHandlerWrapperBase>`. Subsequent `Send` calls for the same request type hit the cache.

### The Send method

```csharp
public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request,
                                             CancellationToken ct = default)
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
    return await wrapper.Handle(request, scope.ServiceProvider, _serviceKey,
                                _behaviorTypes, ct).ConfigureAwait(false);
}
```

`_behaviorTypes` is an `IReadOnlyList<Type>` captured at construction from the builder. Immutable after build — safe for concurrent reads.

## DI registration

### `AddMediator(params Assembly[])`

1. Scans the supplied assemblies for concrete, non-abstract classes implementing `IRequestHandler<,>` (closed generic).
2. Registers each as transient against its closed-generic handler interface.
3. Creates a `MediatorBuilder` holding an ordered `List<Type>` of behavior types. Registers the builder itself as a singleton so `Mediator` can read its frozen list at construction time.
4. Registers `IMediator` as singleton via factory that pulls the builder and constructs `Mediator` with `serviceKey: null` and the builder's behavior list. The list is mutable until first `IMediator` resolution (typically at first `Send`); behaviors added afterward have no effect.
5. Returns the builder for fluent `.AddBehavior(...)` chaining.

### `MediatorBuilder.AddBehavior(Type openGenericBehaviorType)`

The builder carries the `IServiceCollection` it was created against plus its own `object? serviceKey` (null for `AddMediator`, set for `AddKeyedMediator`) so it knows whether to register keyed or unkeyed.

1. Validates the type is an open generic type definition (`IsGenericTypeDefinition`) with exactly two type parameters and implements `IPipelineBehavior<,>`.
2. Appends to the ordered behavior list.
3. Registers the open-generic behavior type as transient against `IPipelineBehavior<,>` — `AddTransient` if `serviceKey` is null, `AddKeyedTransient` with `serviceKey` otherwise. Keyed open generics are supported in .NET 8+.
4. Returns `this`.

### `AddKeyedMediator(object key, params Assembly[])`

Same flow as `AddMediator`, but every registration (handlers, behaviors, `IMediator`) is keyed under `key`. Two keyed mediators in the same container do not see each other's handlers or behaviors. The keyed `IMediator` resolves its dependencies from the keyed service map.

### `ConcurrencyLimitBehavior` wiring

```csharp
services.Configure<ConcurrencyLimitOptions>(o => o.MaxConcurrency = 1);
services.AddSingleton<ConcurrencyLimitGate>();
services.AddMediator(typeof(Program).Assembly)
        .AddBehavior(typeof(ConcurrencyLimitBehavior<,>));
```

`ConcurrencyLimitGate` is a non-generic singleton that owns the `SemaphoreSlim` (sized from `IOptions<ConcurrencyLimitOptions>` in its constructor). The behavior takes `ConcurrencyLimitGate` via DI, so every closed generic of `ConcurrencyLimitBehavior<TRequest, TResponse>` shares the same gate — one process-wide limit across all request types routed through the behavior. This matches the named use case of gating a single forward-pass model resource.

A small helper extension is offered for convenience:

```csharp
public static IServiceCollection AddConcurrencyLimit(
    this IServiceCollection services, int maxConcurrency)
{
    services.Configure<ConcurrencyLimitOptions>(o => o.MaxConcurrency = maxConcurrency);
    services.AddSingleton<ConcurrencyLimitGate>();
    return services;
}
```

## Observability

`MediatorDiagnostics.ActivitySource` is a `static readonly ActivitySource` named `"Llmed.Mediator"`, versioned via `typeof(MediatorDiagnostics).Assembly.GetName().Version?.ToString()`. Each `Send` starts an activity named `"Mediator.Send"` with a `request.type` tag. When no listener is attached, `StartActivity` returns null and the cost is a null check.

## Error semantics

| Condition | Behavior |
|---|---|
| `request == null` | `ArgumentNullException` from `Send` |
| No handler registered for the request type | `InvalidOperationException` from `GetRequiredService` (propagated) |
| Duplicate handler registrations | Last registration wins (standard DI behavior) — documented |
| Behavior or handler throws | Exception propagates up the chain unchanged |
| `CancellationToken` cancelled mid-flight | `OperationCanceledException` propagates from whichever component honored it |

## Project layout

```
llmed/
  Llmed.sln
  LICENSE
  README.md
  src/
    Llmed/
      Llmed.csproj
      IRequest.cs
      IRequestHandler.cs
      IPipelineBehavior.cs
      IMediator.cs
      Mediator.cs
      MediatorBuilder.cs
      MediatorDiagnostics.cs
      RequestHandlerWrapper.cs
      ServiceCollectionExtensions.cs
      Behaviors/
        ConcurrencyLimitBehavior.cs
        ConcurrencyLimitGate.cs
        ConcurrencyLimitOptions.cs
        ConcurrencyLimitServiceCollectionExtensions.cs
  tests/
    Llmed.Tests/
      Llmed.Tests.csproj
      MediatorTests.cs
      PipelineOrderingTests.cs
      ConcurrencyLimitBehaviorTests.cs
      ConcurrentSendStressTests.cs
      MultipleMediatorsAreIsolatedTests.cs
      ActivitySourceTests.cs
  docs/
    superpowers/
      specs/
        2026-05-25-llmed-mediator-design.md
```

## Tests

xUnit, in `tests/Llmed.Tests/`:

- **`MediatorTests`** — `Send` reaches the registered handler; missing handler throws `InvalidOperationException`; `null` request throws `ArgumentNullException`.
- **`PipelineOrderingTests`** — register three behaviors that each append their name pre- and post-`next` to a shared list. Assert the resulting trace is `[B1-pre, B2-pre, B3-pre, handler, B3-post, B2-post, B1-post]`.
- **`ConcurrencyLimitBehaviorTests`** — `MaxConcurrency = 1`; handler awaits a `TaskCompletionSource`; second `Send` blocks until the first completes. Separate test: cancellation aborts a waiter cleanly.
- **`ConcurrentSendStressTests`** — 1000 parallel `Send` calls across multiple request types with no behavior gate. Assert all complete; assert the wrapper cache has one entry per distinct request type.
- **`MultipleMediatorsAreIsolatedTests`** — register two `AddKeyedMediator` instances with different behavior stacks. Send the same request through both; assert each only ran its own behaviors and that one mediator's handlers are not visible to the other.
- **`ActivitySourceTests`** — attach an `ActivityListener`, send a request, assert one activity named `"Mediator.Send"` is captured with the correct `request.type` tag.

## Future considerations

Recorded here so they are on the record without expanding v1 scope:

- **`Publish` / notifications** — `INotification`, `INotificationHandler<T>`, `Publish()` with pluggable strategy (sequential default, `Task.WhenAll` parallel option).
- **`IStreamRequest<T>` → `IAsyncEnumerable<T>`** — natural fit for LLM token streaming; needs a parallel streaming pipeline (`IStreamPipelineBehavior<,>`).
- **`Llmed.Behaviors` companion package** — `RetryBehavior`, `TimeoutBehavior`, `LoggingBehavior`, `ValidationBehavior` (FluentValidation-style) as opt-in extras.
- **Source-generated dispatch** — eliminates reflection cache entirely. Revisit only if profiling shows the cache as a real bottleneck (unlikely for the gating use case).
- **Per-keyed-mediator concurrency gates** — currently `ConcurrencyLimitBehavior` has one shared semaphore process-wide; a keyed variant could give each mediator its own gate.
