using Marten;
using PaymentService.Features.StripeWebhooks;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.Marten;
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

            options.Schema.For<ProcessedStripeEvent>();
            options.Schema.For<StripeIntegration.FakeCard>().Index(c => c.CustomerId);
            options.Schema.For<StripeIntegration.FakeSubscription>().Index(s => s.CustomerId);
        })
        .UseLightweightSessions()
        // Wolverine's outbox/inbox live in the same Postgres database, so Marten changes and messages commit together.
        .IntegrateWithWolverine();

        return services;
    }

    public static void AddPaymentMessaging(this WolverineOptions options)
    {
        options.Policies.AutoApplyTransactions();
        options.Policies.UseDurableLocalQueues();

        // Handlers call Stripe / NATS. Their messages must never be lost (a webhook is acknowledged to Stripe before it is
        // handled, so Stripe does not send it again): retry for as long as it takes, every 15 minutes in the end, instead of
        // dead-lettering.
        options.OnException<Exception>()
            .RetryWithCooldown(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30))
            .Then.ScheduleRetryIndefinitely(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));
    }
}
