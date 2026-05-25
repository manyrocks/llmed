using Llmed;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

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
}
