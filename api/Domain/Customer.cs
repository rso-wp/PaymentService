namespace PaymentService.Domain;

public class Customer
{
    public Guid Id { get; set; }       // same value as UserId from UserService
    public required string StripeCustomerId { get; set; }
}
