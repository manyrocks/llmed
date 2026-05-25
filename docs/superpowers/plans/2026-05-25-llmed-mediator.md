# Llmed Mediator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the minimal in-process mediator library described in `docs/superpowers/specs/2026-05-25-llmed-mediator-design.md` — Send-only request/response with pipeline behaviors, keyed-mediator isolation, a built-in `ConcurrencyLimitBehavior`, and `ActivitySource` observability.

**Architecture:** Classic MediatR-style closure-fold dispatch. `IMediator.Send` looks up a per-request-type wrapper (cached in a `ConcurrentDictionary`), creates a DI scope, resolves the handler and ordered open-generic behaviors, folds them right-to-left into a `RequestHandlerDelegate` chain, and awaits the chain. DI registration is fluent via a `MediatorBuilder`; an `AddKeyedMediator` overload uses .NET 8 keyed services for in-process isolation. Built-in `ConcurrencyLimitBehavior` delegates to a non-generic `ConcurrencyLimitGate` singleton so the `SemaphoreSlim` is shared across all closed generics of the behavior.

**Tech Stack:** .NET 8 (C# 12), `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Options`, `System.Diagnostics.DiagnosticSource` (for `ActivitySource`). Tests use xUnit + `Microsoft.NET.Test.Sdk` + `xunit.runner.visualstudio`.

---

## File Structure

Each library file has a single responsibility; tests are split one file per behavior axis so failures point you straight at the cause.

**Library (`src/Llmed/`):**
- `IRequest.cs` — marker interface for requests
- `IRequestHandler.cs` — handler contract
- `IPipelineBehavior.cs` — behavior contract and `RequestHandlerDelegate` delegate
- `IMediator.cs` — public dispatch contract
- `Mediator.cs` — singleton dispatcher; holds the wrapper cache
- `MediatorBuilder.cs` — fluent registration; holds the ordered behavior list and its key (if any)
- `RequestHandlerWrapper.cs` — internal wrapper hierarchy that bridges from `object`-typed `Send` into closed-generic handler/behavior calls and contains the fold
- `MediatorDiagnostics.cs` — single `ActivitySource` instance
- `ServiceCollectionExtensions.cs` — `AddMediator` and `AddKeyedMediator` DI entry points
- `Behaviors/ConcurrencyLimitOptions.cs` — options POCO
- `Behaviors/ConcurrencyLimitGate.cs` — non-generic singleton owning the `SemaphoreSlim`
- `Behaviors/ConcurrencyLimitBehavior.cs` — open-generic behavior that waits on the gate
- `Behaviors/ConcurrencyLimitServiceCollectionExtensions.cs` — `AddConcurrencyLimit(int)` helper

**Tests (`tests/Llmed.Tests/`):**
- `MediatorTests.cs` — happy-path send, missing-handler, null-argument
- `PipelineOrderingTests.cs` — pre/post ordering across three behaviors
- `ConcurrencyLimitBehaviorTests.cs` — gating and cancellation
- `ConcurrentSendStressTests.cs` — 1000 parallel sends, cache shape
- `MultipleMediatorsAreIsolatedTests.cs` — two keyed mediators do not see each other
- `ActivitySourceTests.cs` — listener captures `Mediator.Send` activity with tag

---

## Task 1: Solution and project skeleton

**Files:**
- Create: `Llmed.sln`
- Create: `src/Llmed/Llmed.csproj`
- Create: `tests/Llmed.Tests/Llmed.Tests.csproj`
- Create: `.gitignore`

- [ ] **Step 1: Create a .NET-friendly `.gitignore`**

```
# Build outputs
bin/
obj/
*.user

# IDE
.vs/
.idea/
*.suo

# Test results
TestResults/
coverage.*
```

- [ ] **Step 2: Create the library project**

Write `src/Llmed/Llmed.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <PackageId>Llmed</PackageId>
    <Description>A minimal, dependency-light in-process mediator for .NET.</Description>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="8.0.2" />
    <PackageReference Include="Microsoft.Extensions.Options" Version="8.0.2" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Create the test project**

Write `tests/Llmed.Tests/Llmed.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.1" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\Llmed\Llmed.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 4: Create the solution and add both projects**

Run from the repo root:

```powershell
dotnet new sln -n Llmed
dotnet sln add src/Llmed/Llmed.csproj
dotnet sln add tests/Llmed.Tests/Llmed.Tests.csproj
```

- [ ] **Step 5: Restore + build, expect success**

Run:

```powershell
dotnet build
```

Expected: `Build succeeded.` with zero errors and zero warnings. (No source files yet — only project metadata.)

- [ ] **Step 6: Commit**

```powershell
git add .gitignore Llmed.sln src/Llmed/Llmed.csproj tests/Llmed.Tests/Llmed.Tests.csproj
git commit -m "chore: scaffold Llmed solution, library, and xUnit test project"
```

---

## Task 2: Public contracts

**Files:**
- Create: `src/Llmed/IRequest.cs`
- Create: `src/Llmed/IRequestHandler.cs`
- Create: `src/Llmed/IPipelineBehavior.cs`
- Create: `src/Llmed/IMediator.cs`

These compile-only — no tests yet (tests come with the dispatcher).

- [ ] **Step 1: Write `IRequest.cs`**

```csharp
namespace Llmed;

public interface IRequest<TResponse>
{
}
```

- [ ] **Step 2: Write `IRequestHandler.cs`**

```csharp
namespace Llmed;

public interface IRequestHandler<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(TRequest request, CancellationToken ct);
}
```

- [ ] **Step 3: Write `IPipelineBehavior.cs`**

```csharp
namespace Llmed;

public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();

public interface IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken ct);
}
```

- [ ] **Step 4: Write `IMediator.cs`**

```csharp
namespace Llmed;

public interface IMediator
{
    Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken ct = default);
}
```

- [ ] **Step 5: Build, expect success**

Run:

```powershell
dotnet build
```

Expected: `Build succeeded.` zero warnings.

- [ ] **Step 6: Commit**

```powershell
git add src/Llmed/IRequest.cs src/Llmed/IRequestHandler.cs src/Llmed/IPipelineBehavior.cs src/Llmed/IMediator.cs
git commit -m "feat: add core mediator contracts (IRequest, IRequestHandler, IPipelineBehavior, IMediator)"
```

---

## Task 3: Diagnostics — `MediatorDiagnostics`

**Files:**
- Create: `src/Llmed/MediatorDiagnostics.cs`

- [ ] **Step 1: Write `MediatorDiagnostics.cs`**

```csharp
using System.Diagnostics;

namespace Llmed;

internal static class MediatorDiagnostics
{
    public const string ActivitySourceName = "Llmed.Mediator";

    public static readonly ActivitySource ActivitySource = new(
        ActivitySourceName,
        typeof(MediatorDiagnostics).Assembly.GetName().Version?.ToString() ?? "0.0.0");
}
```

- [ ] **Step 2: Build, expect success**

Run: `dotnet build`. Expected: `Build succeeded.` zero warnings.

- [ ] **Step 3: Commit**

```powershell
git add src/Llmed/MediatorDiagnostics.cs
git commit -m "feat: add MediatorDiagnostics ActivitySource"
```

---

## Task 4: Request handler wrapper (the fold)

This is the hardest piece in the library. We build it standalone before wiring it into `Mediator` so we can reason about it in isolation. There are no direct tests at this step — the wrapper is `internal` and gets exercised through `Mediator` tests in Task 6. Build correctness is verified by the compiler; behavior correctness is verified end-to-end.

**Files:**
- Create: `src/Llmed/RequestHandlerWrapper.cs`

- [ ] **Step 1: Write `RequestHandlerWrapper.cs`**

```csharp
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
```

- [ ] **Step 2: Build, expect success**

Run: `dotnet build`. Expected: `Build succeeded.` zero warnings.

- [ ] **Step 3: Commit**

```powershell
git add src/Llmed/RequestHandlerWrapper.cs
git commit -m "feat: add RequestHandlerWrapper with reverse-fold pipeline composition"
```

---

## Task 5: The `Mediator` class

The dispatcher itself. Holds the root `IServiceProvider`, the optional service key, the frozen behavior-type list, and the per-request-type wrapper cache.

**Files:**
- Create: `src/Llmed/Mediator.cs`

- [ ] **Step 1: Write `Mediator.cs`**

```csharp
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
```

- [ ] **Step 2: Expose internals to the test assembly**

Add an `InternalsVisibleTo` so the tests can see `Mediator` (the `internal` class) and `WrapperCacheCount`.

Edit `src/Llmed/Llmed.csproj`, adding inside the `<Project>` element after the existing `<ItemGroup>`:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="Llmed.Tests" />
  </ItemGroup>
```

- [ ] **Step 3: Build, expect success**

Run: `dotnet build`. Expected: `Build succeeded.` zero warnings.

- [ ] **Step 4: Commit**

```powershell
git add src/Llmed/Mediator.cs src/Llmed/Llmed.csproj
git commit -m "feat: add Mediator dispatcher with per-request-type wrapper cache"
```

---

## Task 6: `MediatorBuilder` and `ServiceCollectionExtensions` (unkeyed)

The fluent registration entry point. Keyed support is added in Task 9 — keep this task scoped to the unkeyed path to keep the change small.

**Files:**
- Create: `src/Llmed/MediatorBuilder.cs`
- Create: `src/Llmed/ServiceCollectionExtensions.cs`

- [ ] **Step 1: Write `MediatorBuilder.cs`**

```csharp
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Llmed;

public sealed class MediatorBuilder
{
    private readonly IServiceCollection _services;
    private readonly object? _serviceKey;
    private readonly List<Type> _behaviorTypes = new();

    internal MediatorBuilder(IServiceCollection services, object? serviceKey)
    {
        _services = services;
        _serviceKey = serviceKey;
    }

    internal IReadOnlyList<Type> BehaviorTypes => _behaviorTypes;

    public MediatorBuilder AddBehavior(Type openGenericBehaviorType)
    {
        ArgumentNullException.ThrowIfNull(openGenericBehaviorType);

        if (!openGenericBehaviorType.IsGenericTypeDefinition
            || openGenericBehaviorType.GetGenericArguments().Length != 2)
        {
            throw new ArgumentException(
                $"Behavior type must be an open generic with two type parameters: " +
                $"{openGenericBehaviorType.FullName}",
                nameof(openGenericBehaviorType));
        }

        var implementsBehaviorInterface = openGenericBehaviorType.GetInterfaces()
            .Any(i => i.IsGenericType
                      && i.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>));

        if (!implementsBehaviorInterface)
        {
            throw new ArgumentException(
                $"Behavior type must implement IPipelineBehavior<,>: " +
                $"{openGenericBehaviorType.FullName}",
                nameof(openGenericBehaviorType));
        }

        _behaviorTypes.Add(openGenericBehaviorType);

        if (_serviceKey is null)
        {
            _services.AddTransient(openGenericBehaviorType);
        }
        else
        {
            _services.AddKeyedTransient(openGenericBehaviorType, _serviceKey);
        }

        return this;
    }
}
```

- [ ] **Step 2: Write `ServiceCollectionExtensions.cs`**

```csharp
using System.Reflection;
using Llmed;

namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static MediatorBuilder AddMediator(
        this IServiceCollection services,
        params Assembly[] handlerAssemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(handlerAssemblies);

        var builder = new MediatorBuilder(services, serviceKey: null);

        RegisterHandlersFromAssemblies(services, handlerAssemblies, serviceKey: null);

        // Capture 'builder' in the closure so subsequent AddBehavior calls
        // (which mutate builder.BehaviorTypes) are visible when IMediator is
        // first resolved. The list is read once at Mediator construction; any
        // AddBehavior calls after the first Send have no effect.
        services.AddSingleton<IMediator>(sp =>
            new Mediator(sp, serviceKey: null, builder.BehaviorTypes));

        return builder;
    }

    internal static void RegisterHandlersFromAssemblies(
        IServiceCollection services,
        Assembly[] assemblies,
        object? serviceKey)
    {
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface)
                {
                    continue;
                }

                foreach (var iface in type.GetInterfaces())
                {
                    if (!iface.IsGenericType
                        || iface.GetGenericTypeDefinition() != typeof(IRequestHandler<,>))
                    {
                        continue;
                    }

                    if (serviceKey is null)
                    {
                        services.AddTransient(iface, type);
                    }
                    else
                    {
                        services.AddKeyedTransient(iface, serviceKey, type);
                    }
                }
            }
        }
    }
}
```

- [ ] **Step 3: Build, expect success**

Run: `dotnet build`. Expected: `Build succeeded.` zero warnings.

- [ ] **Step 4: Write the first failing test — `Send` reaches the handler**

Create `tests/Llmed.Tests/MediatorTests.cs`:

```csharp
using Llmed;
using Microsoft.Extensions.DependencyInjection;

namespace Llmed.Tests;

public class MediatorTests
{
    public sealed record Ping(string Message) : IRequest<string>;

    public sealed class PingHandler : IRequestHandler<Ping, string>
    {
        public Task<string> Handle(Ping request, CancellationToken ct) =>
            Task.FromResult($"pong:{request.Message}");
    }

    [Fact]
    public async Task Send_routes_to_registered_handler()
    {
        var services = new ServiceCollection();
        services.AddMediator(typeof(MediatorTests).Assembly);
        await using var sp = services.BuildServiceProvider();

        var mediator = sp.GetRequiredService<IMediator>();
        var result = await mediator.Send(new Ping("hello"));

        Assert.Equal("pong:hello", result);
    }
}
```

- [ ] **Step 5: Run, expect pass**

Run:

```powershell
dotnet test --filter "FullyQualifiedName~MediatorTests.Send_routes_to_registered_handler"
```

Expected: `Passed!  - Failed: 0, Passed: 1`.

- [ ] **Step 6: Add the missing-handler test**

Append to `MediatorTests.cs` inside the class:

```csharp
    public sealed record Unregistered : IRequest<int>;

    [Fact]
    public async Task Send_throws_InvalidOperationException_when_no_handler_registered()
    {
        var services = new ServiceCollection();
        // Pass an empty assembly array on purpose: no handlers will be scanned.
        services.AddMediator();
        await using var sp = services.BuildServiceProvider();

        var mediator = sp.GetRequiredService<IMediator>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mediator.Send(new Unregistered()));
    }
```

- [ ] **Step 7: Add the null-request test**

Append to `MediatorTests.cs` inside the class:

```csharp
    [Fact]
    public async Task Send_throws_ArgumentNullException_when_request_is_null()
    {
        var services = new ServiceCollection();
        services.AddMediator(typeof(MediatorTests).Assembly);
        await using var sp = services.BuildServiceProvider();

        var mediator = sp.GetRequiredService<IMediator>();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => mediator.Send<string>(null!));
    }
```

- [ ] **Step 8: Run all `MediatorTests`, expect three passing**

Run:

```powershell
dotnet test --filter "FullyQualifiedName~MediatorTests"
```

Expected: `Passed!  - Failed: 0, Passed: 3`.

- [ ] **Step 9: Commit**

```powershell
git add src/Llmed/MediatorBuilder.cs src/Llmed/ServiceCollectionExtensions.cs tests/Llmed.Tests/MediatorTests.cs
git commit -m "feat: add MediatorBuilder and AddMediator DI extension with handler scanning"
```

---

## Task 7: Pipeline-ordering test

Verify the fold direction. This task only adds a test — `AddBehavior` already works after Task 6.

**Files:**
- Create: `tests/Llmed.Tests/PipelineOrderingTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Llmed;
using Microsoft.Extensions.DependencyInjection;

namespace Llmed.Tests;

public class PipelineOrderingTests
{
    public sealed record Echo(string Value) : IRequest<string>;

    public sealed class EchoHandler : IRequestHandler<Echo, string>
    {
        public static readonly List<string> Trace = new();

        public Task<string> Handle(Echo request, CancellationToken ct)
        {
            lock (Trace) Trace.Add("handler");
            return Task.FromResult(request.Value);
        }
    }

    public sealed class TracingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        private readonly string _name;

        public TracingBehavior(string name) => _name = name;

        public async Task<TResponse> Handle(
            TRequest request,
            RequestHandlerDelegate<TResponse> next,
            CancellationToken ct)
        {
            lock (EchoHandler.Trace) EchoHandler.Trace.Add($"{_name}-pre");
            var response = await next();
            lock (EchoHandler.Trace) EchoHandler.Trace.Add($"{_name}-post");
            return response;
        }
    }

    public sealed class BehaviorB1<TRequest, TResponse> : TracingBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse> { public BehaviorB1() : base("B1") { } }

    public sealed class BehaviorB2<TRequest, TResponse> : TracingBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse> { public BehaviorB2() : base("B2") { } }

    public sealed class BehaviorB3<TRequest, TResponse> : TracingBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse> { public BehaviorB3() : base("B3") { } }

    [Fact]
    public async Task Behaviors_execute_in_registration_order_around_handler()
    {
        EchoHandler.Trace.Clear();

        var services = new ServiceCollection();
        services.AddMediator(typeof(PipelineOrderingTests).Assembly)
                .AddBehavior(typeof(BehaviorB1<,>))
                .AddBehavior(typeof(BehaviorB2<,>))
                .AddBehavior(typeof(BehaviorB3<,>));
        await using var sp = services.BuildServiceProvider();

        var mediator = sp.GetRequiredService<IMediator>();
        await mediator.Send(new Echo("x"));

        Assert.Equal(
            new[] { "B1-pre", "B2-pre", "B3-pre", "handler", "B3-post", "B2-post", "B1-post" },
            EchoHandler.Trace);
    }
}
```

- [ ] **Step 2: Run, expect pass**

Run:

```powershell
dotnet test --filter "FullyQualifiedName~PipelineOrderingTests"
```

Expected: `Passed!  - Failed: 0, Passed: 1`.

If this fails with reversed ordering, the fold direction in `RequestHandlerWrapper` is wrong. The comment in that file describes the intent; revisit before changing the test.

- [ ] **Step 3: Commit**

```powershell
git add tests/Llmed.Tests/PipelineOrderingTests.cs
git commit -m "test: assert pipeline behaviors execute in registration order"
```

---

## Task 8: `ConcurrencyLimitBehavior` and the gate

**Files:**
- Create: `src/Llmed/Behaviors/ConcurrencyLimitOptions.cs`
- Create: `src/Llmed/Behaviors/ConcurrencyLimitGate.cs`
- Create: `src/Llmed/Behaviors/ConcurrencyLimitBehavior.cs`
- Create: `src/Llmed/Behaviors/ConcurrencyLimitServiceCollectionExtensions.cs`
- Create: `tests/Llmed.Tests/ConcurrencyLimitBehaviorTests.cs`

- [ ] **Step 1: Write `ConcurrencyLimitOptions.cs`**

```csharp
namespace Llmed.Behaviors;

public sealed class ConcurrencyLimitOptions
{
    public int MaxConcurrency { get; set; } = 1;
}
```

- [ ] **Step 2: Write `ConcurrencyLimitGate.cs`**

```csharp
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
```

- [ ] **Step 3: Write `ConcurrencyLimitBehavior.cs`**

```csharp
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
```

- [ ] **Step 4: Write `ConcurrencyLimitServiceCollectionExtensions.cs`**

```csharp
using Llmed.Behaviors;

namespace Microsoft.Extensions.DependencyInjection;

public static class ConcurrencyLimitServiceCollectionExtensions
{
    public static IServiceCollection AddConcurrencyLimit(
        this IServiceCollection services,
        int maxConcurrency)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Configure<ConcurrencyLimitOptions>(o => o.MaxConcurrency = maxConcurrency);
        services.AddSingleton<ConcurrencyLimitGate>();
        return services;
    }
}
```

- [ ] **Step 5: Build, expect success**

Run: `dotnet build`. Expected: `Build succeeded.` zero warnings.

- [ ] **Step 6: Write the gating test**

Create `tests/Llmed.Tests/ConcurrencyLimitBehaviorTests.cs`:

```csharp
using Llmed;
using Llmed.Behaviors;
using Microsoft.Extensions.DependencyInjection;

namespace Llmed.Tests;

public class ConcurrencyLimitBehaviorTests
{
    public sealed record Gated(TaskCompletionSource Release) : IRequest<bool>;

    public sealed class GatedHandler : IRequestHandler<Gated, bool>
    {
        public async Task<bool> Handle(Gated request, CancellationToken ct)
        {
            await request.Release.Task.WaitAsync(ct);
            return true;
        }
    }

    [Fact]
    public async Task MaxConcurrency_1_serializes_two_concurrent_sends()
    {
        var services = new ServiceCollection();
        services.AddConcurrencyLimit(1);
        services.AddMediator(typeof(ConcurrencyLimitBehaviorTests).Assembly)
                .AddBehavior(typeof(ConcurrencyLimitBehavior<,>));
        await using var sp = services.BuildServiceProvider();

        var mediator = sp.GetRequiredService<IMediator>();

        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Stagger the sends so first deterministically acquires the gate before
        // second is issued. Without the delay, two concurrent Send calls could
        // race for the semaphore and the test would be order-dependent.
        var first = mediator.Send(new Gated(firstRelease));
        await Task.Delay(50);
        var second = mediator.Send(new Gated(secondRelease));
        await Task.Delay(50);

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        // Releasing the first unblocks its handler. Second then acquires the gate and waits.
        firstRelease.SetResult();
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(second.IsCompleted);

        secondRelease.SetResult();
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(2)));
    }
}
```

- [ ] **Step 7: Run gating test, expect pass**

Run:

```powershell
dotnet test --filter "FullyQualifiedName~ConcurrencyLimitBehaviorTests.MaxConcurrency_1_serializes_two_concurrent_sends"
```

Expected: `Passed!  - Failed: 0, Passed: 1`.

- [ ] **Step 8: Add the cancellation test**

Append to `ConcurrencyLimitBehaviorTests.cs` inside the class:

```csharp
    [Fact]
    public async Task Cancellation_aborts_a_waiter_cleanly()
    {
        var services = new ServiceCollection();
        services.AddConcurrencyLimit(1);
        services.AddMediator(typeof(ConcurrencyLimitBehaviorTests).Assembly)
                .AddBehavior(typeof(ConcurrencyLimitBehavior<,>));
        await using var sp = services.BuildServiceProvider();

        var mediator = sp.GetRequiredService<IMediator>();

        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = mediator.Send(new Gated(firstRelease));
        await Task.Delay(50); // first acquires the gate

        using var cts = new CancellationTokenSource();
        var second = mediator.Send(new Gated(
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)), cts.Token);

        await Task.Delay(50); // second is blocked waiting on the gate
        Assert.False(second.IsCompleted);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        // First is still alive and completes when released.
        firstRelease.SetResult();
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(2)));
    }
```

- [ ] **Step 9: Run all `ConcurrencyLimitBehaviorTests`, expect two passing**

Run:

```powershell
dotnet test --filter "FullyQualifiedName~ConcurrencyLimitBehaviorTests"
```

Expected: `Passed!  - Failed: 0, Passed: 2`.

- [ ] **Step 10: Commit**

```powershell
git add src/Llmed/Behaviors/ tests/Llmed.Tests/ConcurrencyLimitBehaviorTests.cs
git commit -m "feat: add ConcurrencyLimitBehavior backed by a non-generic gate"
```

---

## Task 9: `AddKeyedMediator` and isolation test

**Files:**
- Modify: `src/Llmed/ServiceCollectionExtensions.cs`
- Create: `tests/Llmed.Tests/MultipleMediatorsAreIsolatedTests.cs`

- [ ] **Step 1: Add `AddKeyedMediator` to `ServiceCollectionExtensions.cs`**

Inside the `ServiceCollectionExtensions` class, after the existing `AddMediator` method, add:

```csharp
    public static MediatorBuilder AddKeyedMediator(
        this IServiceCollection services,
        object serviceKey,
        params Assembly[] handlerAssemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        ArgumentNullException.ThrowIfNull(handlerAssemblies);

        var builder = new MediatorBuilder(services, serviceKey);

        RegisterHandlersFromAssemblies(services, handlerAssemblies, serviceKey);

        // Same closure-capture pattern as the unkeyed variant — see AddMediator
        // for why we don't put MediatorBuilder in DI itself.
        services.AddKeyedSingleton<IMediator>(serviceKey, (sp, _) =>
            new Mediator(sp, serviceKey, builder.BehaviorTypes));

        return builder;
    }
```

- [ ] **Step 2: Build, expect success**

Run: `dotnet build`. Expected: `Build succeeded.` zero warnings.

- [ ] **Step 3: Write the isolation test**

Create `tests/Llmed.Tests/MultipleMediatorsAreIsolatedTests.cs`:

```csharp
using Llmed;
using Microsoft.Extensions.DependencyInjection;

namespace Llmed.Tests;

public class MultipleMediatorsAreIsolatedTests
{
    public sealed record Probe(string From) : IRequest<List<string>>;

    public sealed class ProbeHandler : IRequestHandler<Probe, List<string>>
    {
        public Task<List<string>> Handle(Probe request, CancellationToken ct) =>
            Task.FromResult(new List<string> { $"handler:{request.From}" });
    }

    public sealed class TaggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        private readonly string _tag;

        public TaggingBehavior(string tag) => _tag = tag;

        public async Task<TResponse> Handle(
            TRequest request,
            RequestHandlerDelegate<TResponse> next,
            CancellationToken ct)
        {
            var result = await next();
            if (result is List<string> list)
            {
                list.Insert(0, $"behavior:{_tag}");
            }
            return result;
        }
    }

    public sealed class AiTagBehavior<TRequest, TResponse> : TaggingBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse> { public AiTagBehavior() : base("ai") { } }

    public sealed class DomainTagBehavior<TRequest, TResponse> : TaggingBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse> { public DomainTagBehavior() : base("domain") { } }

    [Fact]
    public async Task Two_keyed_mediators_have_independent_behavior_stacks()
    {
        var services = new ServiceCollection();
        services.AddKeyedMediator("ai", typeof(MultipleMediatorsAreIsolatedTests).Assembly)
                .AddBehavior(typeof(AiTagBehavior<,>));
        services.AddKeyedMediator("domain", typeof(MultipleMediatorsAreIsolatedTests).Assembly)
                .AddBehavior(typeof(DomainTagBehavior<,>));
        await using var sp = services.BuildServiceProvider();

        var ai = sp.GetRequiredKeyedService<IMediator>("ai");
        var domain = sp.GetRequiredKeyedService<IMediator>("domain");

        var aiResult = await ai.Send(new Probe("ai-caller"));
        var domainResult = await domain.Send(new Probe("domain-caller"));

        Assert.Equal(new[] { "behavior:ai", "handler:ai-caller" }, aiResult);
        Assert.Equal(new[] { "behavior:domain", "handler:domain-caller" }, domainResult);
    }

    [Fact]
    public async Task Keyed_mediator_handlers_are_not_visible_to_unkeyed_provider()
    {
        var services = new ServiceCollection();
        services.AddKeyedMediator("ai", typeof(MultipleMediatorsAreIsolatedTests).Assembly);
        await using var sp = services.BuildServiceProvider();

        // No unkeyed IMediator was registered.
        Assert.Null(sp.GetService<IMediator>());
        // And the handler is only resolvable via the key.
        Assert.Null(sp.GetService<IRequestHandler<Probe, List<string>>>());
        Assert.NotNull(sp.GetKeyedService<IRequestHandler<Probe, List<string>>>("ai"));
    }
}
```

- [ ] **Step 4: Run both tests, expect pass**

Run:

```powershell
dotnet test --filter "FullyQualifiedName~MultipleMediatorsAreIsolatedTests"
```

Expected: `Passed!  - Failed: 0, Passed: 2`.

- [ ] **Step 5: Commit**

```powershell
git add src/Llmed/ServiceCollectionExtensions.cs tests/Llmed.Tests/MultipleMediatorsAreIsolatedTests.cs
git commit -m "feat: add AddKeyedMediator for in-process mediator isolation"
```

---

## Task 10: Concurrent-send stress test

Verifies the wrapper cache holds exactly one entry per request type under heavy parallel use and that no requests get dropped or crossed.

**Files:**
- Create: `tests/Llmed.Tests/ConcurrentSendStressTests.cs`

- [ ] **Step 1: Write the test**

```csharp
using Llmed;
using Microsoft.Extensions.DependencyInjection;

namespace Llmed.Tests;

public class ConcurrentSendStressTests
{
    public sealed record TypeA(int N) : IRequest<int>;
    public sealed record TypeB(int N) : IRequest<int>;
    public sealed record TypeC(int N) : IRequest<int>;

    public sealed class HandlerA : IRequestHandler<TypeA, int>
    {
        public Task<int> Handle(TypeA r, CancellationToken ct) => Task.FromResult(r.N * 2);
    }

    public sealed class HandlerB : IRequestHandler<TypeB, int>
    {
        public Task<int> Handle(TypeB r, CancellationToken ct) => Task.FromResult(r.N * 3);
    }

    public sealed class HandlerC : IRequestHandler<TypeC, int>
    {
        public Task<int> Handle(TypeC r, CancellationToken ct) => Task.FromResult(r.N * 5);
    }

    [Fact]
    public async Task Thousand_parallel_sends_across_three_types_all_succeed_and_cache_has_three_entries()
    {
        var services = new ServiceCollection();
        services.AddMediator(typeof(ConcurrentSendStressTests).Assembly);
        await using var sp = services.BuildServiceProvider();

        var mediator = sp.GetRequiredService<IMediator>();
        var concrete = (Mediator)mediator;

        const int total = 1000;
        var tasks = new Task<int>[total];
        for (int i = 0; i < total; i++)
        {
            tasks[i] = (i % 3) switch
            {
                0 => mediator.Send(new TypeA(i)),
                1 => mediator.Send(new TypeB(i)),
                _ => mediator.Send(new TypeC(i)),
            };
        }

        var results = await Task.WhenAll(tasks);

        for (int i = 0; i < total; i++)
        {
            var expected = (i % 3) switch
            {
                0 => i * 2,
                1 => i * 3,
                _ => i * 5,
            };
            Assert.Equal(expected, results[i]);
        }

        // Exactly one wrapper per distinct request type, regardless of call count.
        Assert.Equal(3, concrete.WrapperCacheCount);
    }
}
```

- [ ] **Step 2: Run the test, expect pass**

Run:

```powershell
dotnet test --filter "FullyQualifiedName~ConcurrentSendStressTests"
```

Expected: `Passed!  - Failed: 0, Passed: 1`. (Runs in well under a second.)

- [ ] **Step 3: Commit**

```powershell
git add tests/Llmed.Tests/ConcurrentSendStressTests.cs
git commit -m "test: stress 1000 parallel sends across three request types"
```

---

## Task 11: `ActivitySource` test

**Files:**
- Create: `tests/Llmed.Tests/ActivitySourceTests.cs`

- [ ] **Step 1: Write the test**

```csharp
using System.Diagnostics;
using Llmed;
using Microsoft.Extensions.DependencyInjection;

namespace Llmed.Tests;

public class ActivitySourceTests
{
    public sealed record Traced(string Body) : IRequest<string>;

    public sealed class TracedHandler : IRequestHandler<Traced, string>
    {
        public Task<string> Handle(Traced request, CancellationToken ct) =>
            Task.FromResult(request.Body);
    }

    [Fact]
    public async Task Send_starts_one_activity_named_Mediator_Send_with_request_type_tag()
    {
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == MediatorDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => captured.Add(activity),
        };
        ActivitySource.AddActivityListener(listener);

        var services = new ServiceCollection();
        services.AddMediator(typeof(ActivitySourceTests).Assembly);
        await using var sp = services.BuildServiceProvider();

        var mediator = sp.GetRequiredService<IMediator>();
        await mediator.Send(new Traced("hello"));

        var activity = Assert.Single(captured);
        Assert.Equal("Mediator.Send", activity.OperationName);
        Assert.Equal(
            typeof(Traced).FullName,
            activity.GetTagItem("request.type") as string);
    }
}
```

- [ ] **Step 2: Run the test, expect pass**

Run:

```powershell
dotnet test --filter "FullyQualifiedName~ActivitySourceTests"
```

Expected: `Passed!  - Failed: 0, Passed: 1`.

- [ ] **Step 3: Commit**

```powershell
git add tests/Llmed.Tests/ActivitySourceTests.cs
git commit -m "test: assert Mediator.Send emits an ActivitySource activity"
```

---

## Task 12: Full test run and README

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Run the entire suite**

Run:

```powershell
dotnet test
```

Expected: `Passed!  - Failed: 0, Passed: 10` (MediatorTests: 3, PipelineOrderingTests: 1, ConcurrencyLimitBehaviorTests: 2, MultipleMediatorsAreIsolatedTests: 2, ConcurrentSendStressTests: 1, ActivitySourceTests: 1).

If any test fails, do NOT amend earlier commits — debug in place, fix forward, and add a follow-up commit.

- [ ] **Step 2: Replace `README.md` with usage docs**

Overwrite `README.md` with:

````markdown
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
````

- [ ] **Step 3: Final build + test, then commit**

```powershell
dotnet build
dotnet test
```

Expected: build clean, all 10 tests pass.

```powershell
git add README.md
git commit -m "docs: rewrite README with usage examples"
```

---

## Acceptance criteria

The library is done when:

- `dotnet build` succeeds with zero warnings on `net8.0`.
- `dotnet test` reports 10/10 passing.
- The library footprint is ~150–250 LOC under `src/Llmed/` (excluding the built-in `Behaviors/` folder).
- Public API matches the spec exactly: `IRequest<TResponse>`, `IRequestHandler<,>`, `RequestHandlerDelegate<TResponse>`, `IPipelineBehavior<,>`, `IMediator`, `MediatorBuilder`, `AddMediator`, `AddKeyedMediator`, `AddConcurrencyLimit`, `ConcurrencyLimitBehavior<,>`, `ConcurrencyLimitOptions`, `ConcurrencyLimitGate`.
- `ActivitySource` is named `"Llmed.Mediator"` (string literal documented in README; the constant itself is internal).
- All requirements 1–10 in the spec have a corresponding implementation and test.
