using Microsoft.Extensions.Options;
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
            services.AddSingleton<IStripeGateway, FakeStripeGateway>();
        else
            services.AddTransient<IStripeGateway, StripeGateway>();

        return services;
    }
}
