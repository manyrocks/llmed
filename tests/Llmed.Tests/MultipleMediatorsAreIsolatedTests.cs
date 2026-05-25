using Llmed;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Llmed.Tests;

public class MultipleMediatorsAreIsolatedTests
{
    public sealed record Probe(string From) : IRequest<List<string>>;

    public sealed class ProbeHandler : IRequestHandler<Probe, List<string>>
    {
        public Task<List<string>> Handle(Probe request, CancellationToken ct) =>
            Task.FromResult(new List<string> { $"handler:{request.From}" });
    }

    public class TaggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
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
