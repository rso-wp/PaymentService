using Microsoft.Extensions.Options;
using Stripe;

namespace PaymentService.Infrastructure.StripeIntegration;

public static class StripeExtensions
{
    public static IServiceCollection AddStripeIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StripeOptions>()
            .Bind(configuration.GetSection(StripeOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IStripeClient>(sp =>
            new StripeClient(sp.GetRequiredService<IOptions<StripeOptions>>().Value.SecretKey));

        services.AddTransient<CustomerService>();
        services.AddTransient<SubscriptionService>();

        return services;
    }
}
