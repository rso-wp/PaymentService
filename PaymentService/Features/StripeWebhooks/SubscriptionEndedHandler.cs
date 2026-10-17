using NATS.Client.JetStream;
using PaymentService.Infrastructure.Messaging;

namespace PaymentService.Features.StripeWebhooks;

public static class SubscriptionEndedHandler
{
    /// <summary>
    /// Forwards the committed <see cref="SubscriptionEnded"/> (durable local queue) to NATS, where ProvisioningService shuts
    /// the service down. Failures are retried by Wolverine.
    /// </summary>
    public static async Task Handle(SubscriptionEnded message, INatsJSContext js, CancellationToken ct)
    {
        await js.EnsurePaymentsStreamAsync(ct);
        await js.PublishJsonAsync(PaymentSubjects.SubscriptionEnded, message, ct);
    }
}
