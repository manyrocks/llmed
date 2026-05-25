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
