using Llmed;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Llmed.Tests;

public class PipelineOrderingTests
{
    // DI-injected shared list scoped to one test instance — avoids the static-state
    // hazard where a parallel test could append between Clear() and the assertion.
    public sealed class Trace
    {
        public List<string> Entries { get; } = new();
    }

    public sealed record Echo(string Value) : IRequest<string>;

    public sealed class EchoHandler : IRequestHandler<Echo, string>
    {
        private readonly Trace _trace;

        public EchoHandler(Trace trace) => _trace = trace;

        public Task<string> Handle(Echo request, CancellationToken ct)
        {
            lock (_trace.Entries) _trace.Entries.Add("handler");
            return Task.FromResult(request.Value);
        }
    }

    public class TracingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        private readonly string _name;
        private readonly Trace _trace;

        public TracingBehavior(string name, Trace trace)
        {
            _name = name;
            _trace = trace;
        }

        public async Task<TResponse> Handle(
            TRequest request,
            RequestHandlerDelegate<TResponse> next,
            CancellationToken ct)
        {
            lock (_trace.Entries) _trace.Entries.Add($"{_name}-pre");
            var response = await next();
            lock (_trace.Entries) _trace.Entries.Add($"{_name}-post");
            return response;
        }
    }

    public sealed class BehaviorB1<TRequest, TResponse> : TracingBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse> { public BehaviorB1(Trace trace) : base("B1", trace) { } }

    public sealed class BehaviorB2<TRequest, TResponse> : TracingBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse> { public BehaviorB2(Trace trace) : base("B2", trace) { } }

    public sealed class BehaviorB3<TRequest, TResponse> : TracingBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse> { public BehaviorB3(Trace trace) : base("B3", trace) { } }

    [Fact]
    public async Task Behaviors_execute_in_registration_order_around_handler()
    {
        var trace = new Trace();
        var services = new ServiceCollection();
        services.AddSingleton(trace);
        services.AddMediator(typeof(PipelineOrderingTests).Assembly)
                .AddBehavior(typeof(BehaviorB1<,>))
                .AddBehavior(typeof(BehaviorB2<,>))
                .AddBehavior(typeof(BehaviorB3<,>));
        await using var sp = services.BuildServiceProvider();

        var mediator = sp.GetRequiredService<IMediator>();
        await mediator.Send(new Echo("x"));

        Assert.Equal(
            new[] { "B1-pre", "B2-pre", "B3-pre", "handler", "B3-post", "B2-post", "B1-post" },
            trace.Entries);
    }
}
