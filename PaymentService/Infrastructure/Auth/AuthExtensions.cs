using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace PaymentService.Infrastructure.Auth;

public static class AuthExtensions
{
    public const string FrontendCorsPolicy = "Frontend";

    public static IServiceCollection AddEntraAuth(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment() && configuration.GetValue<bool>("DevAuth:Enabled"))
        {
            services.AddAuthentication(DevAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, null);
            services.AddAuthorization();
            return services;
        }

        var section = configuration.GetSection("EntraId");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = section["Authority"]
                    ?? throw new InvalidOperationException("'EntraId:Authority' is not configured.");
                options.Audience = section["Audience"]
                    ?? throw new InvalidOperationException("'EntraId:Audience' is not configured.");
                options.MapInboundClaims = false; // keep raw claim names (oid, email, ...)
            });
        services.AddAuthorization();

        return services;
    }

    public static IServiceCollection AddFrontendCors(this IServiceCollection services, IConfiguration configuration)
    {
        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

        services.AddCors(options => options.AddPolicy(FrontendCorsPolicy, policy =>
            policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));

        return services;
    }

    public static Guid GetUserId(this ClaimsPrincipal principal)
    {
        var oid = principal.FindFirstValue("oid")
            ?? principal.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");
        return Guid.TryParse(oid, out var id)
            ? id
            : throw new InvalidOperationException("Token has no valid 'oid' claim.");
    }

    public static string? GetEmail(this ClaimsPrincipal principal) =>
        principal.FindFirstValue("email")
        ?? principal.FindFirstValue("preferred_username")
        ?? principal.FindFirstValue("upn");
}
