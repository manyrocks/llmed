using Llmed;
using Llmed.Behaviors;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

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
}
