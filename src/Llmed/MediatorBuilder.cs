using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Llmed;

public sealed class MediatorBuilder
{
    private readonly IServiceCollection _services;
    private readonly object? _serviceKey;
    private readonly List<Type> _behaviorTypes = new();

    internal MediatorBuilder(IServiceCollection services, object? serviceKey)
    {
        _services = services;
        _serviceKey = serviceKey;
    }

    internal IReadOnlyList<Type> BehaviorTypes => _behaviorTypes;

    public MediatorBuilder AddBehavior(Type openGenericBehaviorType)
    {
        ArgumentNullException.ThrowIfNull(openGenericBehaviorType);

        if (!openGenericBehaviorType.IsGenericTypeDefinition
            || openGenericBehaviorType.GetGenericArguments().Length != 2)
        {
            throw new ArgumentException(
                $"Behavior type must be an open generic with two type parameters: " +
                $"{openGenericBehaviorType.FullName}",
                nameof(openGenericBehaviorType));
        }

        var implementsBehaviorInterface = openGenericBehaviorType.GetInterfaces()
            .Any(i => i.IsGenericType
                      && i.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>));

        if (!implementsBehaviorInterface)
        {
            throw new ArgumentException(
                $"Behavior type must implement IPipelineBehavior<,>: " +
                $"{openGenericBehaviorType.FullName}",
                nameof(openGenericBehaviorType));
        }

        _behaviorTypes.Add(openGenericBehaviorType);

        if (_serviceKey is null)
        {
            _services.AddTransient(openGenericBehaviorType);
        }
        else
        {
            _services.AddKeyedTransient(openGenericBehaviorType, _serviceKey);
        }

        return this;
    }
}
