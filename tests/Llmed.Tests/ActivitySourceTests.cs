using System.Diagnostics;
using Llmed;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

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
        var captured = new System.Collections.Concurrent.ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = src => src.Name == MediatorDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem("request.type") as string == typeof(Traced).FullName)
                {
                    captured.Add(activity);
                }
            },
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
