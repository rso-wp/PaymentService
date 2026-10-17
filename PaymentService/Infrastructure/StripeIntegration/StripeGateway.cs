using System.Security.Cryptography;
using Stripe;

namespace PaymentService.Infrastructure.StripeIntegration;

/// <summary>Stripe declined or rejected a request; <see cref="Exception.Message"/> is safe to show the user.</summary>
public class StripeGatewayException(string message) : Exception(message);

/// <summary>The Stripe calls PaymentService makes, so local development can run without Stripe keys.</summary>
public interface IStripeGateway
{
    Task<string> CreateCustomerAsync(Guid userId, string? email, CancellationToken ct);

    /// <summary>The customer's default card, else the most recently attached one; null if they have none.</summary>
    Task<string?> FindPaymentMethodAsync(string stripeCustomerId, CancellationToken ct);

    /// <summary>Creates an active subscription charged to <paramref name="paymentMethodId"/>, or throws
    /// <see cref="StripeGatewayException"/> if the first payment fails.</summary>
    Task<string> CreateSubscriptionAsync(
        Guid subscriptionId, string stripeCustomerId, string stripePriceId, string paymentMethodId,
        IReadOnlyDictionary<string, string> metadata, CancellationToken ct);

    /// <summary>Cancels the subscription immediately. A subscription Stripe does not know is treated as cancelled.</summary>
    Task CancelSubscriptionAsync(string stripeSubscriptionId, CancellationToken ct);
}

public class StripeGateway(
    Stripe.CustomerService customers,
    PaymentMethodService paymentMethods,
    SubscriptionService subscriptions) : IStripeGateway
{
    public async Task<string> CreateCustomerAsync(Guid userId, string? email, CancellationToken ct)
    {
        // Idempotency key makes a concurrent/repeated call get the same Stripe customer back.
        var customer = await customers.CreateAsync(
            new CustomerCreateOptions
            {
                Email = string.IsNullOrWhiteSpace(email) ? null : email,
                Metadata = new Dictionary<string, string> { ["user_id"] = userId.ToString() }
            },
            new RequestOptions { IdempotencyKey = $"customer-{userId}" },
            ct);

        return customer.Id;
    }

    public async Task<string?> FindPaymentMethodAsync(string stripeCustomerId, CancellationToken ct)
    {
        var customer = await customers.GetAsync(stripeCustomerId, cancellationToken: ct);
        if (!string.IsNullOrEmpty(customer.InvoiceSettings?.DefaultPaymentMethodId))
            return customer.InvoiceSettings.DefaultPaymentMethodId;

        // Cards saved through the setup intent are attached but not made default.
        var cards = await paymentMethods.ListAsync(
            new PaymentMethodListOptions { Customer = stripeCustomerId, Type = "card", Limit = 1 },
            cancellationToken: ct);
        return cards.Data.FirstOrDefault()?.Id;
    }

    public async Task<string> CreateSubscriptionAsync(
        Guid subscriptionId, string stripeCustomerId, string stripePriceId, string paymentMethodId,
        IReadOnlyDictionary<string, string> metadata, CancellationToken ct)
    {
        try
        {
            var subscription = await subscriptions.CreateAsync(
                new SubscriptionCreateOptions
                {
                    Customer = stripeCustomerId,
                    Items = [new SubscriptionItemOptions { Price = stripePriceId }],
                    DefaultPaymentMethod = paymentMethodId,
                    // Fail the call (instead of leaving an "incomplete" subscription) when the first payment fails.
                    PaymentBehavior = "error_if_incomplete",
                    Metadata = metadata.ToDictionary()
                },
                new RequestOptions { IdempotencyKey = $"subscription-{subscriptionId}" },
                ct);

            return subscription.Id;
        }
        catch (StripeException ex) when (ex.StripeError?.Type is "card_error" or "invalid_request_error")
        {
            throw new StripeGatewayException(ex.StripeError.Message ?? ex.Message);
        }
    }

    public async Task CancelSubscriptionAsync(string stripeSubscriptionId, CancellationToken ct)
    {
        try
        {
            await subscriptions.CancelAsync(stripeSubscriptionId, cancellationToken: ct);
        }
        catch (StripeException ex) when (ex.StripeError?.Code == "resource_missing")
        {
        }
        catch (StripeException ex) when (ex.StripeError?.Type == "invalid_request_error")
        {
            // Cancelling an already-cancelled subscription is an error in Stripe; treat it as done.
            var current = await subscriptions.GetAsync(stripeSubscriptionId, cancellationToken: ct);
            if (current.Status != "canceled") throw;
        }
    }
}

/// <summary>
/// Development only (Stripe:UseFake). Every customer "has" a card and every subscription succeeds, except for the price id
/// <see cref="DeclinedPriceId"/>, which is declined so the fail-fast path can be tried.
/// </summary>
public class FakeStripeGateway(ILogger<FakeStripeGateway> logger) : IStripeGateway
{
    public const string DeclinedPriceId = "price_fake_declined";

    public Task<string> CreateCustomerAsync(Guid userId, string? email, CancellationToken ct)
    {
        logger.LogInformation("FAKE Stripe: customer for {UserId}", userId);
        return Task.FromResult("cus_fake_" + userId.ToString("N")[..14]);
    }

    public Task<string?> FindPaymentMethodAsync(string stripeCustomerId, CancellationToken ct) =>
        Task.FromResult<string?>("pm_fake_card");

    public Task<string> CreateSubscriptionAsync(
        Guid subscriptionId, string stripeCustomerId, string stripePriceId, string paymentMethodId,
        IReadOnlyDictionary<string, string> metadata, CancellationToken ct)
    {
        if (stripePriceId == DeclinedPriceId) throw new StripeGatewayException("Your card was declined. (fake)");

        logger.LogInformation("FAKE Stripe: subscription for {Customer} on {Price}", stripeCustomerId, stripePriceId);
        return Task.FromResult("sub_fake_" + RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 20));
    }

    public Task CancelSubscriptionAsync(string stripeSubscriptionId, CancellationToken ct)
    {
        logger.LogInformation("FAKE Stripe: cancel {Subscription}", stripeSubscriptionId);
        return Task.CompletedTask;
    }
}
