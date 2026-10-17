namespace PaymentService.Infrastructure.Messaging;

// Wire contract shared with ProvisioningService (copy in ProvisioningService/Api/Infrastructure/Messaging/PaymentContracts.cs).
// Keep both copies in sync. Payloads are JSON (camelCase) on NATS JetStream stream PAYMENTS.

public static class PaymentSubjects
{
    public const string Stream = "PAYMENTS";
    public const string All = "payments.>";

    public const string SubscriptionEnded = "payments.events.subscription-ended";
}

/// <summary>
/// A subscription ended without the user asking for it (payment failed, or cancelled in Stripe). The service behind it must
/// be shut down. Not sent for StopSubscription, because then the caller already knows.
/// </summary>
public record SubscriptionEnded(Guid SubscriptionId, Guid UserId, string StripeSubscriptionId, string Reason)
{
    public const string PaymentFailed = "payment_failed";
    public const string CanceledInStripe = "canceled_in_stripe";
}
