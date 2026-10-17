using Microsoft.Extensions.Options;
using PaymentService.Infrastructure.StripeIntegration;
using Stripe;
using Wolverine;

namespace PaymentService.Features.StripeWebhooks;

public static class StripeWebhookEndpoint
{
    /// <summary>Stripe events this service reacts to. Everything else is acknowledged and ignored.</summary>
    public static readonly HashSet<string> HandledTypes =
    [
        EventTypes.InvoicePaymentFailed,
        EventTypes.CustomerSubscriptionDeleted
    ];

    /// <summary>
    /// POST /stripe/webhooks. Called by Stripe (no user auth; the Stripe-Signature header is verified against
    /// Stripe:WebhookSecret). Only verifies and enqueues: the event is handed to a durable Wolverine queue and 200 is
    /// returned right away, so Stripe does not time out; <see cref="StripeWebhookHandler"/> does the work.
    /// </summary>
    public static IEndpointRouteBuilder MapStripeWebhooks(this IEndpointRouteBuilder app)
    {
        app.MapPost("/stripe/webhooks", async (
                HttpRequest request,
                IOptions<StripeOptions> options,
                IMessageBus bus,
                ILogger<StripeWebhookReceived> logger) =>
            {
                using var reader = new StreamReader(request.Body);
                var json = await reader.ReadToEndAsync();

                Event stripeEvent;
                try
                {
                    // The account's API version may differ from the library's; we only read stable fields.
                    stripeEvent = EventUtility.ConstructEvent(
                        json, request.Headers["Stripe-Signature"], options.Value.WebhookSecret,
                        throwOnApiVersionMismatch: false);
                }
                catch (StripeException ex)
                {
                    logger.LogWarning("Rejected Stripe webhook: {Reason}", ex.Message);
                    return Results.BadRequest();
                }

                if (!HandledTypes.Contains(stripeEvent.Type))
                {
                    logger.LogDebug("Ignoring Stripe event {Type} {EventId}", stripeEvent.Type, stripeEvent.Id);
                    return Results.Ok();
                }

                await bus.PublishAsync(new StripeWebhookReceived(stripeEvent.Id, stripeEvent.Type, json));
                return Results.Ok();
            })
            .AllowAnonymous();

        return app;
    }
}
