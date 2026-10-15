using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PaymentService.Domain;

namespace PaymentService.Infrastructure.Persistance.EventLog;

public class EventLogConfiguration : IEntityTypeConfiguration<Domain.EventLog>
{
    public void Configure(EntityTypeBuilder<Domain.EventLog> builder)
    {
        builder.ToTable("EventLogs");
        builder.HasKey(e => e.EventId);
        builder.Property(e => e.AggregateType).IsRequired().HasMaxLength(100);
        builder.Property(e => e.EventType).IsRequired().HasMaxLength(100);
        builder.Property(e => e.Payload).IsRequired();
        // AggregateId is a discriminated FK — no DB-level constraint since it points to two tables
        builder.HasIndex(e => new { e.AggregateId, e.AggregateType });
    }
}
