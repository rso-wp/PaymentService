using Marten;
using PaymentService.Domain;
using PaymentService.Features.StripeWebhooks;
using PaymentService.Infrastructure.Messaging;
using PaymentService.Infrastructure.StripeIntegration;

namespace PaymentService.Features.Reconciliation;

public static class CheckSubscriptionHandler
{
    /// <summary>
    /// Ends a subscription that is Active here but over in Stripe (cancelled, or a renewal not paid): a webhook for it was
    /// missed. Same transaction and outbox as <see cref="StripeWebhookHandler"/>.
    /// </summary>
    public static async Task<SubscriptionEnded?> Handle(
        CheckSubscription message,
        IDocumentSession session,
        IStripeGateway stripe,
        ILogger<CheckSubscription> logger,
        CancellationToken ct)
    {
        var subscription = await session.LoadAsync<Subscription>(message.SubscriptionId, ct);
        if (subscription is not { Status: SubscriptionStatus.Active, StripeSubscriptionId: { } stripeSubscriptionId }) return null;

        var status = await stripe.GetSubscriptionStatusAsync(stripeSubscriptionId, ct);
        bool paymentFailed;
        if (status == StripeSubscriptionStatus.Canceled) paymentFailed = false;
        else if (StripeSubscriptionStatus.IsUnpaid(status)) paymentFailed = true;
        else return null;

        logger.LogWarning("Subscription {SubscriptionId} ({StripeSubscriptionId}) is {Status} in Stripe but was Active here " +
            "(a webhook was missed); ending it", subscription.Id, stripeSubscriptionId, status);
        return await SubscriptionEnding.EndAsync(subscription, paymentFailed, stripeEventId: null, session, stripe, ct);
    }
}
