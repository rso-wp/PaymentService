using System.Text.Json;
using Marten;
using PaymentService.Domain;
using PaymentService.Infrastructure.Messaging;
using PaymentService.Infrastructure.StripeIntegration;
using Stripe;

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

        // true: a payment failed; false: cancelled in Stripe; null: nothing that ends the subscription.
        bool? paymentFailed = message.Type switch
        {
            EventTypes.InvoicePaymentFailed => true,
            EventTypes.CustomerSubscriptionDeleted => false,
            EventTypes.CustomerSubscriptionUpdated => (stripeEvent.Data.Object as Stripe.Subscription)?.Status switch
            {
                StripeSubscriptionStatus.Canceled => false,
                { } status when StripeSubscriptionStatus.IsUnpaid(status) => true,
                _ => null
            },
            _ => null
        };
        if (paymentFailed is null)
        {
            logger.LogDebug("Stripe event {EventId} ({Type}) does not end a subscription", message.EventId, message.Type);
            return null;
        }

        var stripeSubscriptionId = SubscriptionIdOf(stripeEvent, message.Json);
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

        var ended = await SubscriptionEnding.EndAsync(subscription, paymentFailed.Value, message.EventId, session, stripe, ct);
        logger.LogWarning("Subscription {SubscriptionId} ({StripeSubscriptionId}) ended: {Reason}",
            subscription.Id, stripeSubscriptionId, ended.Reason);
        return ended;
    }

    /// <summary>
    /// The Stripe subscription the event is about. An invoice names it in parent.subscription_details (API version
    /// 2025-03-31 and later) or, when the webhook endpoint uses an older API version, in "subscription", which the typed
    /// object no longer has; the raw JSON is read for that case.
    /// </summary>
    private static string? SubscriptionIdOf(Event stripeEvent, string json)
    {
        switch (stripeEvent.Data.Object)
        {
            case Stripe.Subscription subscription:
                return subscription.Id;
            case Invoice { Parent.SubscriptionDetails.SubscriptionId: { Length: > 0 } id }:
                return id;
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("object", out var obj)
            || !obj.TryGetProperty("subscription", out var subscriptionId))
            return null;

        return subscriptionId.ValueKind switch
        {
            JsonValueKind.String => subscriptionId.GetString(),
            JsonValueKind.Object when subscriptionId.TryGetProperty("id", out var id) => id.GetString(),
            _ => null
        };
    }
}
