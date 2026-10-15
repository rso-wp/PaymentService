namespace PaymentService.Domain;

public class Subscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid ServiceId { get; set; }
    public string? StripeSubscriptionId { get; set; }
    public SubscriptionStatus Status { get; set; }
    public required string PriceId { get; set; }

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

    public record SubscriptionRequested(Guid UserId, Guid ServiceId, string StripePriceId);
    public record SubscriptionStarted(string StripeSubscriptionId);
    public record SubscriptionStartFailed(string Reason);
    public record SubscriptionCanceled(string StripeEventId);
    public record SubscriptionPaymentFailed(string StripeEventId);
}

public enum SubscriptionStatus
{
    Pending,
    FailedToStart,
    Active,
    Canceled
}
