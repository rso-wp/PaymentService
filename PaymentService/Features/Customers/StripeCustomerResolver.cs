using Microsoft.EntityFrameworkCore;
using Npgsql;
using PaymentService.Infrastructure.Persistance;
using PaymentService.Infrastructure.StripeIntegration;

namespace PaymentService.Features.Customers;

public class StripeCustomerResolver(PaymentDbContext db, IStripeGateway stripe)
{
    /// <summary>
    /// Returns the user's Stripe customer id, creating the Stripe customer and local row on first use. Normally the
    /// customer already exists (created from UserCreated); this is also the fallback when that message has not arrived yet.
    /// </summary>
    public async Task<string> GetOrCreateStripeCustomerIdAsync(Guid userId, string? email, CancellationToken ct)
    {
        var existing = await db.Customers.FindAsync([userId], ct);
        if (existing is not null) return existing.StripeCustomerId;

        var stripeCustomerId = await stripe.CreateCustomerAsync(userId, email, ct);

        db.Customers.Add(new Domain.Customer { Id = userId, StripeCustomerId = stripeCustomerId });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return (await db.Customers.SingleAsync(c => c.Id == userId, ct)).StripeCustomerId;
        }

        return stripeCustomerId;
    }
}
