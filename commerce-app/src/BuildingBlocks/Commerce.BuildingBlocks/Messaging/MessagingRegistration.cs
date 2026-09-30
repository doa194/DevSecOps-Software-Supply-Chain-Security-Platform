// Dependency-injection setup for messaging: one connection per process, one signer for
// this publisher, one verifier with the trusted publishers, plus outbox publishers and
// consumers registered by the modules and workers that need them.
using System.Reflection;
using Commerce.BuildingBlocks.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Commerce.BuildingBlocks.Messaging;

public static class MessagingRegistration
{
    public static IServiceCollection AddCommerceMessaging(this IServiceCollection services, IConfiguration configuration, params Assembly[] contractAssemblies)
    {
        var options = configuration.GetSection(MessagingOptions.SectionName).Get<MessagingOptions>() ?? new MessagingOptions();
        foreach (var assembly in contractAssemblies.Append(typeof(SharedKernel.Auditing.AuditRecordedV1).Assembly))
        {
            IntegrationEventTypes.RegisterAssembly(assembly);
        }

        services.TryAddSingleton(options);
        services.TryAddSingleton<RabbitMqConnectionProvider>();
        services.TryAddSingleton<ICorrelationContext, CorrelationContext>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new EnvelopeVerifier(sp.GetRequiredService<MessagingOptions>().TrustedPublishers));
        services.TryAddSingleton(sp =>
        {
            var messaging = sp.GetRequiredService<MessagingOptions>();
            return new EnvelopeSigner(messaging.Signing, messaging.Source);
        });
        return services;
    }

    public static IServiceCollection AddOutboxPublisher<TContext>(this IServiceCollection services)
        where TContext : ModuleDbContext
    {
        services.AddHostedService<OutboxPublisher<TContext>>();
        return services;
    }

    public static IServiceCollection AddIntegrationEventConsumer(this IServiceCollection services, string queue, Action<ConsumerDefinition> configure)
    {
        var definition = new ConsumerDefinition(queue);
        configure(definition);
        foreach (var handler in definition.Handlers)
        {
            services.TryAddScoped(handler.HandlerType);
        }

        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => ActivatorUtilities.CreateInstance<IntegrationEventConsumer>(sp, definition));
        return services;
    }
}
