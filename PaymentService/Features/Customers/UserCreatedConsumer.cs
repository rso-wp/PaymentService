using System.Text.Json;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using PaymentService.Infrastructure.Messaging;

namespace PaymentService.Features.Customers;

/// <summary>
/// Durable JetStream consumer for <see cref="UserCreated"/>: creates the user's Stripe customer up front, so it exists
/// before the user adds a card or subscribes. Idempotent (local row + Stripe idempotency key), so redelivery is safe.
/// </summary>
public class UserCreatedConsumer(
    INatsJSContext js,
    IServiceScopeFactory scopes,
    ILogger<UserCreatedConsumer> logger) : BackgroundService
{
    private const string ConsumerName = "payment-service";
    private const int MaxDeliver = 10;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "NATS consumer stopped, reconnecting in 5s");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await js.EnsureUsersStreamAsync(ct);

        var consumer = await js.CreateOrUpdateConsumerAsync(
            UserSubjects.Stream,
            new ConsumerConfig(ConsumerName)
            {
                FilterSubject = UserSubjects.Created,
                AckPolicy = ConsumerConfigAckPolicy.Explicit,
                MaxDeliver = MaxDeliver
            },
            ct);

        await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: ct))
        {
            var attempt = (int)(msg.Metadata?.NumDelivered ?? 1);
            try
            {
                var e = JsonSerializer.Deserialize<UserCreated>(msg.Data ?? [], NatsExtensions.Json)
                    ?? throw new InvalidOperationException("Empty UserCreated message.");

                using var scope = scopes.CreateScope();
                var customers = scope.ServiceProvider.GetRequiredService<StripeCustomerResolver>();
                var stripeCustomerId = await customers.GetOrCreateStripeCustomerIdAsync(e.UserId, e.Email, ct);

                logger.LogInformation("User {UserId} has Stripe customer {StripeCustomerId}", e.UserId, stripeCustomerId);
                await msg.AckAsync(cancellationToken: ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (attempt < MaxDeliver)
                {
                    logger.LogWarning(ex, "UserCreated failed (attempt {Attempt}/{Max}), retrying", attempt, MaxDeliver);
                    await msg.NakAsync(delay: TimeSpan.FromSeconds(Math.Min(10 * attempt, 120)), cancellationToken: ct);
                    continue;
                }

                // Not fatal: the customer is created on demand by setup-intent / TryMakeSubscription.
                logger.LogError(ex, "UserCreated failed permanently after {Attempt} attempts", attempt);
                await msg.AckAsync(cancellationToken: ct);
            }
        }
    }
}
