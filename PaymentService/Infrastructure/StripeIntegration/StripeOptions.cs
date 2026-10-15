using System.ComponentModel.DataAnnotations;

namespace PaymentService.Infrastructure.StripeIntegration;

public class StripeOptions
{
    public const string SectionName = "Stripe";

    [Required]
    public string SecretKey { get; set; } = "";

    [Required]
    public string WebhookSecret { get; set; } = "";
}
