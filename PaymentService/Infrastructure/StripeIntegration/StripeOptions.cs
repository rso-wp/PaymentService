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

    /// <summary>
    /// The webhook secret is a placeholder (terraform's default, the development value) rather than an endpoint's real
    /// signing secret: every webhook then fails the signature check and failed payments are only caught by the reconciliation.
    /// </summary>
    public bool WebhookSecretIsPlaceholder =>
        !WebhookSecret.StartsWith("whsec_") || WebhookSecret is "whsec_not_set_yet" or "whsec_REPLACE_ME";
}
