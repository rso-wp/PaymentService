namespace PaymentService.Features.StripeWebhooks;

/// <summary>Wolverine message: a verified Stripe webhook event, stored durably until handled.</summary>
public record StripeWebhookReceived(string EventId, string Type, string Json);

/// <summary>Marten document: a Stripe event id that was already handled. Stripe redelivers webhooks.</summary>
public class ProcessedStripeEvent
{
    public required string Id { get; set; }
    public required string Type { get; set; }
    public DateTime ProcessedAt { get; set; }
}
