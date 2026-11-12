using Grpc.Core;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.Core;
using NSubstitute.ExceptionExtensions;
using PaymentService.Domain;
using PaymentService.Features.Customers;
using PaymentService.Features.StartSubscription;
using PaymentService.Infrastructure.StripeIntegration;
using PaymentService.Protos;
using static PaymentService.Domain.Subscription;

namespace Test;

/// <summary>Checks that each gRPC method calls the right collaborators (Stripe, customer resolver, Marten events).</summary>
public class PaymentGrpcServiceTests
{
    private const string PriceId = "price_123";
    private const string CustomerId = "cus_1";
    private const string CardId = "pm_1";
    private const string StripeSubId = "sub_1";

    private readonly IDocumentSession session = Substitute.For<IDocumentSession>();
    private readonly IStripeCustomerResolver customers = Substitute.For<IStripeCustomerResolver>();
    private readonly IStripeGateway stripe = Substitute.For<IStripeGateway>();
    private readonly PaymentGrpcService service;

    private readonly Guid userId = Guid.NewGuid();
    private readonly Guid subscriptionId = Guid.NewGuid();

    public PaymentGrpcServiceTests()
    {
        service = new PaymentGrpcService(session, customers, stripe, NullLogger<PaymentGrpcService>.Instance);
        customers.GetOrCreateStripeCustomerIdAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(CustomerId);
        // NSubstitute answers "" for a Task<string?>; "nothing found" must be null.
        stripe.FindPaymentMethodAsync(default!, default).ReturnsForAnyArgs((string?)null);
        stripe.FindSubscriptionAsync(default!, default, default).ReturnsForAnyArgs((string?)null);
    }

    private TryMakeSubscriptionRequest MakeRequest(string paymentMethodId = "") => new()
    {
        UserId = userId.ToString(),
        StripePriceId = PriceId,
        ServiceId = "wordpress",
        PaymentMethodId = paymentMethodId,
        RequestId = subscriptionId.ToString()
    };

    private void ExistingSubscription(SubscriptionStatus status, Guid? owner = null, string? failure = null) =>
        session.LoadAsync<Subscription>(subscriptionId, Arg.Any<CancellationToken>()).Returns(new Subscription
        {
            Id = subscriptionId,
            UserId = owner ?? userId,
            ServiceId = "wordpress",
            PriceId = PriceId,
            Status = status,
            StripeSubscriptionId = status == SubscriptionStatus.Active ? StripeSubId : null,
            FailureReason = failure
        });

    private void StripeCreatesSubscription() =>
        stripe.CreateSubscriptionAsync(
                subscriptionId, CustomerId, PriceId, CardId, Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(StripeSubId);

    /// <summary>Events passed to <c>session.Events.Append</c>, whichever overload was used, with the stream id and expected version.</summary>
    private List<(object? StreamId, object? ExpectedVersion, object Event)> AppendedEvents() =>
        session.Events.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "Append")
            .SelectMany(c =>
            {
                var args = c.GetArguments();
                var events = args.SelectMany(a => a is object[] many ? many : [a])
                    .Where(a => a is not null and not Guid and not long and not CancellationToken)
                    .Select(a => a!);
                return events.Select(e => (args[0], args.Length > 1 && args[1] is long v ? (object?)v : null, e));
            })
            .ToList();

    private void AssertEventAppended<T>(Func<T, bool>? check = null) where T : class
    {
        var appended = Assert.Single(AppendedEvents());
        Assert.Equal(subscriptionId, appended.StreamId);
        // Only the first decision may win when TryMakeSubscription and ResolveSubscription race.
        Assert.Equal(2L, appended.ExpectedVersion);
        var e = Assert.IsType<T>(appended.Event);
        Assert.True(check == null || check(e), "The appended event has the wrong content.");
    }

    private void AssertNoDecisionAppended() => Assert.Empty(AppendedEvents());

    // ---- TryMakeSubscription ----

    [Fact]
    public async Task TryMake_InvalidUserId_IsRejectedWithoutTouchingStripe()
    {
        var request = MakeRequest();
        request.UserId = "not-a-guid";

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.TryMakeSubscription(request, new TestServerCallContext()));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        await stripe.DidNotReceiveWithAnyArgs().CreateSubscriptionAsync(default, default!, default!, default!, default!, default);
        session.Events.DidNotReceiveWithAnyArgs().StartStream<Subscription>(default(Guid), default(object[])!);
    }

    [Theory]
    [InlineData("", "wordpress")]
    [InlineData(PriceId, "")]
    public async Task TryMake_MissingPriceOrService_IsRejected(string price, string serviceId)
    {
        var request = MakeRequest();
        request.StripePriceId = price;
        request.ServiceId = serviceId;

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.TryMakeSubscription(request, new TestServerCallContext()));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task TryMake_NewSubscription_RecordsRequest_ThenChargesDefaultCard_ThenRecordsStarted()
    {
        stripe.FindPaymentMethodAsync(CustomerId, Arg.Any<CancellationToken>()).Returns(CardId);
        StripeCreatesSubscription();

        var response = await service.TryMakeSubscription(MakeRequest(), new TestServerCallContext());

        Assert.Equal("SUCCESSFUL", response.Status);
        Assert.Equal(StripeSubId, response.StripeSubscriptionId);

        // The request is recorded as a new stream before Stripe is called.
        session.Events.Received(1).StartStream<Subscription>(
            subscriptionId,
            Arg.Is<object[]>(e => e.Length == 1
                && ((SubscriptionRequested)e[0]).UserId == userId
                && ((SubscriptionRequested)e[0]).ServiceId == "wordpress"
                && ((SubscriptionRequested)e[0]).StripePriceId == PriceId));
        await customers.Received(1).GetOrCreateStripeCustomerIdAsync(userId, null, Arg.Any<CancellationToken>());
        await stripe.Received(1).FindPaymentMethodAsync(CustomerId, Arg.Any<CancellationToken>());
        await stripe.DidNotReceiveWithAnyArgs().GetCardAsync(default!, default!, default);
        await stripe.Received(1).CreateSubscriptionAsync(
            subscriptionId, CustomerId, PriceId, CardId,
            Arg.Is<IReadOnlyDictionary<string, string>>(m =>
                m[IStripeGateway.SubscriptionIdMetadata] == subscriptionId.ToString()
                && m["user_id"] == userId.ToString()
                && m["service_id"] == "wordpress"),
            Arg.Any<CancellationToken>());
        AssertEventAppended<SubscriptionStarted>(e => e.StripeSubscriptionId == StripeSubId);
        await session.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryMake_WithPaymentMethodId_ChecksTheCardBelongsToTheCustomer_InsteadOfUsingTheDefault()
    {
        stripe.GetCardAsync(CustomerId, CardId, Arg.Any<CancellationToken>())
            .Returns(new CardSummary(CardId, "visa", "4242", 12, 2030));
        StripeCreatesSubscription();

        var response = await service.TryMakeSubscription(MakeRequest(CardId), new TestServerCallContext());

        Assert.Equal("SUCCESSFUL", response.Status);
        await stripe.Received(1).GetCardAsync(CustomerId, CardId, Arg.Any<CancellationToken>());
        await stripe.DidNotReceiveWithAnyArgs().FindPaymentMethodAsync(default!, default);
    }

    [Fact]
    public async Task TryMake_CardNotOnAccount_RecordsFailure_AndNeverCreatesSubscription()
    {
        stripe.GetCardAsync(CustomerId, CardId, Arg.Any<CancellationToken>()).Returns((CardSummary?)null);

        var response = await service.TryMakeSubscription(MakeRequest(CardId), new TestServerCallContext());

        Assert.Equal("FAILED", response.Status);
        Assert.Equal("That card is not on your account.", response.Reason);
        await stripe.DidNotReceiveWithAnyArgs().CreateSubscriptionAsync(default, default!, default!, default!, default!, default);
        AssertEventAppended<SubscriptionStartFailed>(e => e.Reason == response.Reason);
    }

    [Fact]
    public async Task TryMake_NoCardOnFile_RecordsFailure()
    {
        stripe.FindPaymentMethodAsync(CustomerId, Arg.Any<CancellationToken>()).Returns((string?)null);

        var response = await service.TryMakeSubscription(MakeRequest(), new TestServerCallContext());

        Assert.Equal("FAILED", response.Status);
        AssertEventAppended<SubscriptionStartFailed>();
        await stripe.DidNotReceiveWithAnyArgs().CreateSubscriptionAsync(default, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task TryMake_StripeDeclines_RecordsFailureWithStripesReason()
    {
        stripe.FindPaymentMethodAsync(CustomerId, Arg.Any<CancellationToken>()).Returns(CardId);
        stripe.CreateSubscriptionAsync(default, default!, default!, default!, default!, default)
            .ReturnsForAnyArgs<string>(_ => throw new StripeGatewayException("Your card was declined."));

        var response = await service.TryMakeSubscription(MakeRequest(), new TestServerCallContext());

        Assert.Equal("FAILED", response.Status);
        Assert.Equal("Your card was declined.", response.Reason);
        AssertEventAppended<SubscriptionStartFailed>(e => e.Reason == "Your card was declined.");
    }

    [Fact]
    public async Task TryMake_UnexpectedError_ReturnsUnavailable_AndDoesNotRecordAnOutcome()
    {
        stripe.FindPaymentMethodAsync(CustomerId, Arg.Any<CancellationToken>()).Throws(new HttpRequestException("Stripe down"));

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.TryMakeSubscription(MakeRequest(), new TestServerCallContext()));

        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
        // Stripe may have created it, so it must stay Pending: neither started nor failed is recorded.
        AssertNoDecisionAppended();
    }

    [Fact]
    public async Task TryMake_RepeatedRequestForActiveSubscription_ReturnsTheFirstOutcome_WithoutCallingStripe()
    {
        ExistingSubscription(SubscriptionStatus.Active);

        var response = await service.TryMakeSubscription(MakeRequest(), new TestServerCallContext());

        Assert.Equal("SUCCESSFUL", response.Status);
        Assert.Equal(StripeSubId, response.StripeSubscriptionId);
        session.Events.DidNotReceiveWithAnyArgs().StartStream<Subscription>(default(Guid), default(object[])!);
        await stripe.DidNotReceiveWithAnyArgs().CreateSubscriptionAsync(default, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task TryMake_RepeatedRequestForPendingSubscription_ReturnsUnavailable()
    {
        ExistingSubscription(SubscriptionStatus.Pending);

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.TryMakeSubscription(MakeRequest(), new TestServerCallContext()));

        Assert.Equal(StatusCode.Unavailable, ex.StatusCode);
        await stripe.DidNotReceiveWithAnyArgs().CreateSubscriptionAsync(default, default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task TryMake_RequestIdOfAnotherUser_IsRejected()
    {
        ExistingSubscription(SubscriptionStatus.Active, owner: Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<RpcException>(() => service.TryMakeSubscription(MakeRequest(), new TestServerCallContext()));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    // ---- ResolveSubscription ----

    private static ResolveSubscriptionRequest ResolveRequest(Guid id) => new() { RequestId = id.ToString() };

    [Fact]
    public async Task Resolve_UnknownRequest_Fails_WithoutAskingStripe()
    {
        var response = await service.ResolveSubscription(ResolveRequest(subscriptionId), new TestServerCallContext());

        Assert.Equal("FAILED", response.Status);
        await stripe.DidNotReceiveWithAnyArgs().FindSubscriptionAsync(default!, default, default);
    }

    [Fact]
    public async Task Resolve_InvalidRequestId_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(
            () => service.ResolveSubscription(new ResolveSubscriptionRequest { RequestId = "x" }, new TestServerCallContext()));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Resolve_AlreadyDecided_ReturnsRecordedOutcome_WithoutAskingStripe()
    {
        ExistingSubscription(SubscriptionStatus.FailedToStart, failure: "declined");

        var response = await service.ResolveSubscription(ResolveRequest(subscriptionId), new TestServerCallContext());

        Assert.Equal("FAILED", response.Status);
        Assert.Equal("declined", response.Reason);
        await stripe.DidNotReceiveWithAnyArgs().FindSubscriptionAsync(default!, default, default);
    }

    [Fact]
    public async Task Resolve_PendingButStripeHasIt_RecordsStarted()
    {
        ExistingSubscription(SubscriptionStatus.Pending);
        stripe.FindSubscriptionAsync(CustomerId, subscriptionId, Arg.Any<CancellationToken>()).Returns(StripeSubId);

        var response = await service.ResolveSubscription(ResolveRequest(subscriptionId), new TestServerCallContext());

        Assert.Equal("SUCCESSFUL", response.Status);
        Assert.Equal(StripeSubId, response.StripeSubscriptionId);
        await customers.Received(1).GetOrCreateStripeCustomerIdAsync(userId, null, Arg.Any<CancellationToken>());
        AssertEventAppended<SubscriptionStarted>(e => e.StripeSubscriptionId == StripeSubId);
    }

    [Fact]
    public async Task Resolve_PendingStripeDoesNotHaveIt_RecentStream_StaysPending()
    {
        ExistingSubscription(SubscriptionStatus.Pending);
        StreamCreated(DateTimeOffset.UtcNow.AddMinutes(-1));

        var response = await service.ResolveSubscription(ResolveRequest(subscriptionId), new TestServerCallContext());

        Assert.Equal("PENDING", response.Status);
        AssertNoDecisionAppended();
    }

    [Fact]
    public async Task Resolve_PendingStripeDoesNotHaveIt_OldStream_RecordsFailure()
    {
        ExistingSubscription(SubscriptionStatus.Pending);
        StreamCreated(DateTimeOffset.UtcNow.AddHours(-1));

        var response = await service.ResolveSubscription(ResolveRequest(subscriptionId), new TestServerCallContext());

        Assert.Equal("FAILED", response.Status);
        AssertEventAppended<SubscriptionStartFailed>();
    }

    private void StreamCreated(DateTimeOffset created) =>
        session.Events.FetchStreamStateAsync(subscriptionId, Arg.Any<CancellationToken>())
            .Returns(new StreamState(subscriptionId, 1, typeof(Subscription), created, created));

    // ---- StopSubscription ----

    // The paths that look the subscription up (session.Query<Subscription>()) are not covered here: Marten's LINQ provider
    // cannot be mocked and needs a real Postgres database.

    [Fact]
    public async Task Stop_MissingStripeSubscriptionId_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<RpcException>(
            () => service.StopSubscription(new StopSubscriptionRequest(), new TestServerCallContext()));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        await stripe.DidNotReceiveWithAnyArgs().CancelSubscriptionAsync(default!, default);
    }
}
