using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PaymentService.Features.Customers;
using PaymentService.Infrastructure.Auth;
using PaymentService.Infrastructure.StripeIntegration;

namespace PaymentService.Features.PaymentMethods;

/// <summary>How the frontend should collect cards: Stripe Elements with this key, or the dev-only fake card form.</summary>
public record PaymentConfigResponse(string Mode, string? PublishableKey);

public record AddFakeCardRequest(string Number, string Expiry);

public static partial class PaymentMethodsEndpoints
{
    [GeneratedRegex(@"^(0[1-9]|1[0-2])\s*/\s*(\d{2})$")]
    private static partial Regex Expiry();

    public static IEndpointRouteBuilder MapPaymentMethods(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/payment-methods")
            .RequireAuthorization()
            .RequireCors(AuthExtensions.FrontendCorsPolicy);

        // GET /payment-methods/config: "stripe" (+ publishable key) or "fake" (local development without Stripe).
        group.MapGet("/config", (IStripeGateway stripe, IOptions<StripeOptions> options) =>
            stripe is FakeStripeGateway
                ? new PaymentConfigResponse("fake", null)
                : new PaymentConfigResponse("stripe", options.Value.PublishableKey));

        // GET /payment-methods: the user's saved cards, newest first.
        group.MapGet("", async (
                ClaimsPrincipal user, StripeCustomerResolver customers, IStripeGateway stripe, CancellationToken ct) =>
            {
                var customerId = await customers.GetOrCreateStripeCustomerIdAsync(user.GetUserId(), user.GetEmail(), ct);
                return Results.Ok(await stripe.ListCardsAsync(customerId, ct));
            });

        // POST /payment-methods/fake: Development + Stripe:UseFake only. Stands in for Stripe Elements + setup intent.
        // Only brand, last 4 digits and expiry are kept.
        group.MapPost("/fake", async (
                AddFakeCardRequest request,
                ClaimsPrincipal user,
                StripeCustomerResolver customers,
                IStripeGateway stripe,
                CancellationToken ct) =>
            {
                if (stripe is not FakeStripeGateway fake) return Results.NotFound();

                var digits = new string((request.Number ?? "").Where(char.IsDigit).ToArray());
                var expiry = Expiry().Match(request.Expiry ?? "");
                if (digits.Length is < 13 or > 19 || !expiry.Success)
                    return Results.Problem(detail: "Check the card number and expiry date.", statusCode: StatusCodes.Status400BadRequest);

                var customerId = await customers.GetOrCreateStripeCustomerIdAsync(user.GetUserId(), user.GetEmail(), ct);
                var card = await fake.AddCardAsync(
                    customerId, digits, int.Parse(expiry.Groups[1].Value), 2000 + int.Parse(expiry.Groups[2].Value), ct);
                return Results.Ok(card);
            });

        return app;
    }
}
