namespace PaymentService.Domain;

/// <summary>
/// Event-sourced (Marten). Stream id = <see cref="Id"/>; the document is the inline snapshot, folded from the events below.
/// </summary>
public class Subscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string ServiceId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public SubscriptionStatus Status { get; set; }
    public required string PriceId { get; set; }
    public string? FailureReason { get; set; }

    public static Subscription Create(SubscriptionRequested e) => new()
    {
        UserId = e.UserId,
        ServiceId = e.ServiceId,
        PriceId = e.StripePriceId,
        Status = SubscriptionStatus.Pending
    };

    public void Apply(SubscriptionStarted e)
    {
        if (Status == SubscriptionStatus.Canceled) return;
        StripeSubscriptionId = e.StripeSubscriptionId;
        Status = SubscriptionStatus.Active;
    }

    public void Apply(SubscriptionStartFailed e)
    {
        if (Status == SubscriptionStatus.Canceled) return;
        FailureReason = e.Reason;
        Status = SubscriptionStatus.FailedToStart;
    }

    public void Apply(SubscriptionPaymentFailed e)
    {
        if (Status == SubscriptionStatus.Canceled) return;
        Status = SubscriptionStatus.Canceled;
    }

    public void Apply(SubscriptionCanceled e)
    {
        if (Status == SubscriptionStatus.Canceled) return;
        Status = SubscriptionStatus.Canceled;
    }

    public record SubscriptionRequested(Guid UserId, string ServiceId, string StripePriceId);
    public record SubscriptionStarted(string StripeSubscriptionId);
    public record SubscriptionStartFailed(string Reason);
    /// <summary>StripeEventId is set when the cancel came from a Stripe webhook (idempotency), null for a user cancel.</summary>
    public record SubscriptionCanceled(string? StripeEventId);
    public record SubscriptionPaymentFailed(string StripeEventId);
}

public enum SubscriptionStatus
{
    Pending,
    FailedToStart,
    Active,
    Canceled
}
