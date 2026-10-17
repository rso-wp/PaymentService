using System.Security.Claims;
using PaymentService.Infrastructure.Auth;
using Stripe;

namespace PaymentService.Features.AddPaymentMethod;

public record SetupIntentResponse(string ClientSecret);

public static class AddPaymentMethodEndpoint
{
    /// <summary>
    /// POST /payment-methods/setup-intent. The frontend passes the returned client secret to
    /// stripe.confirmSetup() (Stripe Elements), which saves the card to the user's Stripe customer.
    /// </summary>
    public static IEndpointRouteBuilder MapAddPaymentMethod(this IEndpointRouteBuilder app)
    {
        app.MapPost("/payment-methods/setup-intent", async (
                ClaimsPrincipal user,
                StripeCustomerResolver customers,
                SetupIntentService setupIntents,
                CancellationToken ct) =>
            {
                var stripeCustomerId = await customers.GetOrCreateStripeCustomerIdAsync(
                    user.GetUserId(), user.GetEmail(), ct);

                var intent = await setupIntents.CreateAsync(
                    new SetupIntentCreateOptions
                    {
                        Customer = stripeCustomerId,
                        Usage = "off_session",
                        AutomaticPaymentMethods = new SetupIntentAutomaticPaymentMethodsOptions
                        {
                            Enabled = true,
                            AllowRedirects = "never"
                        }
                    },
                    cancellationToken: ct);

                return Results.Ok(new SetupIntentResponse(intent.ClientSecret));
            })
            .RequireAuthorization()
            .RequireCors(AuthExtensions.FrontendCorsPolicy);

        return app;
    }
}
