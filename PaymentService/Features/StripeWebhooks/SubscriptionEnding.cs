using Marten;
using PaymentService.Infrastructure.Messaging;
using PaymentService.Infrastructure.StripeIntegration;
using static PaymentService.Domain.Subscription;

namespace PaymentService.Features.StripeWebhooks;

/// <summary>
/// Ends an Active subscription that Stripe reports as over: a webhook (<see cref="StripeWebhookHandler"/>) or the
/// reconciliation (<see cref="Reconciliation.CheckSubscriptionHandler"/>). The caller returns the <see cref="SubscriptionEnded"/>, so it
/// commits with the appended event (outbox) and ProvisioningService then shuts the service down.
/// </summary>
public static class SubscriptionEnding
{
    /// <param name="paymentFailed">
    /// true: a payment failed. The subscription is also cancelled in Stripe, so Stripe stops retrying the charge.
    /// false: Stripe has already cancelled it.
    /// </param>
    public static async Task<SubscriptionEnded> EndAsync(
        Domain.Subscription subscription,
        bool paymentFailed,
        string? stripeEventId,
        IDocumentSession session,
        IStripeGateway stripe,
        CancellationToken ct)
    {
        var stripeSubscriptionId = subscription.StripeSubscriptionId
            ?? throw new InvalidOperationException($"Subscription {subscription.Id} has no Stripe subscription.");

        if (paymentFailed)
        {
            await stripe.CancelSubscriptionAsync(stripeSubscriptionId, ct);
            session.Events.Append(subscription.Id, new SubscriptionPaymentFailed(stripeEventId));
        }
        else
        {
            session.Events.Append(subscription.Id, new SubscriptionCanceled(stripeEventId));
        }

        var reason = paymentFailed ? SubscriptionEnded.PaymentFailed : SubscriptionEnded.CanceledInStripe;
        return new SubscriptionEnded(subscription.Id, subscription.UserId, stripeSubscriptionId, reason);
    }
}
