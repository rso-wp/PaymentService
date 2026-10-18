using System.Security.Cryptography;
using Marten;
using Stripe;

namespace PaymentService.Infrastructure.StripeIntegration;

/// <summary>Stripe declined or rejected a request; <see cref="Exception.Message"/> is safe to show the user.</summary>
public class StripeGatewayException(string message) : Exception(message);

/// <summary>A saved card, as shown to the user.</summary>
public record CardSummary(string Id, string Brand, string Last4, long ExpMonth, long ExpYear);

/// <summary>The Stripe calls PaymentService makes, so local development can run without Stripe keys.</summary>
public interface IStripeGateway
{
    Task<string> CreateCustomerAsync(Guid userId, string? email, CancellationToken ct);

    /// <summary>The customer's cards, newest first.</summary>
    Task<IReadOnlyList<CardSummary>> ListCardsAsync(string stripeCustomerId, CancellationToken ct);

    /// <summary>The card if it exists and belongs to the customer, else null.</summary>
    Task<CardSummary?> GetCardAsync(string stripeCustomerId, string paymentMethodId, CancellationToken ct);

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

    public async Task<IReadOnlyList<CardSummary>> ListCardsAsync(string stripeCustomerId, CancellationToken ct)
    {
        var cards = await paymentMethods.ListAsync(
            new PaymentMethodListOptions { Customer = stripeCustomerId, Type = "card", Limit = 20 },
            cancellationToken: ct);
        return cards.Data.Select(ToSummary).ToList();
    }

    public async Task<CardSummary?> GetCardAsync(string stripeCustomerId, string paymentMethodId, CancellationToken ct)
    {
        try
        {
            var pm = await paymentMethods.GetAsync(paymentMethodId, cancellationToken: ct);
            return pm.CustomerId == stripeCustomerId && pm.Type == "card" ? ToSummary(pm) : null;
        }
        catch (StripeException ex) when (ex.StripeError?.Code == "resource_missing")
        {
            return null;
        }
    }

    public async Task<string?> FindPaymentMethodAsync(string stripeCustomerId, CancellationToken ct)
    {
        var customer = await customers.GetAsync(stripeCustomerId, cancellationToken: ct);
        if (!string.IsNullOrEmpty(customer.InvoiceSettings?.DefaultPaymentMethodId))
            return customer.InvoiceSettings.DefaultPaymentMethodId;

        // Cards saved through the setup intent are attached but not made default.
        return (await ListCardsAsync(stripeCustomerId, ct)).FirstOrDefault()?.Id;
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

    private static CardSummary ToSummary(PaymentMethod pm) =>
        new(pm.Id, CardBrands.Display(pm.Card?.Brand), pm.Card?.Last4 ?? "????", pm.Card?.ExpMonth ?? 0, pm.Card?.ExpYear ?? 0);
}

public static class CardBrands
{
    public static string Display(string? brand) => brand?.ToLowerInvariant() switch
    {
        "visa" => "Visa",
        "mastercard" => "Mastercard",
        "amex" => "Amex",
        _ => "Card"
    };

    public static string FromNumber(string digits) =>
        digits.StartsWith('4') ? "Visa"
        : digits.Length > 1 && (digits[..2] is "51" or "52" or "53" or "54" or "55" || digits[0] == '2') ? "Mastercard"
        : digits.StartsWith("34") || digits.StartsWith("37") ? "Amex"
        : "Card";
}

/// <summary>Fake saved card (Marten document), only used by <see cref="FakeStripeGateway"/>.</summary>
public class FakeCard
{
    public required string Id { get; set; }
    public required string CustomerId { get; set; }
    public required string Brand { get; set; }
    public required string Last4 { get; set; }
    public int ExpMonth { get; set; }
    public int ExpYear { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Development only (Stripe:UseFake). Cards are added through <c>POST /payment-methods/fake</c> and stored in Marten.
/// Subscriptions succeed, except with a card ending in 0002 (Stripe's generic-decline test card) or the price id
/// <see cref="DeclinedPriceId"/>, which are declined so the fail-fast path can be tried.
/// </summary>
public class FakeStripeGateway(IDocumentStore store, ILogger<FakeStripeGateway> logger) : IStripeGateway
{
    public const string DeclinedPriceId = "price_fake_declined";

    public Task<string> CreateCustomerAsync(Guid userId, string? email, CancellationToken ct)
    {
        logger.LogInformation("FAKE Stripe: customer for {UserId}", userId);
        return Task.FromResult("cus_fake_" + userId.ToString("N")[..14]);
    }

    public async Task<IReadOnlyList<CardSummary>> ListCardsAsync(string stripeCustomerId, CancellationToken ct)
    {
        await using var session = store.QuerySession();
        var cards = await session.Query<FakeCard>()
            .Where(c => c.CustomerId == stripeCustomerId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
        return cards.Select(c => new CardSummary(c.Id, c.Brand, c.Last4, c.ExpMonth, c.ExpYear)).ToList();
    }

    public async Task<CardSummary?> GetCardAsync(string stripeCustomerId, string paymentMethodId, CancellationToken ct) =>
        (await ListCardsAsync(stripeCustomerId, ct)).FirstOrDefault(c => c.Id == paymentMethodId);

    public async Task<string?> FindPaymentMethodAsync(string stripeCustomerId, CancellationToken ct) =>
        (await ListCardsAsync(stripeCustomerId, ct)).FirstOrDefault()?.Id;

    public async Task<CardSummary> AddCardAsync(string stripeCustomerId, string number, int expMonth, int expYear, CancellationToken ct)
    {
        var digits = new string(number.Where(char.IsDigit).ToArray());
        var card = new FakeCard
        {
            Id = "pm_fake_" + RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 16),
            CustomerId = stripeCustomerId,
            Brand = CardBrands.FromNumber(digits),
            Last4 = digits[^4..],
            ExpMonth = expMonth,
            ExpYear = expYear,
            CreatedAt = DateTime.UtcNow
        };

        await using var session = store.LightweightSession();
        session.Insert(card);
        await session.SaveChangesAsync(ct);

        logger.LogInformation("FAKE Stripe: card {Brand} {Last4} for {Customer}", card.Brand, card.Last4, stripeCustomerId);
        return new CardSummary(card.Id, card.Brand, card.Last4, card.ExpMonth, card.ExpYear);
    }

    public async Task<string> CreateSubscriptionAsync(
        Guid subscriptionId, string stripeCustomerId, string stripePriceId, string paymentMethodId,
        IReadOnlyDictionary<string, string> metadata, CancellationToken ct)
    {
        var card = await GetCardAsync(stripeCustomerId, paymentMethodId, ct);
        if (stripePriceId == DeclinedPriceId || card?.Last4 == "0002")
            throw new StripeGatewayException("Your card was declined.");

        logger.LogInformation("FAKE Stripe: subscription for {Customer} on {Price}", stripeCustomerId, stripePriceId);
        return "sub_fake_" + RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 20);
    }

    public Task CancelSubscriptionAsync(string stripeSubscriptionId, CancellationToken ct)
    {
        logger.LogInformation("FAKE Stripe: cancel {Subscription}", stripeSubscriptionId);
        return Task.CompletedTask;
    }
}
