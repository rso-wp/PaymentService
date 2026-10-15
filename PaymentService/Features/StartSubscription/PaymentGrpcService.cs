using System.Security.Cryptography;
using Grpc.Core;
using PaymentService.Protos;

namespace PaymentService.Features.StartSubscription;

public class PaymentGrpcService(ILogger<PaymentGrpcService> logger) : Payment.PaymentBase
{
    public override Task<TryMakeSubscriptionResponse> TryMakeSubscription(
        TryMakeSubscriptionRequest request,
        ServerCallContext context)
    {
        logger.LogInformation(
            "TryMakeSubscription called. UserId: {UserId}, StripePriceId: {StripePriceId}, ServiceId: {ServiceId}",
            request.UserId, request.StripePriceId, request.ServiceId);

        return Task.FromResult(new TryMakeSubscriptionResponse
        {
            StripeSubscriptionId = GenerateFakeSubscriptionId(),
            Status = "SUCCESSFUL"
        });
    }

    // Stripe subscription ids look like "sub_" followed by 24 alphanumeric characters.
    private static string GenerateFakeSubscriptionId()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return "sub_" + RandomNumberGenerator.GetString(chars, 24);
    }
}
