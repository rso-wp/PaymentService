using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PaymentService.Domain;

namespace PaymentService.Infrastructure.Persistance.Subscriptions;

public class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> builder)
    {
        builder.ToTable("Subscriptions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.StripeSubscriptionId).HasMaxLength(255);
        builder.HasIndex(s => s.StripeSubscriptionId).IsUnique();
        builder.Property(s => s.PriceId).IsRequired().HasMaxLength(255);
        builder.Property(s => s.Status).HasConversion<string>().IsRequired();
        builder.HasIndex(s => s.UserId);
        builder.HasIndex(s => s.ServiceId);
    }
}
