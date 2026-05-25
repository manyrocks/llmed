namespace Llmed.Tests;

public class OrchestratorMediatorTests
{
    [Fact]
    public async Task Send_ExecutesPipelineInRegistrationOrder()
    {
        var trace = new List<string>();
        var mediator = new OrchestratorMediator(
            singleResolver: type =>
            {
                if (type == typeof(IRequestHandler<Ping, string>))
                {
                    return new PingHandler(trace);
                }

                return null;
            },
            multiResolver: type =>
            {
                if (type == typeof(IPipelineBehavior<Ping, string>))
                {
                    return
                    [
                        new TraceBehavior("behavior-1", trace),
                        new TraceBehavior("behavior-2", trace)
                    ];
                }

                return [];
            });

        var result = await mediator.Send(new Ping("hello"));

        Assert.Equal("pong:hello", result);
        Assert.Equal(
            ["behavior-1:before", "behavior-2:before", "handler", "behavior-2:after", "behavior-1:after"],
            trace);
    }

    [Fact]
    public async Task Publish_DispatchesToAllHandlers()
    {
        var trace = new List<string>();
        var mediator = new OrchestratorMediator(
            singleResolver: _ => null,
            multiResolver: type =>
            {
                if (type == typeof(IEventHandler<UserCreated>))
                {
                    return
                    [
                        new UserCreatedAuditHandler(trace),
                        new UserCreatedAnalyticsHandler(trace)
                    ];
                }

                return [];
            });

        await mediator.Publish((IEvent)new UserCreated("u-1"));

        Assert.Equal(["audit:u-1", "analytics:u-1"], trace);
    }

    [Fact]
    public async Task Send_ThrowsWhenNoHandlerRegistered()
    {
        var mediator = new OrchestratorMediator(
            singleResolver: _ => null,
            multiResolver: _ => []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Send(new Ping("missing")));
    }

    private sealed record Ping(string Value) : IRequest<string>;

    private sealed class PingHandler(List<string> trace) : IRequestHandler<Ping, string>
    {
        public Task<string> Handle(Ping request, CancellationToken cancellationToken)
        {
            trace.Add("handler");
            return Task.FromResult($"pong:{request.Value}");
        }
    }

    private sealed class TraceBehavior(string name, List<string> trace) : IPipelineBehavior<Ping, string>
    {
        public async Task<string> Handle(Ping request, RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
        {
            trace.Add($"{name}:before");
            var result = await next();
            trace.Add($"{name}:after");
            return result;
        }
    }

    private sealed record UserCreated(string UserId) : IEvent;

    private sealed class UserCreatedAuditHandler(List<string> trace) : IEventHandler<UserCreated>
    {
        public Task Handle(UserCreated @event, CancellationToken cancellationToken)
        {
            trace.Add($"audit:{@event.UserId}");
            return Task.CompletedTask;
        }
    }

    private sealed class UserCreatedAnalyticsHandler(List<string> trace) : IEventHandler<UserCreated>
    {
        public Task Handle(UserCreated @event, CancellationToken cancellationToken)
        {
            trace.Add($"analytics:{@event.UserId}");
            return Task.CompletedTask;
        }
    }
}
