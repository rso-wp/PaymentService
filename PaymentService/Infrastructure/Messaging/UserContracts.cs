namespace PaymentService.Infrastructure.Messaging;

// Wire contract shared with UserService (copy in UserService/Api/Infrastructure/Messaging/UserContracts.cs).
// Keep both copies in sync. Payloads are JSON (camelCase) on NATS JetStream stream USERS.

public static class UserSubjects
{
    public const string Stream = "USERS";
    public const string All = "users.>";

    public const string Created = "users.events.created";
}

/// <summary>A user signed in for the first time. <see cref="UserId"/> is the Entra oid.</summary>
public record UserCreated(Guid UserId, string Email);
