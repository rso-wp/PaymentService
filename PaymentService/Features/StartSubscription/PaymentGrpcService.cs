using Grpc.Core;
using JasperFx;
using JasperFx.Events;
using Marten;
using PaymentService.Domain;
using PaymentService.Features.Customers;
using PaymentService.Infrastructure.StripeIntegration;
using PaymentService.Protos;
using static PaymentService.Domain.Subscription;

namespace PaymentService.Features.StartSubscription;

/// <summary>
/// Synchronous (fail-fast) subscription calls for ProvisioningService. Every step is recorded as an event on the
/// subscription's Marten stream.
/// </summary>
public class PaymentGrpcService(
    IDocumentSession session,
    StripeCustomerResolver customers,
    IStripeGateway stripe,
    ILogger<PaymentGrpcService> logger) : Payment.PaymentBase
{
    private const string Successful = "SUCCESSFUL";
    private const string Failed = "FAILED";
    private const string Pending = "PENDING";

    /// <summary>
    /// How long a TryMakeSubscription call can still be running after it was recorded (Stripe calls time out after 80 s and
    /// are retried twice). A Pending subscription older than this that Stripe does not have never will be.
    /// </summary>
    private static readonly TimeSpan InFlightWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Stream version once the outcome is appended (<see cref="SubscriptionRequested"/> + the outcome; Marten's expected
    /// version is the one after the append). So when TryMakeSubscription and ResolveSubscription decide the same
    /// subscription at once, only the first decision counts.
    /// </summary>
    private const long DecidedVersion = 2;

    public override async Task<TryMakeSubscriptionResponse> TryMakeSubscription(
        TryMakeSubscriptionRequest request,
        ServerCallContext context)
    {
        var ct = context.CancellationToken;
        if (!Guid.TryParse(request.UserId, out var userId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "user_id must be a Guid."));
        if (string.IsNullOrWhiteSpace(request.StripePriceId) || string.IsNullOrWhiteSpace(request.ServiceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "stripe_price_id and service_id are required."));

        // The caller's request id is the subscription id, so asking again (after a timeout) never starts a second one.
        var subscriptionId = string.IsNullOrEmpty(request.RequestId) ? Guid.NewGuid() : ParseRequestId(request.RequestId);

        if (await session.LoadAsync<Subscription>(subscriptionId, ct) is { } existing)
        {
            if (existing.UserId != userId)
                throw new RpcException(new Status(StatusCode.InvalidArgument, "request_id belongs to another user."));
            return existing.Status == SubscriptionStatus.Pending ? throw StillStarting() : Outcome(existing);
        }

        // Recorded before Stripe is called, so a failed or crashed attempt is still on record.
        session.Events.StartStream<Subscription>(
            subscriptionId, new SubscriptionRequested(userId, request.ServiceId, request.StripePriceId));
        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is Marten.Exceptions.ExistingStreamIdCollisionException
                                       or JasperFx.Events.ExistingStreamIdCollisionException)
        {
            throw StillStarting();
        }

        // Stripe may be called from here on, so the outcome is recorded even if the caller stops waiting: the call's
        // cancellation is not passed on.
        var none = CancellationToken.None;
        try
        {
            var customerId = await customers.GetOrCreateStripeCustomerIdAsync(userId, email: null, none);

            string paymentMethodId;
            if (string.IsNullOrEmpty(request.PaymentMethodId))
            {
                paymentMethodId = await stripe.FindPaymentMethodAsync(customerId, none)
                    ?? throw new StripeGatewayException("No payment method on file. Add a card first.");
            }
            else
            {
                // Only the user's own cards can be charged.
                paymentMethodId = (await stripe.GetCardAsync(customerId, request.PaymentMethodId, none))?.Id
                    ?? throw new StripeGatewayException("That card is not on your account.");
            }

            var stripeSubscriptionId = await stripe.CreateSubscriptionAsync(
                subscriptionId, customerId, request.StripePriceId, paymentMethodId,
                new Dictionary<string, string>
                {
                    [IStripeGateway.SubscriptionIdMetadata] = subscriptionId.ToString(),
                    ["user_id"] = userId.ToString(),
                    ["service_id"] = request.ServiceId
                },
                none);

            return await RecordStartedAsync(subscriptionId, stripeSubscriptionId);
        }
        catch (StripeGatewayException ex)
        {
            return await RecordFailedAsync(subscriptionId, ex.Message);
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            // Unknown outcome (Stripe or the database unreachable): Stripe may have created the subscription, so it is not
            // recorded as failed. It stays Pending until ResolveSubscription finds out.
            logger.LogError(ex, "Subscription {SubscriptionId} could not be started", subscriptionId);
            throw new RpcException(new Status(StatusCode.Unavailable, "Payment provider error."));
        }
    }

    public override async Task<TryMakeSubscriptionResponse> ResolveSubscription(
        ResolveSubscriptionRequest request,
        ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var subscriptionId = ParseRequestId(request.RequestId);

        // Never recorded: the call did not get as far as Stripe, so nothing was charged.
        if (await session.LoadAsync<Subscription>(subscriptionId, ct) is not { } subscription)
            return new TryMakeSubscriptionResponse { Status = Failed, Reason = "The payment was not started." };
        if (subscription.Status != SubscriptionStatus.Pending) return Outcome(subscription);

        var customerId = await customers.GetOrCreateStripeCustomerIdAsync(subscription.UserId, email: null, ct);
        if (await stripe.FindSubscriptionAsync(customerId, subscriptionId, ct) is { } stripeSubscriptionId)
            return await RecordStartedAsync(subscriptionId, stripeSubscriptionId);

        var stream = await session.Events.FetchStreamStateAsync(subscriptionId, ct);
        if (stream is not null && stream.Created > DateTimeOffset.UtcNow - InFlightWindow)
            return new TryMakeSubscriptionResponse { Status = Pending };

        return await RecordFailedAsync(subscriptionId, "The payment was not completed. You were not charged.");
    }

    public override async Task<StopSubscriptionResponse> StopSubscription(
        StopSubscriptionRequest request,
        ServerCallContext context)
    {
        var ct = context.CancellationToken;
        if (string.IsNullOrWhiteSpace(request.StripeSubscriptionId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "stripe_subscription_id is required."));

        var subscription = await session.Query<Subscription>()
            .FirstOrDefaultAsync(s => s.StripeSubscriptionId == request.StripeSubscriptionId, ct);

        if (subscription is null)
        {
            // Unknown here (e.g. created before event sourcing): still make sure Stripe stops charging.
            logger.LogWarning("StopSubscription for unknown subscription {StripeSubscriptionId}", request.StripeSubscriptionId);
            await stripe.CancelSubscriptionAsync(request.StripeSubscriptionId, ct);
            return new StopSubscriptionResponse { Status = Successful };
        }

        if (subscription.Status == SubscriptionStatus.Canceled)
            return new StopSubscriptionResponse { Status = Successful };

        await stripe.CancelSubscriptionAsync(request.StripeSubscriptionId, ct);

        session.Events.Append(subscription.Id, new SubscriptionCanceled(StripeEventId: null));
        await session.SaveChangesAsync(ct);

        logger.LogInformation("Subscription {SubscriptionId} canceled", subscription.Id);
        return new StopSubscriptionResponse { Status = Successful };
    }

    private async Task<TryMakeSubscriptionResponse> RecordStartedAsync(Guid subscriptionId, string stripeSubscriptionId)
    {
        session.Events.Append(subscriptionId, DecidedVersion, new SubscriptionStarted(stripeSubscriptionId));
        try
        {
            await session.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (IsConflict(ex))
        {
            var current = await LoadAsync(subscriptionId);
            logger.LogInformation("Subscription {SubscriptionId} was decided concurrently: {Status}", subscriptionId, current.Status);
            if (current.Status == SubscriptionStatus.FailedToStart)
            {
                // ResolveSubscription gave up on this call before Stripe answered: do not leave it charging.
                logger.LogWarning("Subscription {SubscriptionId} was started after it was given up; cancelling {StripeSubscriptionId}",
                    subscriptionId, stripeSubscriptionId);
                await stripe.CancelSubscriptionAsync(stripeSubscriptionId, CancellationToken.None);
            }
            return Outcome(current);
        }

        logger.LogInformation("Subscription {SubscriptionId} started ({StripeSubscriptionId})", subscriptionId, stripeSubscriptionId);
        return new TryMakeSubscriptionResponse { StripeSubscriptionId = stripeSubscriptionId, Status = Successful };
    }

    private async Task<TryMakeSubscriptionResponse> RecordFailedAsync(Guid subscriptionId, string reason)
    {
        session.Events.Append(subscriptionId, DecidedVersion, new SubscriptionStartFailed(reason));
        try
        {
            await session.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (IsConflict(ex))
        {
            var current = await LoadAsync(subscriptionId);
            logger.LogInformation("Subscription {SubscriptionId} was decided concurrently: {Status}", subscriptionId, current.Status);
            return Outcome(current);
        }

        logger.LogInformation("Subscription {SubscriptionId} not started: {Reason}", subscriptionId, reason);
        return new TryMakeSubscriptionResponse { Status = Failed, Reason = reason };
    }

    private static TryMakeSubscriptionResponse Outcome(Subscription subscription) => subscription.Status switch
    {
        SubscriptionStatus.Active => new() { Status = Successful, StripeSubscriptionId = subscription.StripeSubscriptionId },
        SubscriptionStatus.Pending => new() { Status = Pending },
        SubscriptionStatus.Canceled => new() { Status = Failed, Reason = "The subscription was cancelled." },
        _ => new() { Status = Failed, Reason = subscription.FailureReason ?? "The subscription could not be started." }
    };

    private async Task<Subscription> LoadAsync(Guid subscriptionId) =>
        await session.LoadAsync<Subscription>(subscriptionId, CancellationToken.None)
            ?? throw new InvalidOperationException($"Subscription {subscriptionId} not found.");

    private static bool IsConflict(Exception ex) => ex is EventStreamUnexpectedMaxEventIdException or ConcurrencyException;

    private static Guid ParseRequestId(string value) => Guid.TryParse(value, out var id)
        ? id
        : throw new RpcException(new Status(StatusCode.InvalidArgument, "request_id must be a Guid."));

    private static RpcException StillStarting() =>
        new(new Status(StatusCode.Unavailable, "The subscription is still being started."));
}
