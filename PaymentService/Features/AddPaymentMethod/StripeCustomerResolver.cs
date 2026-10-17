using Microsoft.EntityFrameworkCore;
using Npgsql;
using PaymentService.Domain;
using PaymentService.Infrastructure.Persistance;
using Stripe;

namespace PaymentService.Features.AddPaymentMethod;

public class StripeCustomerResolver(PaymentDbContext db, Stripe.CustomerService stripeCustomers)
{
    /// <summary>Returns the user's Stripe customer id, creating the Stripe customer and local row on first use.</summary>
    public async Task<string> GetOrCreateStripeCustomerIdAsync(Guid userId, string? email, CancellationToken ct)
    {
        var existing = await db.Customers.FindAsync([userId], ct);
        if (existing is not null) return existing.StripeCustomerId;

        // Idempotency key makes a concurrent first call get the same Stripe customer back.
        var stripeCustomer = await stripeCustomers.CreateAsync(
            new CustomerCreateOptions
            {
                Email = string.IsNullOrWhiteSpace(email) ? null : email,
                Metadata = new Dictionary<string, string> { ["user_id"] = userId.ToString() }
            },
            new RequestOptions { IdempotencyKey = $"customer-{userId}" },
            ct);

        db.Customers.Add(new Domain.Customer { Id = userId, StripeCustomerId = stripeCustomer.Id });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return (await db.Customers.SingleAsync(c => c.Id == userId, ct)).StripeCustomerId;
        }

        return stripeCustomer.Id;
    }
}
