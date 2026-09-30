// Maps integration event classes to their stable wire names and back. Consumers only
// deserialize types that were registered from a Contracts assembly, so a message cannot
// make the consumer instantiate an arbitrary .NET type (unsafe deserialization).
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Commerce.SharedKernel.Messaging;

namespace Commerce.BuildingBlocks.Messaging;

public sealed record IntegrationEventDescriptor(string Name, int Version, string RoutingKey, Type ClrType);

public static class IntegrationEventTypes
{
    private static readonly ConcurrentDictionary<Type, IntegrationEventDescriptor> ByType = new();
    private static readonly ConcurrentDictionary<string, IntegrationEventDescriptor> ByRoutingKey = new();

    public static IntegrationEventDescriptor Describe(Type type) => ByType.GetOrAdd(type, static clrType =>
    {
        var attribute = clrType.GetCustomAttribute<IntegrationEventAttribute>()
            ?? throw new InvalidOperationException($"{clrType.Name} has no [IntegrationEvent] attribute.");
        var descriptor = new IntegrationEventDescriptor(attribute.Name, attribute.Version, attribute.RoutingKey, clrType);
        ByRoutingKey[descriptor.RoutingKey] = descriptor;
        return descriptor;
    });

    public static IntegrationEventDescriptor Describe<TEvent>() where TEvent : IntegrationEvent => Describe(typeof(TEvent));

    public static void RegisterAssembly(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes().Where(t => t.GetCustomAttribute<IntegrationEventAttribute>() is not null))
        {
            Describe(type);
        }
    }

    public static IntegrationEventDescriptor? Find(string routingKey) =>
        ByRoutingKey.TryGetValue(routingKey, out var descriptor) ? descriptor : null;
}

public static class MessagingJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        // Unknown properties are ignored for forward compatibility, but polymorphic type
        // metadata is never honoured: the target type always comes from the registry above.
        AllowOutOfOrderMetadataProperties = false,
    };
}
