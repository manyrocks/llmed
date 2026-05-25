using Llmed;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

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

    public class TracingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
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
