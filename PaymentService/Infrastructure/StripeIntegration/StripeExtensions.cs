using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PaymentService.Infrastructure.Observability;
using Stripe;

namespace PaymentService.Infrastructure.StripeIntegration;

public static class StripeExtensions
{
    public static IServiceCollection AddStripeIntegration(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<StripeOptions>()
            .Bind(configuration.GetSection(StripeOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IStripeClient>(sp =>
            new StripeClient(sp.GetRequiredService<IOptions<StripeOptions>>().Value.SecretKey));

        services.AddTransient<CustomerService>();
        services.AddTransient<SubscriptionService>();
        services.AddTransient<SetupIntentService>();
        services.AddTransient<PaymentMethodService>();

        if (environment.IsDevelopment() && configuration.GetValue<bool>($"{StripeOptions.SectionName}:UseFake"))
        {
            services.AddSingleton<FakeStripeGateway>();
            services.AddSingleton<IStripeGateway>(sp => sp.GetRequiredService<FakeStripeGateway>());
        }
        else
            services.AddTransient<IStripeGateway, StripeGateway>();

        // Not fatal (the service still works, and terraform starts with a placeholder until the endpoint exists), but visible.
        services.AddHealthChecks().AddCheck<WebhookSecretHealthCheck>(
            "stripe-webhook-secret", HealthStatus.Degraded, [ObservabilityExtensions.ReadyTag]);

        return services;
    }

    /// <summary>Logs an error at startup while Stripe webhooks cannot work (see <see cref="StripeOptions.WebhookSecretIsPlaceholder"/>).</summary>
    public static void LogStripeProblems(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<StripeOptions>>().Value;
        if ((options.UseFake && app.Environment.IsDevelopment()) || !options.WebhookSecretIsPlaceholder) return;

        app.Logger.LogError("Stripe:WebhookSecret is a placeholder: every Stripe webhook will be rejected, so failed " +
            "payments only shut sites down when the subscription reconciliation runs. Set it to the endpoint's signing secret.");
    }

    private sealed class WebhookSecretHealthCheck(IOptions<StripeOptions> options, IStripeGateway stripe) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(stripe is FakeStripeGateway || !options.Value.WebhookSecretIsPlaceholder
                ? HealthCheckResult.Healthy()
                : new HealthCheckResult(context.Registration.FailureStatus,
                    "Stripe:WebhookSecret is a placeholder: every Stripe webhook is rejected."));
    }
}
