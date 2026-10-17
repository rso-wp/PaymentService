using Grpc.Core;
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

    public override async Task<TryMakeSubscriptionResponse> TryMakeSubscription(
        TryMakeSubscriptionRequest request,
        ServerCallContext context)
    {
        var ct = context.CancellationToken;
        if (!Guid.TryParse(request.UserId, out var userId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "user_id must be a Guid."));
        if (string.IsNullOrWhiteSpace(request.StripePriceId) || string.IsNullOrWhiteSpace(request.ServiceId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "stripe_price_id and service_id are required."));

        // Recorded before Stripe is called, so a failed or crashed attempt is still on record.
        var subscriptionId = Guid.NewGuid();
        session.Events.StartStream<Subscription>(
            subscriptionId, new SubscriptionRequested(userId, request.ServiceId, request.StripePriceId));
        await session.SaveChangesAsync(ct);

        try
        {
            var customerId = await customers.GetOrCreateStripeCustomerIdAsync(userId, email: null, ct);

            var paymentMethodId = await stripe.FindPaymentMethodAsync(customerId, ct)
                ?? throw new StripeGatewayException("No payment method on file. Add a card first.");

            var stripeSubscriptionId = await stripe.CreateSubscriptionAsync(
                subscriptionId, customerId, request.StripePriceId, paymentMethodId,
                new Dictionary<string, string>
                {
                    ["subscription_id"] = subscriptionId.ToString(),
                    ["user_id"] = userId.ToString(),
                    ["service_id"] = request.ServiceId
                },
                ct);

            session.Events.Append(subscriptionId, new SubscriptionStarted(stripeSubscriptionId));
            await session.SaveChangesAsync(ct);

            logger.LogInformation("Subscription {SubscriptionId} started ({StripeSubscriptionId})", subscriptionId, stripeSubscriptionId);
            return new TryMakeSubscriptionResponse { StripeSubscriptionId = stripeSubscriptionId, Status = Successful };
        }
        catch (StripeGatewayException ex)
        {
            session.Events.Append(subscriptionId, new SubscriptionStartFailed(ex.Message));
            await session.SaveChangesAsync(ct);

            logger.LogInformation("Subscription {SubscriptionId} declined: {Reason}", subscriptionId, ex.Message);
            return new TryMakeSubscriptionResponse { Status = Failed, Reason = ex.Message };
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            // Stripe unreachable / misconfigured: close the stream and report an infrastructure error, not a decline.
            logger.LogError(ex, "Subscription {SubscriptionId} could not be started", subscriptionId);
            session.Events.Append(subscriptionId, new SubscriptionStartFailed("Payment provider error."));
            await session.SaveChangesAsync(CancellationToken.None);
            throw new RpcException(new Status(StatusCode.Unavailable, "Payment provider error."));
        }
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
}
