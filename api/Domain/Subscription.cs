namespace PaymentService.Domain;

public class Subscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid ServiceId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public SubscriptionStatus Status { get; set; }
    public required string PriceId { get; set; }

    public record SubscriptionRequested(Guid UserId, Guid ServiceId, string StripePriceId);

    public record SubscriptionStarted(string StripeSubscriptionId);

    public record SubscriptionStartFailed(string Reason);

    public record PaymentSucceeded(string StripeEventId, string StripeInvoiceId, long AmountPaid, string Currency);

    public record PaymentFailed(string StripeEventId, string StripeInvoiceId, string Reason);

    public record SubscriptionCanceled(string StripeEventId, string Reason);
}

public enum SubscriptionStatus
{
    Pending,
    Active,
    PastDue,
    Canceled,
    Failed
}
