using Marten;
using PaymentService.Domain;
using Wolverine;

namespace PaymentService.Features.Reconciliation;

/// <summary>Wolverine message: compare one Active subscription with Stripe (<see cref="CheckSubscriptionHandler"/>).</summary>
public record CheckSubscription(Guid SubscriptionId);

/// <summary>
/// Safety net for webhooks that never arrive (endpoint down for longer than Stripe retries, wrong signing secret, event
/// type not subscribed): every <see cref="Interval"/> each Active subscription is checked against Stripe, on the durable
/// local queue. Running it on several replicas at once only repeats checks, which are idempotent.
/// </summary>
public class SubscriptionReconciler(IServiceScopeFactory scopes, ILogger<SubscriptionReconciler> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRun = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(FirstRun, ct);
            using var timer = new PeriodicTimer(Interval);
            do
            {
                try
                {
                    await QueueChecksAsync(ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogError(ex, "Queueing the subscription checks failed, trying again in {Interval}", Interval);
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task QueueChecksAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var active = await session.Query<Subscription>()
            .Where(s => s.Status == SubscriptionStatus.Active)
            .Select(s => s.Id)
            .ToListAsync(ct);
        foreach (var id in active) await bus.PublishAsync(new CheckSubscription(id));

        logger.LogInformation("Checking {Count} active subscriptions against Stripe", active.Count);
    }
}
