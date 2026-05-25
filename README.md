# Llmed

A minimal, dependency-light in-process mediator for .NET — MediatR-style request/response with pipeline behaviors, hand-rolled and MIT-licensed.

Targets `net8.0`. Single runtime dependency: `Microsoft.Extensions.DependencyInjection.Abstractions` (+ `Microsoft.Extensions.Options` for the built-in concurrency-limit behavior).

## Install

```powershell
dotnet add package Llmed
```

## Define a request and a handler

```csharp
public sealed record Ping(string Message) : IRequest<string>;

public sealed class PingHandler : IRequestHandler<Ping, string>
{
    public Task<string> Handle(Ping request, CancellationToken ct) =>
        Task.FromResult($"pong:{request.Message}");
}
```

## Wire it up

```csharp
var services = new ServiceCollection();
services.AddMediator(typeof(Program).Assembly);
var sp = services.BuildServiceProvider();

var mediator = sp.GetRequiredService<IMediator>();
var result = await mediator.Send(new Ping("hello")); // "pong:hello"
```

## Add a pipeline behavior

```csharp
public sealed class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken ct)
    {
        Console.WriteLine($"-> {typeof(TRequest).Name}");
        var response = await next();
        Console.WriteLine($"<- {typeof(TRequest).Name}");
        return response;
    }
}

services.AddMediator(typeof(Program).Assembly)
        .AddBehavior(typeof(LoggingBehavior<,>));
```

Behaviors execute in registration order (first registered = outermost in the chain).

## Gate concurrency

Useful for serializing calls to a single-forward-pass model resource.

```csharp
services.AddConcurrencyLimit(maxConcurrency: 1);
services.AddMediator(typeof(Program).Assembly)
        .AddBehavior(typeof(ConcurrencyLimitBehavior<,>));
```

## Isolate two mediators in one process

For Clean Architecture setups where (for example) AI-tool orchestration and domain logic should not share a behavior stack:

```csharp
services.AddKeyedMediator("ai", typeof(AiHandlers).Assembly)
        .AddBehavior(typeof(AiTracingBehavior<,>));

services.AddKeyedMediator("domain", typeof(DomainHandlers).Assembly)
        .AddBehavior(typeof(DomainValidationBehavior<,>));

// Resolve with [FromKeyedServices("ai")] IMediator mediator
```

The two mediators do not see each other's handlers or behaviors.

## Observability

Each `Send` emits an `ActivitySource("Llmed.Mediator")` activity named `Mediator.Send` with a `request.type` tag. Attach an `ActivityListener` (or use OpenTelemetry's `AddSource("Llmed.Mediator")`) to capture.

## License

MIT.
