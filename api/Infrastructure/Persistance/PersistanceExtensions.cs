using Microsoft.EntityFrameworkCore;

namespace PaymentService.Infrastructure.Persistance;

public static class PersistanceExtensions
{
    public static IServiceCollection AddPaymentPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");

        services.AddDbContext<PaymentDbContext>(options =>
            options.UseNpgsql(connectionString));

        return services;
    }
}
