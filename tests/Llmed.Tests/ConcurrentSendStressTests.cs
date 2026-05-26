using Llmed;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

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
        // Use Assert.IsType so a future refactor that decorates IMediator fails with
        // a clear assertion rather than a confusing InvalidCastException.
        var concrete = Assert.IsType<Mediator>(mediator);

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
