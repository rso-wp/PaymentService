using Marten;
using PaymentService.Domain;
using PaymentService.Infrastructure.Messaging;
using PaymentService.Infrastructure.StripeIntegration;
using Stripe;
using static PaymentService.Domain.Subscription;

namespace PaymentService.Features.StripeWebhooks;

public static class StripeWebhookHandler
{
    /// <summary>
    /// Runs in one Marten transaction (Wolverine transactional middleware): the processed-event marker, the appended
    /// subscription event and the cascaded <see cref="SubscriptionEnded"/> message commit together (outbox).
    ///
    /// Policy: the first failed payment ends the subscription. It is cancelled in Stripe (so Stripe stops retrying) and the
    /// service is shut down. Only Active subscriptions are affected, so our own cancels (StopSubscription, which make Stripe
    /// send customer.subscription.deleted) and failed first payments (never Active) are ignored.
    /// </summary>
    public static async Task<SubscriptionEnded?> Handle(
        StripeWebhookReceived message,
        IDocumentSession session,
        IStripeGateway stripe,
        ILogger<StripeWebhookReceived> logger,
        CancellationToken ct)
    {
        if (await session.LoadAsync<ProcessedStripeEvent>(message.EventId, ct) is not null)
        {
            logger.LogInformation("Stripe event {EventId} already handled", message.EventId);
            return null;
        }
        // Insert, not Store: a concurrent redelivery fails on commit and is retried, then sees the marker above.
        session.Insert(new ProcessedStripeEvent { Id = message.EventId, Type = message.Type, ProcessedAt = DateTime.UtcNow });

        var stripeEvent = EventUtility.ParseEvent(message.Json, throwOnApiVersionMismatch: false);

        var stripeSubscriptionId = stripeEvent.Data.Object switch
        {
            Invoice invoice => invoice.Parent?.SubscriptionDetails?.SubscriptionId,
            Stripe.Subscription stripeSubscription => stripeSubscription.Id,
            _ => null
        };
        if (string.IsNullOrEmpty(stripeSubscriptionId))
        {
            logger.LogInformation("Stripe event {EventId} ({Type}) is not about a subscription", message.EventId, message.Type);
            return null;
        }

        var subscription = await session.Query<Domain.Subscription>()
            .FirstOrDefaultAsync(s => s.StripeSubscriptionId == stripeSubscriptionId, ct);
        if (subscription is null || subscription.Status != SubscriptionStatus.Active)
        {
            logger.LogInformation("Stripe event {EventId} ({Type}): subscription {StripeSubscriptionId} is {Status}, nothing to do",
                message.EventId, message.Type, stripeSubscriptionId, subscription?.Status.ToString() ?? "unknown");
            return null;
        }

        string reason;
        if (message.Type == EventTypes.InvoicePaymentFailed)
        {
            await stripe.CancelSubscriptionAsync(stripeSubscriptionId, ct);
            session.Events.Append(subscription.Id, new SubscriptionPaymentFailed(message.EventId));
            reason = SubscriptionEnded.PaymentFailed;
        }
        else
        {
            session.Events.Append(subscription.Id, new SubscriptionCanceled(message.EventId));
            reason = SubscriptionEnded.CanceledInStripe;
        }

        logger.LogWarning("Subscription {SubscriptionId} ({StripeSubscriptionId}) ended: {Reason}",
            subscription.Id, stripeSubscriptionId, reason);

        return new SubscriptionEnded(subscription.Id, subscription.UserId, stripeSubscriptionId, reason);
    }
}
