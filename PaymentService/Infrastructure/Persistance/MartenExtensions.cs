using Marten;
using JasperFx.Events.Projections;
using PaymentService.Domain;

namespace PaymentService.Infrastructure.Persistance;

public static class MartenExtensions
{
    public static IServiceCollection AddPaymentEventStore(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

        services.AddMarten(options =>
        {
            options.Connection(connectionString);

            options.Events.AddEventType<Subscription.SubscriptionRequested>();
            options.Events.AddEventType<Subscription.SubscriptionStarted>();
            options.Events.AddEventType<Subscription.SubscriptionStartFailed>();
            options.Events.AddEventType<Subscription.SubscriptionPaymentFailed>();
            options.Events.AddEventType<Subscription.SubscriptionCanceled>();

            options.Projections.Snapshot<Subscription>(SnapshotLifecycle.Inline);

            options.Schema.For<Subscription>().Index(s => s.UserId);
            options.Schema.For<Subscription>().Index(s => s.ServiceId);
            options.Schema.For<Subscription>().Index(s => s.StripeSubscriptionId!, x => x.IsUnique = true);
        }).UseLightweightSessions();

        return services;
    }
}
