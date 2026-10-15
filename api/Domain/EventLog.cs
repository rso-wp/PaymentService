namespace PaymentService.Domain;

public class EventLog
{
    public Guid EventId { get; set; }
    public Guid AggregateId { get; set; }
    public required string AggregateType { get; set; }
    public required string EventType { get; set; }
    public required string Payload { get; set; }
    public DateTime Timestamp { get; set; }
    public int Version { get; set; }
}
