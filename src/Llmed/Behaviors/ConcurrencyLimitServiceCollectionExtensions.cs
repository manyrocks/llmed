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
