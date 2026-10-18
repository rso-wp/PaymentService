using System.ComponentModel.DataAnnotations;

namespace PaymentService.Infrastructure.StripeIntegration;

public class StripeOptions
{
    public const string SectionName = "Stripe";

    [Required]
    public string SecretKey { get; set; } = "";

    [Required]
    public string WebhookSecret { get; set; } = "";

    /// <summary>Publishable key handed to the frontend for Stripe Elements (pk_test_... / pk_live_...).</summary>
    public string PublishableKey { get; set; } = "";

    /// <summary>Development only: use <see cref="FakeStripeGateway"/> instead of calling Stripe.</summary>
    public bool UseFake { get; set; }
}
